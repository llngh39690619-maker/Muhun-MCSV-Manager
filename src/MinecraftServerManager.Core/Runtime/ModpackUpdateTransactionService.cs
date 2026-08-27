using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Replaces installer-owned modpack files in a stopped live server while preserving the live
/// world and operational data. Every rename is described by a durable sibling journal before the
/// first live entry moves. No command shell or downloaded executable is invoked by this service.
/// </summary>
public sealed class ModpackUpdateTransactionService
{
    private const int JournalSchemaVersion = 2;
    private const long MaximumJournalBytes = 1024 * 1024;
    private const int MaximumJournalEntries = 256;
    private const string RollbackContentDirectoryName = "old";

    private static readonly string[] InstallerOwnedDirectoryNames =
    [
        "mods",
        "plugins",
        "config",
        "defaultconfigs",
        "kubejs",
        "scripts",
        "libraries",
        "versions"
    ];

    private static readonly string[] KnownLaunchFileNames =
    [
        "run.bat",
        "run.sh",
        "run.ps1",
        "start.bat",
        "start.sh",
        "start.ps1",
        "startserver.bat",
        "startserver.sh",
        "launch.bat",
        "launch.sh",
        "win_args.txt",
        "unix_args.txt"
    ];

    private static readonly HashSet<string> PreservedTopLevelNames = new(
    [
        "ops.json",
        "whitelist.json",
        "banned-ips.json",
        "banned-players.json",
        "usercache.json",
        "server.properties",
        "eula.txt",
        "user_jvm_args.txt",
        "logs",
        "crash",
        "crash-reports",
        "backups",
        "cache",
        ".mcsv-runtime",
        ServerDirectoryLock.FileName
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BundledRuntimeTopLevelNames = new(
        ["jre", "runtime", ".jre", "java"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 32
    };

    private readonly MinecraftWorldLayoutResolver _worldLayoutResolver;
    private readonly Action<ModpackUpdateFaultPoint, string?>? _faultInjector;

    public ModpackUpdateTransactionService()
        : this(new MinecraftWorldLayoutResolver(), null)
    {
    }

    internal ModpackUpdateTransactionService(
        MinecraftWorldLayoutResolver worldLayoutResolver,
        Action<ModpackUpdateFaultPoint, string?>? faultInjector)
    {
        _worldLayoutResolver = worldLayoutResolver
            ?? throw new ArgumentNullException(nameof(worldLayoutResolver));
        _faultInjector = faultInjector;
    }

    /// <summary>
    /// Checks only the deterministic sibling artifact names for a configured live path. The live
    /// directory is deliberately not opened or validated here, so ordinary missing/reparse legacy
    /// entries do not turn application startup into a recovery failure when no transaction exists.
    /// Once an artifact is present, callers must still use <see cref="RecoverPendingAsync"/>, which
    /// performs the complete existence, reparse-point, identity and journal validation.
    /// </summary>
    public bool HasPendingArtifacts(ServerInstance liveInstance)
    {
        ArgumentNullException.ThrowIfNull(liveInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveInstance.DirectoryPath);
        var liveRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(liveInstance.DirectoryPath));
        if (Path.GetDirectoryName(liveRoot) is null || string.IsNullOrEmpty(Path.GetFileName(liveRoot)))
        {
            return false;
        }

        var paths = CreateTransactionPaths(liveRoot);
        return File.Exists(paths.JournalPath) || Directory.Exists(paths.RollbackRoot);
    }

    /// <summary>
    /// Commits the candidate installation into the stopped live directory. On success the caller
    /// must persist <see cref="ModpackUpdateTransactionResult.LaunchFields"/> and then call
    /// <see cref="AcknowledgeCommitAsync"/>. Keeping the candidate, rollback and committed journal
    /// until that explicit
    /// acknowledgement closes the crash gap between filesystem commit and manager.json commit.
    /// </summary>
    public Task<ModpackUpdateTransactionResult> CommitAsync(
        ServerInstance liveInstance,
        ServerInstance candidateInstance,
        CancellationToken cancellationToken = default)
        => CommitAsync(
            liveInstance,
            candidateInstance,
            beforeFilesystemChanges: null,
            cancellationToken);

    /// <param name="beforeFilesystemChanges">
    /// Optional stopped-server work such as the data-only ZIP backup. It runs while both directory
    /// leases are held, after the transaction plan is validated but before rollback/journal
    /// creation or any rename. A failure leaves no transaction artifact.
    /// </param>
    public async Task<ModpackUpdateTransactionResult> CommitAsync(
        ServerInstance liveInstance,
        ServerInstance candidateInstance,
        Func<CancellationToken, Task>? beforeFilesystemChanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveInstance);
        ArgumentNullException.ThrowIfNull(candidateInstance);
        var roots = ValidateInitialRoots(liveInstance, candidateInstance);
        var transactionPaths = CreateTransactionPaths(roots.LiveRoot);
        if (File.Exists(transactionPaths.JournalPath)
            || Directory.Exists(transactionPaths.RollbackRoot))
        {
            throw new InvalidOperationException(
                "這個 Server 尚有未完成或未確認的模組包更新 journal；請先執行啟動復原。"
            );
        }

        ServerDirectoryLease? liveLease = null;
        ServerDirectoryLease? candidateLease = null;
        JournalPayload? journal = null;
        JournalIdentity? rollbackIdentity = null;
        var journalPublished = false;
        var durableCommit = false;
        try
        {
            (liveLease, candidateLease) = AcquireBothLeases(roots.LiveRoot, roots.CandidateRoot);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRootIdentities(roots);
            SafePath.EnsureTreeContainsNoReparsePoints(roots.LiveRoot);
            SafePath.EnsureTreeContainsNoReparsePoints(roots.CandidateRoot);

            var worldLayout = await _worldLayoutResolver.ResolveAsync(
                    roots.LiveRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            var plan = BuildPlan(liveInstance, candidateInstance, roots, worldLayout);
            var planFingerprint = ComputePlanFingerprint(plan, worldLayout);
            cancellationToken.ThrowIfCancellationRequested();

            if (beforeFilesystemChanges is not null)
            {
                await beforeFilesystemChanges(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // The callback can be long-running. The cross-process leases prevent another
                // manager from starting either server; repeat the no-reparse/identity checks to
                // fail closed if an external actor changed the trees during the backup.
                ValidateRootIdentities(roots);
                SafePath.EnsureTreeContainsNoReparsePoints(roots.LiveRoot);
                SafePath.EnsureTreeContainsNoReparsePoints(roots.CandidateRoot);
                var revalidatedWorldLayout = await _worldLayoutResolver.ResolveAsync(
                        roots.LiveRoot,
                        cancellationToken)
                    .ConfigureAwait(false);
                var revalidatedPlan = BuildPlan(
                    liveInstance,
                    candidateInstance,
                    roots,
                    revalidatedWorldLayout);
                if (!CryptographicOperations.FixedTimeEquals(
                        planFingerprint,
                        ComputePlanFingerprint(revalidatedPlan, revalidatedWorldLayout)))
                {
                    throw new IOException(
                        "備份期間 live/candidate 的世界或替換計畫已改變，拒絕使用可能不一致的備份繼續更新。"
                    );
                }
            }

            // The callback is intentionally allowed to write the backup below the live root, but
            // it must not be able to make us adopt a pre-existing sibling transaction artifact.
            // Repeat this check while both server leases are still held, immediately before this
            // transaction creates any state of its own.
            if (File.Exists(transactionPaths.JournalPath)
                || Directory.Exists(transactionPaths.RollbackRoot))
            {
                throw new IOException(
                    "備份期間出現另一份模組包更新 journal／rollback，拒絕接管其內容。"
                );
            }

            Directory.CreateDirectory(transactionPaths.RollbackRoot);
            SafePath.EnsureTreeContainsNoReparsePoints(transactionPaths.RollbackRoot);
            rollbackIdentity = CaptureIdentity(transactionPaths.RollbackRoot);
            EnsureSameVolume(roots.LiveIdentity, rollbackIdentity, roots.LiveRoot, transactionPaths.RollbackRoot);

            journal = new JournalPayload(
                TransactionId: Guid.NewGuid(),
                Phase: JournalPhase.Prepared,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                LiveRoot: roots.LiveRoot,
                CandidateRoot: roots.CandidateRoot,
                RollbackRoot: transactionPaths.RollbackRoot,
                LiveIdentity: roots.LiveIdentity,
                CandidateIdentity: roots.CandidateIdentity,
                RollbackIdentity: rollbackIdentity,
                LiveEntries: plan.LiveEntries,
                CandidateEntries: plan.CandidateEntries,
                CreatedLiveDirectories: plan.CreatedLiveDirectories,
                LaunchFields: plan.LaunchFields,
                PreviousLaunchFields: plan.PreviousLaunchFields);
            await WriteJournalAsync(
                    transactionPaths.JournalPath,
                    journal,
                    cancellationToken)
                .ConfigureAwait(false);
            journalPublished = true;
            InvokeFault(ModpackUpdateFaultPoint.JournalPrepared, null);
            cancellationToken.ThrowIfCancellationRequested();

            journal = journal with { Phase = JournalPhase.Applying };
            await WriteJournalAsync(
                    transactionPaths.JournalPath,
                    journal,
                    CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var entry in journal.LiveEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = CombineJournalRelativePath(roots.LiveRoot, entry.RelativePath);
                var destination = CombineJournalRelativePath(
                    Path.Combine(transactionPaths.RollbackRoot, RollbackContentDirectoryName),
                    entry.RelativePath);
                MoveEntry(source, destination, entry.IsDirectory);
                InvokeFault(ModpackUpdateFaultPoint.LiveEntryMoved, entry.RelativePath);
                cancellationToken.ThrowIfCancellationRequested();
            }

            CreatePlannedLiveDirectories(roots.LiveRoot, journal.CreatedLiveDirectories);
            foreach (var entry in journal.CandidateEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = CombineJournalRelativePath(roots.CandidateRoot, entry.RelativePath);
                var destination = CombineJournalRelativePath(roots.LiveRoot, entry.RelativePath);
                MoveEntry(source, destination, entry.IsDirectory);
                InvokeFault(ModpackUpdateFaultPoint.CandidateEntryMoved, entry.RelativePath);
                cancellationToken.ThrowIfCancellationRequested();
            }

            ValidateCommittedLaunchFields(journal.LaunchFields);
            cancellationToken.ThrowIfCancellationRequested();
            journal = journal with { Phase = JournalPhase.Committed };
            await WriteJournalAsync(
                    transactionPaths.JournalPath,
                    journal,
                    CancellationToken.None)
                .ConfigureAwait(false);
            InvokeFault(ModpackUpdateFaultPoint.CommitMarked, null);
            // A normal exception from the final fault boundary is still recoverable and must take
            // the automatic rollback path. Only the dedicated simulated-process-loss exception
            // bypasses that path so startup recovery can exercise the committed journal.
            durableCommit = true;

            return new ModpackUpdateTransactionResult(
                journal.TransactionId,
                journal.LaunchFields,
                journal.PreviousLaunchFields,
                CleanupPending: true);
        }
        catch (ModpackUpdateSimulatedCrashException)
        {
            // Test-only equivalent of process termination. The durable journal intentionally
            // remains untouched so a fresh service instance exercises startup recovery.
            throw;
        }
        catch (Exception updateError) when (!durableCommit)
        {
            if (journalPublished && journal is not null)
            {
                try
                {
                    journal = journal with { Phase = JournalPhase.RollingBack };
                    await WriteJournalAsync(
                            transactionPaths.JournalPath,
                            journal,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await RollbackAsync(journal).ConfigureAwait(false);
                    DeleteJournal(transactionPaths.JournalPath);
                }
                catch (Exception rollbackError)
                {
                    throw new ModpackUpdateRollbackException(
                        transactionPaths.JournalPath,
                        updateError,
                        rollbackError);
                }
            }
            else if (Directory.Exists(transactionPaths.RollbackRoot)
                     && (!OperatingSystem.IsWindows() || rollbackIdentity is not null))
            {
                DeleteOwnedDirectory(
                    Path.GetDirectoryName(transactionPaths.RollbackRoot)!,
                    transactionPaths.RollbackRoot,
                    rollbackIdentity,
                    roots.LiveIdentity);
            }

            throw;
        }
        finally
        {
            if (candidateLease is not null)
            {
                await candidateLease.DisposeAsync().ConfigureAwait(false);
            }

            if (liveLease is not null)
            {
                await liveLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Recovers the one fixed journal associated with a live directory. Applying journals are
    /// rolled back. Committed journals return the launch fields again without deleting rollback,
    /// so the caller can persist them and then explicitly acknowledge or revert the commit.
    /// </summary>
    public async Task<ModpackUpdateRecoveryResult> RecoverPendingAsync(
        ServerInstance liveInstance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveInstance.DirectoryPath);
        var liveRoot = NormalizeExistingDirectory(liveInstance.DirectoryPath, "live Server");
        var paths = CreateTransactionPaths(liveRoot);
        if (!File.Exists(paths.JournalPath))
        {
            if (Directory.Exists(paths.RollbackRoot))
            {
                throw new InvalidDataException(
                    "找到更新 rollback 目錄但缺少 durable journal；為避免猜測檔案歸屬，已停止自動復原。"
                );
            }

            return new ModpackUpdateRecoveryResult(ModpackUpdateRecoveryAction.None);
        }

        var journal = await ReadAndValidateJournalAsync(
                paths.JournalPath,
                liveRoot,
                paths.RollbackRoot,
                cancellationToken)
            .ConfigureAwait(false);
        ServerDirectoryLease? liveLease = null;
        ServerDirectoryLease? candidateLease = null;
        try
        {
            liveLease = ServerDirectoryLease.Acquire(liveRoot);
            ValidateExpectedIdentity(liveRoot, journal.LiveIdentity, "live Server");
            SafePath.EnsureTreeContainsNoReparsePoints(liveRoot);

            if (journal.Phase == JournalPhase.Committed)
            {
                if (Directory.Exists(journal.CandidateRoot))
                {
                    ValidateExpectedIdentity(
                        journal.CandidateRoot,
                        journal.CandidateIdentity,
                        "candidate Server");
                    SafePath.EnsureTreeContainsNoReparsePoints(journal.CandidateRoot);
                }

                if (Directory.Exists(journal.RollbackRoot))
                {
                    ValidateExpectedIdentity(
                        journal.RollbackRoot,
                        journal.RollbackIdentity,
                        "rollback");
                    SafePath.EnsureTreeContainsNoReparsePoints(journal.RollbackRoot);
                }

                return new ModpackUpdateRecoveryResult(
                    ModpackUpdateRecoveryAction.CommittedAwaitingAcknowledgement,
                    journal.TransactionId,
                    journal.LaunchFields,
                    journal.PreviousLaunchFields,
                    CleanupPending: Directory.Exists(journal.CandidateRoot)
                                    || Directory.Exists(journal.RollbackRoot));
            }

            if (!Directory.Exists(journal.CandidateRoot))
            {
                throw new DirectoryNotFoundException(
                    $"更新 candidate 資料夾遺失，無法安全復原：{journal.CandidateRoot}");
            }

            ValidateExpectedIdentity(
                journal.CandidateRoot,
                journal.CandidateIdentity,
                "candidate Server");
            candidateLease = ServerDirectoryLease.Acquire(journal.CandidateRoot);
            ValidateExpectedIdentity(
                journal.CandidateRoot,
                journal.CandidateIdentity,
                "candidate Server");
            SafePath.EnsureTreeContainsNoReparsePoints(journal.CandidateRoot);
            if (Directory.Exists(journal.RollbackRoot))
            {
                ValidateExpectedIdentity(journal.RollbackRoot, journal.RollbackIdentity, "rollback");
                SafePath.EnsureTreeContainsNoReparsePoints(journal.RollbackRoot);
            }
            else if (journal.Phase != JournalPhase.RollingBack)
            {
                throw new DirectoryNotFoundException(
                    $"更新 rollback 資料夾遺失，無法安全復原：{journal.RollbackRoot}");
            }

            journal = journal with { Phase = JournalPhase.RollingBack };
            await WriteJournalAsync(paths.JournalPath, journal, CancellationToken.None)
                .ConfigureAwait(false);
            await RollbackAsync(journal).ConfigureAwait(false);
            DeleteJournal(paths.JournalPath);
            return new ModpackUpdateRecoveryResult(
                ModpackUpdateRecoveryAction.RolledBack,
                journal.TransactionId,
                PreviousLaunchFields: journal.PreviousLaunchFields);
        }
        finally
        {
            if (candidateLease is not null)
            {
                await candidateLease.DisposeAsync().ConfigureAwait(false);
            }

            if (liveLease is not null)
            {
                await liveLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Removes a committed journal only after the caller has durably persisted its returned launch
    /// fields. Calling this with an applying/rollback journal is always rejected.
    /// </summary>
    public async Task AcknowledgeCommitAsync(
        ServerInstance liveInstance,
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveInstance);
        var liveRoot = NormalizeExistingDirectory(liveInstance.DirectoryPath, "live Server");
        var paths = CreateTransactionPaths(liveRoot);
        var journal = await ReadAndValidateJournalAsync(
                paths.JournalPath,
                liveRoot,
                paths.RollbackRoot,
                cancellationToken)
            .ConfigureAwait(false);
        if (journal.TransactionId != transactionId || journal.Phase != JournalPhase.Committed)
        {
            throw new InvalidOperationException("更新 journal 並非指定的已提交交易，拒絕確認。");
        }

        await using var liveLease = ServerDirectoryLease.Acquire(liveRoot);
        ServerDirectoryLease? candidateLease = null;
        try
        {
            ValidateExpectedIdentity(liveRoot, journal.LiveIdentity, "live Server");
            if (Directory.Exists(journal.CandidateRoot))
            {
                ValidateExpectedIdentity(
                    journal.CandidateRoot,
                    journal.CandidateIdentity,
                    "candidate Server");
                candidateLease = ServerDirectoryLease.Acquire(journal.CandidateRoot);
                ValidateExpectedIdentity(
                    journal.CandidateRoot,
                    journal.CandidateIdentity,
                    "candidate Server");
                await candidateLease.DisposeAsync().ConfigureAwait(false);
                candidateLease = null;
            }

            var cleanupComplete = await TryFinalizeCommittedCleanupAsync(journal)
                .ConfigureAwait(false);
            if (!cleanupComplete)
            {
                throw new IOException("已提交更新的 candidate／rollback 尚未清理完成，journal 會保留供下次重試。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            DeleteJournal(paths.JournalPath);
        }
        finally
        {
            if (candidateLease is not null)
            {
                await candidateLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Durably records that a committed candidate failed its first launch health check. This
    /// intentionally does not wait for the live process lock: startup recovery can therefore
    /// finish the rollback even if the manager exits in the narrow process-lock handoff window.
    /// Repeating the same request is idempotent.
    /// </summary>
    public async Task RequestCommittedRollbackAsync(
        ServerInstance liveInstance,
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveInstance);
        var liveRoot = NormalizeExistingDirectory(liveInstance.DirectoryPath, "live Server");
        var paths = CreateTransactionPaths(liveRoot);
        var journal = await ReadAndValidateJournalAsync(
                paths.JournalPath,
                liveRoot,
                paths.RollbackRoot,
                cancellationToken)
            .ConfigureAwait(false);
        if (journal.TransactionId != transactionId
            || journal.Phase is not (JournalPhase.Committed or JournalPhase.RollingBack))
        {
            throw new InvalidOperationException(
                "更新 journal 並非指定的已提交／復原中交易，拒絕標記復原。");
        }

        if (!Directory.Exists(journal.CandidateRoot)
            || !Directory.Exists(journal.RollbackRoot))
        {
            throw new InvalidOperationException(
                "candidate 或 rollback 已開始清理，不能再安全標記復原。");
        }

        if (journal.Phase == JournalPhase.RollingBack) return;
        await WriteJournalAsync(
                paths.JournalPath,
                journal with { Phase = JournalPhase.RollingBack },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts a filesystem commit before <see cref="AcknowledgeCommitAsync"/> starts deleting
    /// candidate/rollback data. The rollback intent is durably written before waiting for the live
    /// directory lease, so a process-exit lock handoff or manager crash cannot turn a rejected
    /// first launch back into an apparently valid committed update on the next startup.
    /// </summary>
    public async Task RollbackCommittedAsync(
        ServerInstance liveInstance,
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveInstance);
        var liveRoot = NormalizeExistingDirectory(liveInstance.DirectoryPath, "live Server");
        var paths = CreateTransactionPaths(liveRoot);
        var journal = await ReadAndValidateJournalAsync(
                paths.JournalPath,
                liveRoot,
                paths.RollbackRoot,
                cancellationToken)
            .ConfigureAwait(false);
        if (journal.TransactionId != transactionId
            || journal.Phase is not (JournalPhase.Committed or JournalPhase.RollingBack))
        {
            throw new InvalidOperationException(
                "更新 journal 並非指定的已提交／復原中交易，拒絕復原。");
        }

        if (!Directory.Exists(journal.CandidateRoot)
            || !Directory.Exists(journal.RollbackRoot))
        {
            throw new InvalidOperationException(
                "candidate 或 rollback 已開始清理，不能再安全回復已提交交易。"
            );
        }

        if (journal.Phase == JournalPhase.Committed)
        {
            await RequestCommittedRollbackAsync(liveInstance, transactionId, CancellationToken.None)
                .ConfigureAwait(false);
            journal = journal with { Phase = JournalPhase.RollingBack };
        }

        await using var liveLease = ServerDirectoryLease.Acquire(liveRoot);
        ValidateExpectedIdentity(
            journal.CandidateRoot,
            journal.CandidateIdentity,
            "candidate Server");
        await using var candidateLease = ServerDirectoryLease.Acquire(journal.CandidateRoot);
        ValidateExpectedIdentity(liveRoot, journal.LiveIdentity, "live Server");
        ValidateExpectedIdentity(
            journal.CandidateRoot,
            journal.CandidateIdentity,
            "candidate Server");
        ValidateExpectedIdentity(journal.RollbackRoot, journal.RollbackIdentity, "rollback");
        SafePath.EnsureTreeContainsNoReparsePoints(liveRoot);
        SafePath.EnsureTreeContainsNoReparsePoints(journal.CandidateRoot);
        SafePath.EnsureTreeContainsNoReparsePoints(journal.RollbackRoot);

        await RollbackAsync(journal).ConfigureAwait(false);
        DeleteJournal(paths.JournalPath);
    }

    private TransactionPlan BuildPlan(
        ServerInstance liveInstance,
        ServerInstance candidateInstance,
        ValidatedRoots roots,
        MinecraftWorldLayout worldLayout)
    {
        var comparer = PathComparer;
        var launchPaths = new HashSet<string>(comparer);
        AddLaunchMetadataPaths(liveInstance, roots.LiveRoot, launchPaths, requireCandidateFiles: false);
        AddLaunchMetadataPaths(candidateInstance, roots.CandidateRoot, launchPaths, requireCandidateFiles: true);

        var conditionalRuntimeDirectories = new HashSet<string>(comparer);
        AddBundledRuntimeDirectory(liveInstance.JavaExecutablePath, roots.LiveRoot, conditionalRuntimeDirectories);
        AddBundledRuntimeDirectory(
            candidateInstance.JavaExecutablePath,
            roots.CandidateRoot,
            conditionalRuntimeDirectories);

        var worldPaths = worldLayout.RelativeWorldDirectories
            .Select(NormalizeStrictRelativePath)
            .ToArray();
        foreach (var selected in InstallerOwnedDirectoryNames
                     .Concat(conditionalRuntimeDirectories)
                     .Concat(launchPaths)
                     .Concat(KnownLaunchFileNames))
        {
            EnsureReplaceablePath(selected, worldPaths);
        }

        var liveEntries = BuildExistingEntryPlan(
            roots.LiveRoot,
            InstallerOwnedDirectoryNames
                .Concat(conditionalRuntimeDirectories)
                .Concat(launchPaths)
                .Concat(KnownLaunchFileNames));
        var candidateEntries = BuildExistingEntryPlan(
            roots.CandidateRoot,
            InstallerOwnedDirectoryNames
                .Concat(conditionalRuntimeDirectories)
                .Concat(launchPaths)
                .Concat(KnownLaunchFileNames));
        ValidateCandidateLaunchDependencies(candidateInstance, roots, candidateEntries);
        var createdDirectories = FindRequiredLiveDirectories(
            roots.LiveRoot,
            candidateEntries);
        var launchFields = CreateLaunchFields(candidateInstance, roots);
        var previousLaunchFields = CaptureLiveLaunchFields(liveInstance, roots.LiveRoot);
        return new TransactionPlan(
            liveEntries,
            candidateEntries,
            createdDirectories,
            launchFields,
            previousLaunchFields);
    }

    private static void AddLaunchMetadataPaths(
        ServerInstance instance,
        string root,
        ISet<string> paths,
        bool requireCandidateFiles)
    {
        if (!string.IsNullOrWhiteSpace(instance.ServerJarPath))
        {
            paths.Add(NormalizeModelPath(root, instance.ServerJarPath, nameof(instance.ServerJarPath)));
        }
        else if (requireCandidateFiles && instance.LaunchKind == ServerLaunchKind.ExecutableJar)
        {
            throw new InvalidDataException("candidate 使用 JAR 啟動，但沒有 ServerJarPath。");
        }

        if (!string.IsNullOrWhiteSpace(instance.SourceLaunchScriptPath))
        {
            paths.Add(NormalizeModelPath(
                root,
                instance.SourceLaunchScriptPath,
                nameof(instance.SourceLaunchScriptPath)));
        }

        foreach (var path in instance.JavaArgumentFilePaths ?? [])
        {
            var relative = NormalizeModelPath(root, path, nameof(instance.JavaArgumentFilePaths));
            // This file contains the live administrator's memory/JVM choices and is deliberately
            // preserved. Candidate metadata may keep referring to the same root-relative path.
            if (relative.Equals("user_jvm_args.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            paths.Add(relative);
        }
    }

    private static void AddBundledRuntimeDirectory(
        string? javaExecutablePath,
        string root,
        ISet<string> selectedDirectories)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(
            Path.IsPathFullyQualified(javaExecutablePath)
                ? javaExecutablePath
                : Path.Combine(root, javaExecutablePath));
        if (!SafePath.IsWithinRoot(root, fullPath))
        {
            return;
        }

        var relative = NormalizeStrictRelativePath(Path.GetRelativePath(root, fullPath));
        var topLevel = relative.Split('/', 2)[0];
        if (!BundledRuntimeTopLevelNames.Contains(topLevel))
        {
            throw new InvalidDataException(
                $"內嵌 Java 位於未核准的 candidate 目錄「{topLevel}」，無法確認完整 runtime 邊界。"
            );
        }

        selectedDirectories.Add(topLevel);
    }

    private static IReadOnlyList<JournalEntry> BuildExistingEntryPlan(
        string root,
        IEnumerable<string> requestedPaths)
    {
        var comparer = PathComparer;
        var discovered = new Dictionary<string, JournalEntry>(comparer);
        foreach (var requested in requestedPaths)
        {
            var relative = NormalizeStrictRelativePath(requested);
            var fullPath = CombineJournalRelativePath(root, relative);
            var isDirectory = Directory.Exists(fullPath);
            var isFile = File.Exists(fullPath);
            if (!isDirectory && !isFile)
            {
                continue;
            }

            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException($"更新項目不得是 reparse point：{fullPath}");
            }

            discovered[relative] = new JournalEntry(relative, isDirectory);
        }

        var result = new List<JournalEntry>();
        foreach (var entry in discovered.Values
                     .OrderBy(static item => PathDepth(item.RelativePath))
                     .ThenBy(static item => item.RelativePath, comparer))
        {
            if (result.Any(parent => parent.IsDirectory
                                     && IsSameOrDescendant(parent.RelativePath, entry.RelativePath)))
            {
                continue;
            }

            result.Add(entry);
        }

        if (result.Count > MaximumJournalEntries)
        {
            throw new InvalidDataException("更新項目超過安全數量上限。");
        }

        return result;
    }

    private static IReadOnlyList<string> FindRequiredLiveDirectories(
        string liveRoot,
        IReadOnlyList<JournalEntry> candidateEntries)
    {
        var comparer = PathComparer;
        var created = new HashSet<string>(comparer);
        foreach (var entry in candidateEntries)
        {
            var segments = entry.RelativePath.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                var relativeParent = string.Join('/', segments.Take(index));
                var fullParent = CombineJournalRelativePath(liveRoot, relativeParent);
                if (File.Exists(fullParent) && !Directory.Exists(fullParent))
                {
                    throw new IOException(
                        $"candidate 啟動檔的目的父路徑被 live 檔案占用：{relativeParent}");
                }

                if (!Directory.Exists(fullParent))
                {
                    created.Add(relativeParent);
                }
            }
        }

        return created
            .OrderBy(static path => PathDepth(path))
            .ThenBy(static path => path, comparer)
            .ToArray();
    }

    private static void ValidateCandidateLaunchDependencies(
        ServerInstance candidate,
        ValidatedRoots roots,
        IReadOnlyList<JournalEntry> candidateEntries)
    {
        if (candidate.LaunchKind == ServerLaunchKind.ExecutableJar)
        {
            var jar = NormalizeModelPath(
                roots.CandidateRoot,
                candidate.ServerJarPath,
                nameof(candidate.ServerJarPath));
            RequireCandidateFileOrSelectedAncestor(roots.CandidateRoot, jar, candidateEntries);
        }
        else if (candidate.LaunchKind == ServerLaunchKind.JavaArgumentFiles)
        {
            if (candidate.JavaArgumentFilePaths is not { Count: > 0 })
            {
                throw new InvalidDataException("candidate 缺少 Java argument-file 啟動資料。");
            }

            foreach (var path in candidate.JavaArgumentFilePaths)
            {
                var relative = NormalizeModelPath(
                    roots.CandidateRoot,
                    path,
                    nameof(candidate.JavaArgumentFilePaths));
                if (relative.Equals("user_jvm_args.txt", StringComparison.OrdinalIgnoreCase))
                {
                    var liveJvmArgs = CombineJournalRelativePath(roots.LiveRoot, relative);
                    if (!File.Exists(liveJvmArgs))
                    {
                        throw new InvalidDataException(
                            "candidate 啟動需要 user_jvm_args.txt，但 live Server 沒有可保留的版本。"
                        );
                    }

                    continue;
                }

                RequireCandidateFileOrSelectedAncestor(
                    roots.CandidateRoot,
                    relative,
                    candidateEntries);
            }
        }
        else
        {
            throw new InvalidDataException($"不支援的 candidate 啟動模式：{candidate.LaunchKind}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.SourceLaunchScriptPath))
        {
            var script = NormalizeModelPath(
                roots.CandidateRoot,
                candidate.SourceLaunchScriptPath,
                nameof(candidate.SourceLaunchScriptPath));
            RequireCandidateFileOrSelectedAncestor(roots.CandidateRoot, script, candidateEntries);
        }

        if (!string.IsNullOrWhiteSpace(candidate.JavaExecutablePath))
        {
            var fullJavaPath = Path.GetFullPath(
                Path.IsPathFullyQualified(candidate.JavaExecutablePath)
                    ? candidate.JavaExecutablePath
                    : Path.Combine(roots.CandidateRoot, candidate.JavaExecutablePath));
            if (SafePath.IsWithinRoot(roots.CandidateRoot, fullJavaPath))
            {
                var java = NormalizeStrictRelativePath(
                    Path.GetRelativePath(roots.CandidateRoot, fullJavaPath));
                RequireCandidateFileOrSelectedAncestor(
                    roots.CandidateRoot,
                    java,
                    candidateEntries);
            }
            else if (SafePath.IsWithinRoot(roots.LiveRoot, fullJavaPath))
            {
                throw new InvalidDataException(
                    "candidate Java 不得借用 live Server 內部路徑；無法確認更新後的 runtime 歸屬。"
                );
            }
            else if (!File.Exists(fullJavaPath))
            {
                throw new FileNotFoundException("candidate 指定的外部 Java 不存在。", fullJavaPath);
            }
        }
    }

    private static void RequireCandidateFileOrSelectedAncestor(
        string candidateRoot,
        string relativePath,
        IReadOnlyList<JournalEntry> candidateEntries)
    {
        var fullPath = CombineJournalRelativePath(candidateRoot, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("candidate 啟動相依檔案不存在。", fullPath);
        }

        if (!candidateEntries.Any(entry => entry.RelativePath.Equals(
                    relativePath,
                    PathComparison)
                || entry.IsDirectory && IsSameOrDescendant(entry.RelativePath, relativePath)))
        {
            throw new InvalidDataException(
                $"candidate 啟動相依檔案不在核准的替換集合內：{relativePath}");
        }
    }

    private static ModpackUpdateLaunchFields CreateLaunchFields(
        ServerInstance candidate,
        ValidatedRoots roots)
    {
        var serverJar = string.IsNullOrWhiteSpace(candidate.ServerJarPath)
            ? string.Empty
            : RemapCandidatePathToLive(
                roots,
                candidate.ServerJarPath,
                nameof(candidate.ServerJarPath));
        var sourceScript = string.IsNullOrWhiteSpace(candidate.SourceLaunchScriptPath)
            ? null
            : RemapCandidatePathToLive(
                roots,
                candidate.SourceLaunchScriptPath,
                nameof(candidate.SourceLaunchScriptPath));
        string? javaExecutable = candidate.JavaExecutablePath;
        if (!string.IsNullOrWhiteSpace(javaExecutable))
        {
            var candidateJava = Path.GetFullPath(
                Path.IsPathFullyQualified(javaExecutable)
                    ? javaExecutable
                    : Path.Combine(roots.CandidateRoot, javaExecutable));
            javaExecutable = SafePath.IsWithinRoot(roots.CandidateRoot, candidateJava)
                ? Path.Combine(
                    roots.LiveRoot,
                    Path.GetRelativePath(roots.CandidateRoot, candidateJava))
                : candidateJava;
        }

        return new ModpackUpdateLaunchFields(
            roots.LiveRoot,
            serverJar,
            candidate.LaunchKind,
            (candidate.JavaArgumentFilePaths ?? [])
                .Select(path => NormalizeModelPath(
                    roots.CandidateRoot,
                    path,
                    nameof(candidate.JavaArgumentFilePaths)))
                .ToArray(),
            sourceScript,
            candidate.CoreType,
            candidate.MinecraftVersion,
            candidate.JavaMajorVersion,
            javaExecutable,
            candidate.ServerArguments is null ? [] : [.. candidate.ServerArguments],
            candidate.ModpackSource,
            candidate.ModpackProjectId,
            candidate.ModpackVersionId,
            candidate.ModpackVersionName,
            candidate.IsInstallerArtifact);
    }

    private static ModpackUpdateLaunchFields CaptureLiveLaunchFields(
        ServerInstance live,
        string liveRoot)
    {
        ArgumentNullException.ThrowIfNull(live);
        return new ModpackUpdateLaunchFields(
            liveRoot,
            live.ServerJarPath,
            live.LaunchKind,
            live.JavaArgumentFilePaths is null ? [] : [.. live.JavaArgumentFilePaths],
            live.SourceLaunchScriptPath,
            live.CoreType,
            live.MinecraftVersion,
            live.JavaMajorVersion,
            live.JavaExecutablePath,
            live.ServerArguments is null ? [] : [.. live.ServerArguments],
            live.ModpackSource,
            live.ModpackProjectId,
            live.ModpackVersionId,
            live.ModpackVersionName,
            live.IsInstallerArtifact);
    }

    private static string RemapCandidatePathToLive(
        ValidatedRoots roots,
        string candidatePath,
        string fieldName)
    {
        var relative = NormalizeModelPath(roots.CandidateRoot, candidatePath, fieldName);
        return CombineJournalRelativePath(roots.LiveRoot, relative);
    }

    private static void EnsureReplaceablePath(
        string relativePath,
        IReadOnlyList<string> liveWorldPaths)
    {
        var normalized = NormalizeStrictRelativePath(relativePath);
        var topLevel = normalized.Split('/', 2)[0];
        if (PreservedTopLevelNames.Contains(topLevel))
        {
            throw new InvalidDataException(
                $"更新啟動／載入器路徑「{normalized}」與必須保留的 live 資料衝突。"
            );
        }

        if (liveWorldPaths.Any(world => PathsOverlap(world, normalized)))
        {
            throw new InvalidDataException(
                $"更新替換路徑「{normalized}」與 live 世界路徑衝突，已拒絕交易。"
            );
        }
    }

    private static void CreatePlannedLiveDirectories(
        string liveRoot,
        IReadOnlyList<string> relativeDirectories)
    {
        foreach (var relative in relativeDirectories)
        {
            var path = CombineJournalRelativePath(liveRoot, relative);
            if (File.Exists(path) && !Directory.Exists(path))
            {
                throw new IOException($"無法建立 candidate 目的資料夾，路徑已被檔案占用：{path}");
            }

            Directory.CreateDirectory(path);
            SafePath.EnsureNoReparsePointsUnderRoot(liveRoot, path);
        }
    }

    private static void MoveEntry(string source, string destination, bool isDirectory)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"交易目的路徑已存在，拒絕覆蓋：{destination}");
        }

        if (isDirectory)
        {
            if (!Directory.Exists(source) || File.Exists(source))
            {
                throw new DirectoryNotFoundException($"交易來源資料夾遺失：{source}");
            }

            SafePath.EnsureTreeContainsNoReparsePoints(source);
        }
        else
        {
            if (!File.Exists(source) || Directory.Exists(source))
            {
                throw new FileNotFoundException("交易來源檔案遺失。", source);
            }

            if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException($"交易來源檔案不得是 reparse point：{source}");
            }
        }

        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("交易目的路徑缺少父資料夾。");
        Directory.CreateDirectory(destinationParent);
        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: false);
        }
    }

    private static async Task RollbackAsync(JournalPayload journal)
    {
        ValidateJournalEntries(journal);
        var oldByPath = journal.LiveEntries.ToDictionary(
            static entry => entry.RelativePath,
            PathComparer);
        foreach (var entry in journal.CandidateEntries.Reverse())
        {
            var candidatePath = CombineJournalRelativePath(
                journal.CandidateRoot,
                entry.RelativePath);
            var livePath = CombineJournalRelativePath(journal.LiveRoot, entry.RelativePath);
            var candidateExists = EntryExists(candidatePath, entry.IsDirectory);
            var liveExists = EntryExists(livePath, entry.IsDirectory);
            var rollbackOldExists = oldByPath.TryGetValue(entry.RelativePath, out var oldEntry)
                                    && EntryExists(
                                        CombineJournalRelativePath(
                                            Path.Combine(
                                                journal.RollbackRoot,
                                                RollbackContentDirectoryName),
                                            entry.RelativePath),
                                        oldEntry.IsDirectory);

            if (!candidateExists && liveExists)
            {
                MoveEntry(livePath, candidatePath, entry.IsDirectory);
            }
            else if (!candidateExists && !liveExists)
            {
                throw new InvalidDataException(
                    $"復原時 candidate 與 live 更新項目同時遺失：{entry.RelativePath}");
            }
            else if (candidateExists && liveExists && rollbackOldExists)
            {
                throw new InvalidDataException(
                    $"復原時偵測到無法判定歸屬的重複更新項目：{entry.RelativePath}");
            }
        }

        foreach (var entry in journal.LiveEntries.Reverse())
        {
            var rollbackPath = CombineJournalRelativePath(
                Path.Combine(journal.RollbackRoot, RollbackContentDirectoryName),
                entry.RelativePath);
            var livePath = CombineJournalRelativePath(journal.LiveRoot, entry.RelativePath);
            var rollbackExists = EntryExists(rollbackPath, entry.IsDirectory);
            var liveExists = EntryExists(livePath, entry.IsDirectory);
            if (rollbackExists && !liveExists)
            {
                MoveEntry(rollbackPath, livePath, entry.IsDirectory);
            }
            else if (rollbackExists && liveExists)
            {
                throw new InvalidDataException(
                    $"復原時 rollback 與 live 舊項目同時存在：{entry.RelativePath}");
            }
            else if (!rollbackExists && !liveExists)
            {
                throw new InvalidDataException(
                    $"復原時 live 舊項目與 rollback 同時遺失：{entry.RelativePath}");
            }
        }

        foreach (var relative in journal.CreatedLiveDirectories
                     .OrderByDescending(static path => PathDepth(path)))
        {
            var path = CombineJournalRelativePath(journal.LiveRoot, relative);
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }

        if (Directory.Exists(journal.RollbackRoot))
        {
            DeleteOwnedDirectory(
                Path.GetDirectoryName(journal.RollbackRoot)!,
                journal.RollbackRoot,
                journal.RollbackIdentity,
                journal.LiveIdentity,
                journal.CandidateIdentity);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task<bool> TryFinalizeCommittedCleanupAsync(JournalPayload journal)
    {
        try
        {
            if (Directory.Exists(journal.CandidateRoot))
            {
                DeleteOwnedDirectory(
                    Path.GetDirectoryName(journal.CandidateRoot)!,
                    journal.CandidateRoot,
                    journal.CandidateIdentity,
                    journal.LiveIdentity,
                    journal.RollbackIdentity);
            }

            if (Directory.Exists(journal.RollbackRoot))
            {
                DeleteOwnedDirectory(
                    Path.GetDirectoryName(journal.RollbackRoot)!,
                    journal.RollbackRoot,
                    journal.RollbackIdentity,
                    journal.LiveIdentity,
                    journal.CandidateIdentity);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static void DeleteOwnedDirectory(
        string trustedParent,
        string ownedDirectory,
        JournalIdentity? expectedIdentity,
        params JournalIdentity?[] protectedIdentities)
    {
        if (!Directory.Exists(ownedDirectory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            if (expectedIdentity is null)
            {
                throw new InvalidDataException("Windows 清理缺少預期 filesystem identity。");
            }

            var protectedObjects = protectedIdentities
                .Where(static identity => identity is not null)
                .Select(static identity => identity!.ToSafePathIdentity())
                .ToHashSet();
            SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    trustedParent,
                    ownedDirectory,
                    expectedIdentity.ToSafePathIdentity(),
                    protectedObjects,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return;
        }

        SafePath.DeleteTreeWithoutFollowingReparsePoints(trustedParent, ownedDirectory);
    }

    private static void ValidateCommittedLaunchFields(ModpackUpdateLaunchFields fields)
    {
        if (fields.LaunchKind == ServerLaunchKind.ExecutableJar)
        {
            if (string.IsNullOrWhiteSpace(fields.ServerJarPath)
                || !File.Exists(fields.ServerJarPath))
            {
                throw new FileNotFoundException(
                    "更新後找不到 candidate Server JAR。",
                    fields.ServerJarPath);
            }
        }
        else
        {
            foreach (var relative in fields.JavaArgumentFilePaths)
            {
                var path = CombineJournalRelativePath(fields.LiveDirectoryPath, relative);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("更新後找不到 Java argument-file。", path);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(fields.SourceLaunchScriptPath)
            && !File.Exists(fields.SourceLaunchScriptPath))
        {
            throw new FileNotFoundException(
                "更新後找不到來源啟動檔。",
                fields.SourceLaunchScriptPath);
        }

        if (!string.IsNullOrWhiteSpace(fields.JavaExecutablePath)
            && !File.Exists(fields.JavaExecutablePath))
        {
            throw new FileNotFoundException(
                "更新後找不到 candidate 指定的 Java。",
                fields.JavaExecutablePath);
        }
    }

    private static ValidatedRoots ValidateInitialRoots(
        ServerInstance liveInstance,
        ServerInstance candidateInstance)
    {
        var liveRoot = NormalizeExistingDirectory(liveInstance.DirectoryPath, "live Server");
        var candidateRoot = NormalizeExistingDirectory(
            candidateInstance.DirectoryPath,
            "candidate Server");
        if (PathsEqual(liveRoot, candidateRoot)
            || SafePath.IsWithinRoot(liveRoot, candidateRoot)
            || SafePath.IsWithinRoot(candidateRoot, liveRoot))
        {
            throw new InvalidOperationException(
                "live 與 candidate 必須是兩個互不包含的獨立實體資料夾。"
            );
        }

        SafePath.EnsureTreeContainsNoReparsePoints(liveRoot);
        SafePath.EnsureTreeContainsNoReparsePoints(candidateRoot);
        var liveIdentity = CaptureIdentity(liveRoot);
        var candidateIdentity = CaptureIdentity(candidateRoot);
        if (liveIdentity is not null && liveIdentity == candidateIdentity)
        {
            throw new InvalidOperationException("live 與 candidate 指向相同 filesystem object。");
        }

        EnsureSameVolume(liveIdentity, candidateIdentity, liveRoot, candidateRoot);
        return new ValidatedRoots(
            liveRoot,
            candidateRoot,
            liveIdentity,
            candidateIdentity);
    }

    private static void ValidateRootIdentities(ValidatedRoots roots)
    {
        ValidateExpectedIdentity(roots.LiveRoot, roots.LiveIdentity, "live Server");
        ValidateExpectedIdentity(roots.CandidateRoot, roots.CandidateIdentity, "candidate Server");
    }

    private static (ServerDirectoryLease Live, ServerDirectoryLease Candidate) AcquireBothLeases(
        string liveRoot,
        string candidateRoot)
    {
        ServerDirectoryLease? first = null;
        try
        {
            if (string.Compare(liveRoot, candidateRoot, PathComparison) <= 0)
            {
                first = ServerDirectoryLease.Acquire(liveRoot);
                var second = ServerDirectoryLease.Acquire(candidateRoot);
                return (first, second);
            }

            first = ServerDirectoryLease.Acquire(candidateRoot);
            var live = ServerDirectoryLease.Acquire(liveRoot);
            return (live, first);
        }
        catch
        {
            first?.Dispose();
            throw;
        }
    }

    private static string NormalizeExistingDirectory(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"找不到 {description} 資料夾：{fullPath}");
        }

        if (Path.GetDirectoryName(fullPath) is null || string.IsNullOrEmpty(Path.GetFileName(fullPath)))
        {
            throw new InvalidOperationException($"{description} 不得使用磁碟根目錄。");
        }

        if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"{description} 根目錄不得是 reparse point。");
        }

        return fullPath;
    }

    private static JournalIdentity? CaptureIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var identity = SafePath.GetExistingObjectIdentity(path);
        return new JournalIdentity(identity.VolumeSerialNumber, identity.FileId);
    }

    private static void ValidateExpectedIdentity(
        string path,
        JournalIdentity? expected,
        string description)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{description} 資料夾遺失：{path}");
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"{description} 不得是 reparse point：{path}");
        }

        if (OperatingSystem.IsWindows())
        {
            if (expected is null || CaptureIdentity(path) != expected)
            {
                throw new UnauthorizedAccessException(
                    $"{description} filesystem identity 已改變，拒絕繼續交易：{path}");
            }
        }
    }

    private static void EnsureSameVolume(
        JournalIdentity? leftIdentity,
        JournalIdentity? rightIdentity,
        string leftPath,
        string rightPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (leftIdentity is null
                || rightIdentity is null
                || leftIdentity.VolumeSerialNumber != rightIdentity.VolumeSerialNumber)
            {
                throw new InvalidOperationException("live、candidate 與 rollback 必須位於同一個實體磁碟區。");
            }

            return;
        }

        if (!string.Equals(
                Path.GetPathRoot(leftPath),
                Path.GetPathRoot(rightPath),
                PathComparison))
        {
            throw new InvalidOperationException("live、candidate 與 rollback 必須位於同一個磁碟區。");
        }
    }

    private static TransactionPaths CreateTransactionPaths(string liveRoot)
    {
        var normalized = OperatingSystem.IsWindows()
            ? liveRoot.ToUpperInvariant()
            : liveRoot;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16]
            .ToLowerInvariant();
        var parent = Path.GetDirectoryName(liveRoot)
            ?? throw new InvalidOperationException("live Server 缺少父資料夾。");
        return new TransactionPaths(
            Path.Combine(parent, $".mcsv-modpack-update-{hash}.journal.json"),
            Path.Combine(parent, $".mcsv-modpack-update-{hash}.rollback"));
    }

    private static async Task WriteJournalAsync(
        string journalPath,
        JournalPayload journal,
        CancellationToken cancellationToken)
    {
        ValidateJournalEntries(journal);
        if (File.Exists(journalPath)
            && File.GetAttributes(journalPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("更新 journal 不得是 reparse point。");
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJsonOptions);
        var envelope = new JournalEnvelope(
            JournalSchemaVersion,
            journal,
            Convert.ToHexString(SHA256.HashData(payloadBytes)));
        var temporary = journalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        envelope,
                        JournalJsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, journalPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<JournalPayload> ReadAndValidateJournalAsync(
        string journalPath,
        string expectedLiveRoot,
        string expectedRollbackRoot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
        {
            throw new FileNotFoundException("找不到待確認的更新 journal。", journalPath);
        }

        var info = new FileInfo(journalPath);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || info.Length is < 2 or > MaximumJournalBytes)
        {
            throw new InvalidDataException("更新 journal 的類型或大小無效。");
        }

        JournalEnvelope envelope;
        await using (var stream = new FileStream(
                         journalPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            try
            {
                envelope = await JsonSerializer.DeserializeAsync<JournalEnvelope>(
                        stream,
                        JournalJsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException("更新 journal 是空的。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("更新 journal JSON 無效。", exception);
            }
        }

        if (envelope.SchemaVersion != JournalSchemaVersion || envelope.Payload is null)
        {
            throw new InvalidDataException("更新 journal schema 不受支援。");
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, JournalJsonOptions);
        var expectedHash = SHA256.HashData(payloadBytes);
        byte[] actualHash;
        try
        {
            actualHash = Convert.FromHexString(envelope.Sha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("更新 journal checksum 格式無效。", exception);
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException("更新 journal checksum 不符，拒絕猜測交易狀態。");
        }

        var journal = envelope.Payload;
        if (!PathsEqual(journal.LiveRoot, expectedLiveRoot)
            || !PathsEqual(journal.RollbackRoot, expectedRollbackRoot)
            || PathsEqual(journal.LiveRoot, journal.CandidateRoot)
            || SafePath.IsWithinRoot(journal.LiveRoot, journal.CandidateRoot)
            || SafePath.IsWithinRoot(journal.CandidateRoot, journal.LiveRoot))
        {
            throw new InvalidDataException("更新 journal 的根目錄邊界無效。");
        }

        ValidateJournalEntries(journal);
        return journal;
    }

    private static void ValidateJournalEntries(JournalPayload journal)
    {
        if (journal.TransactionId == Guid.Empty
            || journal.LiveEntries is null
            || journal.CandidateEntries is null
            || journal.CreatedLiveDirectories is null
            || journal.LaunchFields is null
            || journal.PreviousLaunchFields is null
            || journal.LiveEntries.Count > MaximumJournalEntries
            || journal.CandidateEntries.Count > MaximumJournalEntries
            || journal.CreatedLiveDirectories.Count > MaximumJournalEntries)
        {
            throw new InvalidDataException("更新 journal 欄位無效或超過安全上限。");
        }

        ValidateEntrySet(journal.LiveEntries, "live");
        ValidateEntrySet(journal.CandidateEntries, "candidate");
        var directories = new HashSet<string>(PathComparer);
        foreach (var path in journal.CreatedLiveDirectories)
        {
            var normalized = NormalizeStrictRelativePath(path);
            if (!directories.Add(normalized))
            {
                throw new InvalidDataException("更新 journal 含有重複的建立資料夾項目。");
            }
        }

        if (!PathsEqual(journal.LaunchFields.LiveDirectoryPath, journal.LiveRoot))
        {
            throw new InvalidDataException("更新 journal 的啟動欄位不屬於 live 根目錄。");
        }

        ValidateJournalLaunchFields(journal.LaunchFields);
        if (!PathsEqual(journal.PreviousLaunchFields.LiveDirectoryPath, journal.LiveRoot))
        {
            throw new InvalidDataException("更新 journal 的舊版啟動欄位不屬於 live 根目錄。");
        }

        ValidateJournalLaunchFields(journal.PreviousLaunchFields);
    }

    private static void ValidateJournalLaunchFields(ModpackUpdateLaunchFields fields)
    {
        if (fields.LaunchKind is not (ServerLaunchKind.ExecutableJar
            or ServerLaunchKind.JavaArgumentFiles))
        {
            throw new InvalidDataException("更新 journal 含有不支援的啟動模式。");
        }

        if (fields.JavaArgumentFilePaths is null
            || fields.ServerArguments is null
            || fields.JavaArgumentFilePaths.Count > 64
            || fields.ServerArguments.Count > 128)
        {
            throw new InvalidDataException("更新 journal 的啟動參數數量無效。");
        }

        if (fields.LaunchKind == ServerLaunchKind.ExecutableJar)
        {
            ValidateAbsoluteLivePath(
                fields.LiveDirectoryPath,
                fields.ServerJarPath,
                "ServerJarPath");
        }
        else if (!string.IsNullOrEmpty(fields.ServerJarPath))
        {
            throw new InvalidDataException("argument-file 更新 journal 不得包含 ServerJarPath。");
        }

        foreach (var path in fields.JavaArgumentFilePaths)
        {
            if (!NormalizeStrictRelativePath(path).Equals(path, StringComparison.Ordinal))
            {
                throw new InvalidDataException("更新 journal 的 Java argument-file 路徑未正規化。");
            }
        }

        if (!string.IsNullOrWhiteSpace(fields.SourceLaunchScriptPath))
        {
            ValidateAbsoluteLivePath(
                fields.LiveDirectoryPath,
                fields.SourceLaunchScriptPath,
                "SourceLaunchScriptPath");
        }

        foreach (var argument in fields.ServerArguments)
        {
            if (argument is null
                || argument.Length > 8192
                || argument.Contains('\0')
                || argument.Contains('\r')
                || argument.Contains('\n'))
            {
                throw new InvalidDataException("更新 journal 含有無效的 Server 啟動參數。");
            }
        }

        foreach (var value in new[]
                 {
                     fields.MinecraftVersion,
                     fields.ModpackProjectId,
                     fields.ModpackVersionId,
                     fields.ModpackVersionName
                 })
        {
            if (value?.Length > 1024)
            {
                throw new InvalidDataException("更新 journal 的版本／來源欄位過長。");
            }
        }

        static void ValidateAbsoluteLivePath(string root, string path, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !Path.IsPathFullyQualified(path)
                || !SafePath.IsWithinRoot(root, path)
                || PathsEqual(root, path))
            {
                throw new InvalidDataException($"更新 journal 的 {fieldName} 不在 live 根目錄內。");
            }
        }
    }

    private static void ValidateEntrySet(IReadOnlyList<JournalEntry> entries, string description)
    {
        var paths = new HashSet<string>(PathComparer);
        foreach (var entry in entries)
        {
            var normalized = NormalizeStrictRelativePath(entry.RelativePath);
            if (!paths.Add(normalized))
            {
                throw new InvalidDataException($"更新 journal 含有重複的 {description} 項目。");
            }
        }

        foreach (var directory in entries.Where(static entry => entry.IsDirectory))
        {
            if (entries.Any(other => !ReferenceEquals(directory, other)
                                     && !directory.RelativePath.Equals(
                                         other.RelativePath,
                                         PathComparison)
                                     && IsSameOrDescendant(
                                         directory.RelativePath,
                                         other.RelativePath)))
            {
                throw new InvalidDataException(
                    $"更新 journal 的 {description} 項目彼此重疊。");
            }
        }
    }

    private static string NormalizeModelPath(string root, string path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\0')
            || path.Contains('\r')
            || path.Contains('\n')
            || path.StartsWith('@'))
        {
            throw new InvalidDataException($"candidate {fieldName} 含有無效路徑。");
        }

        string relative;
        if (Path.IsPathFullyQualified(path))
        {
            var fullPath = Path.GetFullPath(path);
            if (!SafePath.IsWithinRoot(root, fullPath) || PathsEqual(root, fullPath))
            {
                throw new InvalidDataException($"candidate {fieldName} 必須位於 candidate 根目錄內。");
            }

            relative = Path.GetRelativePath(root, fullPath);
        }
        else
        {
            relative = path;
        }

        return NormalizeStrictRelativePath(relative);
    }

    private static string NormalizeStrictRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains(':')
            || path.Contains('\0')
            || path.Contains('\r')
            || path.Contains('\n'))
        {
            throw new InvalidDataException($"更新交易路徑必須是安全的 root-relative 路徑：{path}");
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment =>
                segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')))
        {
            throw new InvalidDataException($"更新交易路徑含有不安全或模糊片段：{path}");
        }

        return string.Join('/', segments);
    }

    private static string CombineJournalRelativePath(string root, string relativePath)
    {
        var normalized = NormalizeStrictRelativePath(relativePath);
        return SafePath.EnsureWithinRoot(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar),
            allowRoot: false);
    }

    private static bool EntryExists(string path, bool isDirectory)
        => isDirectory ? Directory.Exists(path) : File.Exists(path);

    private static bool PathsOverlap(string left, string right)
        => IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    private static bool IsSameOrDescendant(string parent, string candidate)
        => candidate.Equals(parent, PathComparison)
           || candidate.StartsWith(parent + '/', PathComparison);

    private static int PathDepth(string path) => path.Count(static character => character == '/');

    private static byte[] ComputePlanFingerprint(
        TransactionPlan plan,
        MinecraftWorldLayout worldLayout)
        => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new PlanFingerprint(plan, worldLayout),
            JournalJsonOptions));

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void DeleteJournal(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return;
        }

        if (File.GetAttributes(journalPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("拒絕刪除被替換成 reparse point 的更新 journal。");
        }

        File.Delete(journalPath);
    }

    private void InvokeFault(ModpackUpdateFaultPoint point, string? relativePath)
        => _faultInjector?.Invoke(point, relativePath);

    private enum JournalPhase
    {
        Prepared,
        Applying,
        RollingBack,
        Committed,
    }

    private sealed record JournalEntry(string RelativePath, bool IsDirectory);

    private sealed record JournalIdentity(ulong VolumeSerialNumber, Guid FileId)
    {
        public SafePathObjectIdentity ToSafePathIdentity() => new(VolumeSerialNumber, FileId);
    }

    private sealed record JournalPayload(
        Guid TransactionId,
        JournalPhase Phase,
        DateTimeOffset CreatedAtUtc,
        string LiveRoot,
        string CandidateRoot,
        string RollbackRoot,
        JournalIdentity? LiveIdentity,
        JournalIdentity? CandidateIdentity,
        JournalIdentity? RollbackIdentity,
        IReadOnlyList<JournalEntry> LiveEntries,
        IReadOnlyList<JournalEntry> CandidateEntries,
        IReadOnlyList<string> CreatedLiveDirectories,
        ModpackUpdateLaunchFields LaunchFields,
        ModpackUpdateLaunchFields PreviousLaunchFields);

    private sealed record JournalEnvelope(
        int SchemaVersion,
        JournalPayload Payload,
        string Sha256);

    private sealed record TransactionPlan(
        IReadOnlyList<JournalEntry> LiveEntries,
        IReadOnlyList<JournalEntry> CandidateEntries,
        IReadOnlyList<string> CreatedLiveDirectories,
        ModpackUpdateLaunchFields LaunchFields,
        ModpackUpdateLaunchFields PreviousLaunchFields);

    private sealed record ValidatedRoots(
        string LiveRoot,
        string CandidateRoot,
        JournalIdentity? LiveIdentity,
        JournalIdentity? CandidateIdentity);

    private sealed record TransactionPaths(string JournalPath, string RollbackRoot);

    private sealed record PlanFingerprint(
        TransactionPlan Plan,
        MinecraftWorldLayout WorldLayout);
}
