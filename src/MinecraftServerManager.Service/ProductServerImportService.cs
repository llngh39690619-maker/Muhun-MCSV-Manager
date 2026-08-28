using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

internal interface IProductImportDiskSpaceProbe
{
    long GetAvailableBytes(string path);
}

internal sealed class ProductImportDiskSpaceProbe : IProductImportDiskSpaceProbe
{
    public long GetAvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("The import destination volume could not be resolved.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}

/// <summary>
/// Transactional bridge from the installer-authorized imports tree into Service-owned server and
/// runtime trees. The Service never receives or opens a client source path.
/// </summary>
public sealed class ProductServerImportService : IAsyncDisposable
{
    public const string ManifestFileName = "manifest.v1.json";
    internal const int MaximumManifestFiles = 100_000;
    internal const long MaximumManifestBytes = 32L * 1024 * 1024;
    internal const long MaximumImportBytes = 1024L * 1024 * 1024 * 1024;
    private const long DiskSafetyMarginBytes = 64L * 1024 * 1024;
    internal const int DirectoryMoveMaximumAttempts = 4;
    internal const int SameProcessResumeMaximumAttempts = 3;
    private static readonly TimeSpan[] DirectoryMoveRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
    ];
    private static readonly TimeSpan[] SameProcessResumeRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
    ];
    private readonly ProductDataLayout _layout;
    private readonly ProductServerRegistry _registry;
    private readonly ProductServerRuntime _runtime;
    private readonly IProductImportDiskSpaceProbe _diskSpace;
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private readonly SemaphoreSlim _copyConcurrency = new(2, 2);
    private readonly ConcurrentDictionary<Guid, ImportJournal> _journals = [];
    private readonly ConcurrentDictionary<Guid, Task> _operations = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _operationCancellation = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Action<string, string> _moveDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private int _initialized;
    private int _disposed;

    public ProductServerImportService(
        ProductDataLayout layout,
        ProductServerRegistry registry,
        ProductServerRuntime runtime)
        : this(layout, registry, runtime, new ProductImportDiskSpaceProbe())
    {
    }

    internal ProductServerImportService(
        ProductDataLayout layout,
        ProductServerRegistry registry,
        ProductServerRuntime runtime,
        IProductImportDiskSpaceProbe diskSpace,
        Action<string, string>? moveDirectory = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _diskSpace = diskSpace ?? throw new ArgumentNullException(nameof(diskSpace));
        _moveDirectory = moveDirectory ?? Directory.Move;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        _layout.EnsureCreated();
        EnsureSafeDirectory(_layout.Root, _layout.Imports);
        EnsureSafeDirectory(_layout.Root, JournalDirectory);
        EnsureSafeDirectory(_layout.Root, ReceiptDirectory);

        foreach (var path in Directory.EnumerateFiles(JournalDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparse(path);
            var journal = await ReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
            ValidateJournal(journal);
            if (!_journals.TryAdd(journal.ImportId, journal))
            {
                throw new InvalidDataException("Import journal contains a duplicate id.");
            }
        }

        await CleanupOrphanedTransactionTreesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var journal in _journals.Values)
        {
            if (IsResumable(journal))
            {
                if (journal.State == ProductServerImportState.Failed && IsResumeRequired(journal))
                {
                    await MarkRecoveryStartedAsync(journal.ImportId).ConfigureAwait(false);
                }

                Schedule(journal.ImportId);
            }
        }
    }

    public async Task<ProductServerImportStatus> BeginAsync(
        ProductServerImportBeginRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ValidateDefinition(request);
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(request.MigrationKey))
            {
                var receipt = await TryReadReceiptAsync(request.MigrationKey, cancellationToken)
                    .ConfigureAwait(false);
                if (receipt is not null)
                {
                    return ReceiptStatus(receipt);
                }

                var existing = _journals.Values
                    .Where(value => string.Equals(
                        value.MigrationKey,
                        request.MigrationKey,
                        StringComparison.Ordinal))
                    .OrderByDescending(value => value.UpdatedAtUtc)
                    .FirstOrDefault(value => value.State is not ProductServerImportState.Cancelled
                        && (value.State != ProductServerImportState.Failed || IsResumeRequired(value)));
                if (existing is not null)
                {
                    return ToStatus(existing);
                }
            }

            if (_registry.TryGet(request.Server.ServerId, out _))
            {
                return new ProductServerImportStatus(
                    Guid.Empty,
                    request.Server.ServerId,
                    ProductServerImportState.Completed,
                    null,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    DateTimeOffset.UtcNow);
            }

            if (_journals.Values.Any(value => value.Server.ServerId == request.Server.ServerId &&
                value.State is not ProductServerImportState.Cancelled
                    && (value.State != ProductServerImportState.Failed || IsResumeRequired(value))))
            {
                throw new InvalidOperationException("An import for this server id already exists.");
            }

            var importId = Guid.NewGuid();
            var staging = StagingDirectory(importId);
            EnsurePathDoesNotExist(staging);
            Directory.CreateDirectory(Path.Combine(staging, "payload", "server"));
            Directory.CreateDirectory(Path.Combine(staging, "payload", "runtime"));
            EnsureNoReparseTree(staging);
            var now = DateTimeOffset.UtcNow;
            var journal = new ImportJournal(
                SchemaVersion: 1,
                importId,
                request.Server,
                NormalizeMigrationKey(request.MigrationKey),
                ProductServerImportState.Staging,
                ManifestSha256: null,
                TotalBytes: 0,
                CompletedBytes: 0,
                TotalFiles: 0,
                CompletedFiles: 0,
                ServerPromoted: false,
                RuntimePromoted: false,
                ErrorCode: null,
                ErrorMessage: null,
                CreatedAtUtc: now,
                UpdatedAtUtc: now);
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            _journals[importId] = journal;
            return ToStatus(journal);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public async Task<ProductServerImportStatus> CommitAsync(
        Guid importId,
        string manifestSha256,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        var expectedHash = ParseSha256(manifestSha256);
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = GetJournal(importId);
            if (journal.State == ProductServerImportState.Completed)
            {
                return ToStatus(journal);
            }

            if (journal.State != ProductServerImportState.Staging)
            {
                throw new InvalidOperationException("Only a staging import can be committed.");
            }

            EnsureStagingCapability(importId);
            var manifestPath = ManifestPath(importId);
            var manifestRead = await ReadUntrustedManifestAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            var actualHash = manifestRead.Sha256;
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException("Import manifest hash does not match the committed hash.");
            }

            var manifest = manifestRead.Manifest;
            var totals = ValidateManifest(manifest, importId);
            PreflightDiskSpace(totals.TotalBytes);
            journal = journal with
            {
                State = ProductServerImportState.Queued,
                ManifestSha256 = Convert.ToHexString(actualHash),
                TotalBytes = totals.TotalBytes,
                TotalFiles = totals.TotalFiles,
                CompletedBytes = 0,
                CompletedFiles = 0,
                ErrorCode = null,
                ErrorMessage = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            _journals[importId] = journal;
            Schedule(importId);
            return ToStatus(journal);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public ProductServerImportStatus GetStatus(Guid importId)
    {
        ThrowIfUnavailable();
        return ToStatus(GetJournal(importId));
    }

    public async Task<ProductServerImportStatus> CancelAsync(
        Guid importId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = GetJournal(importId);
            if (journal.State is ProductServerImportState.Completed
                or ProductServerImportState.Cancelled
                or ProductServerImportState.Failed)
            {
                return ToStatus(journal);
            }

            if (journal.ServerPromoted || journal.RuntimePromoted ||
                journal.State is ProductServerImportState.Promoting
                    or ProductServerImportState.Registering)
            {
                throw new InvalidOperationException(
                    "The import has crossed its atomic promotion boundary and can no longer be cancelled.");
            }

            if (_operationCancellation.TryGetValue(importId, out var owner))
            {
                owner.Cancel();
                return ToStatus(journal);
            }

            journal = journal with
            {
                State = ProductServerImportState.Cancelled,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            _journals[importId] = journal;
            await CleanupPrePromotionAsync(importId).ConfigureAwait(false);
            return ToStatus(journal);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _shutdown.Cancel();
        var tasks = _operations.Values.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_operations.Values.ToArray()).ConfigureAwait(false);
        }
        finally
        {
            foreach (var owner in _operationCancellation.Values)
            {
                owner.Dispose();
            }

            _shutdown.Dispose();
            _copyConcurrency.Dispose();
            _journalGate.Dispose();
        }
    }

    private void Schedule(Guid importId)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_operations.TryAdd(importId, completion.Task))
        {
            return;
        }

        _ = CompleteScheduledAsync(importId, completion);
    }

    private async Task CompleteScheduledAsync(Guid importId, TaskCompletionSource completion)
    {
        try
        {
            await ProcessScheduledAsync(importId).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            if (_operations.TryGetValue(importId, out var registered) &&
                ReferenceEquals(registered, completion.Task))
            {
                _operations.TryRemove(importId, out _);
            }
        }
    }

    private async Task ProcessScheduledAsync(Guid importId)
    {
        using var operationCancellation = new CancellationTokenSource();
        if (!_operationCancellation.TryAdd(importId, operationCancellation))
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellation.Token,
            _shutdown.Token);
        try
        {
            Exception? lastRecoveryError = null;
            for (var resumeAttempt = 0; ; resumeAttempt++)
            {
                var operationToken = resumeAttempt == 0 ? linked.Token : _shutdown.Token;
                try
                {
                    if (IsResumeRequired(GetJournal(importId)))
                    {
                        await MarkRecoveryStartedAsync(importId).ConfigureAwait(false);
                    }

                    await _copyConcurrency.WaitAsync(operationToken).ConfigureAwait(false);
                    try
                    {
                        await ProcessAsync(importId, operationToken).ConfigureAwait(false);
                        return;
                    }
                    finally
                    {
                        _copyConcurrency.Release();
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested &&
                                                           !operationCancellation.IsCancellationRequested)
                {
                    // Service shutdown is a recoverable interruption. The durable non-terminal journal
                    // is intentionally retained and InitializeAsync resumes it next start.
                    return;
                }
                catch (OperationCanceledException error) when (operationCancellation.IsCancellationRequested)
                {
                    lastRecoveryError = error;
                    await MarkCancelledAsync(importId).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    lastRecoveryError = error;
                    await MarkFailedAsync(importId, error).ConfigureAwait(false);
                }

                var journal = GetJournal(importId);
                if (!IsResumeRequired(journal))
                {
                    return;
                }

                if (resumeAttempt >= SameProcessResumeMaximumAttempts)
                {
                    await MarkRecoveryExhaustedAsync(importId, lastRecoveryError).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await _delayAsync(
                            SameProcessResumeRetryDelays[resumeAttempt],
                            _shutdown.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            _operationCancellation.TryRemove(importId, out _);
        }
    }

    private async Task ProcessAsync(Guid importId, CancellationToken cancellationToken)
    {
        var journal = GetJournal(importId);
        if (journal.State is ProductServerImportState.Completed
            or ProductServerImportState.Cancelled
            || journal.State == ProductServerImportState.Failed && !IsResumeRequired(journal))
        {
            return;
        }

        EnsureStagingCapability(importId);
        var manifestPath = ManifestPath(importId);
        var committedHash = ParseSha256(journal.ManifestSha256
            ?? throw new InvalidDataException("Committed import has no manifest hash."));
        var manifestRead = await ReadUntrustedManifestAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        var actualHash = manifestRead.Sha256;
        if (!CryptographicOperations.FixedTimeEquals(actualHash, committedHash))
        {
            throw new InvalidDataException("Import manifest changed after commit.");
        }

        var manifest = manifestRead.Manifest;
        var totals = ValidateManifest(manifest, importId);
        if (totals.TotalBytes != journal.TotalBytes || totals.TotalFiles != journal.TotalFiles)
        {
            throw new InvalidDataException("Import manifest totals changed after commit.");
        }

        var serverWork = ServerWorkingDirectory(importId);
        var runtimeWork = RuntimeWorkingDirectory(importId);
        var serverFinal = ServerFinalDirectory(journal.Server.ServerId);
        var runtimeFinal = RuntimeFinalDirectory(journal.Server.ServerId);
        var mayRecoverOwnedPromotion = journal.ServerPromoted || journal.RuntimePromoted ||
                                       journal.State is ProductServerImportState.Promoting
                                           or ProductServerImportState.Registering;
        var promoted = DetectOwnedPromotions(
            journal,
            manifest,
            serverFinal,
            runtimeFinal,
            mayRecoverOwnedPromotion);
        if (promoted.Journal.ServerPromoted != journal.ServerPromoted ||
            promoted.Journal.RuntimePromoted != journal.RuntimePromoted)
        {
            journal = promoted.Journal with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            journal = promoted.Journal;
        }

        journal = await TransitionAsync(journal, ProductServerImportState.Verifying)
            .ConfigureAwait(false);
        await VerifyStagingAsync(importId, manifest, cancellationToken).ConfigureAwait(false);
        PreflightDiskSpace(totals.TotalBytes);

        if (!journal.ServerPromoted || !journal.RuntimePromoted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serverWorkReady = await PrepareWorkingTreeAsync(
                    _layout.Servers,
                    serverWork,
                    manifest,
                    "server/",
                    journal.ServerPromoted)
                .ConfigureAwait(false);
            var runtimeWorkReady = await PrepareWorkingTreeAsync(
                    _layout.Runtimes,
                    runtimeWork,
                    manifest,
                    "runtime/",
                    journal.RuntimePromoted)
                .ConfigureAwait(false);

            journal = await TransitionAsync(journal, ProductServerImportState.Copying)
                .ConfigureAwait(false);
            var retainedEntries = manifest.Files.Where(entry =>
                entry.Path.StartsWith("server/", StringComparison.Ordinal)
                    ? journal.ServerPromoted || serverWorkReady
                    : journal.RuntimePromoted || runtimeWorkReady).ToArray();
            journal = journal with
            {
                CompletedBytes = retainedEntries.Sum(entry => entry.Length),
                CompletedFiles = retainedEntries.Length,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await PersistJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            await CopyManifestAsync(
                    importId,
                    manifest,
                    serverWork,
                    runtimeWork,
                    journal,
                    copyServer: !journal.ServerPromoted && !serverWorkReady,
                    copyRuntime: !journal.RuntimePromoted && !runtimeWorkReady,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            journal = await TransitionAsync(GetJournal(importId), ProductServerImportState.Promoting)
                .ConfigureAwait(false);

            if (!journal.ServerPromoted)
            {
                EnsurePathDoesNotExist(serverFinal);
                await MoveDirectoryWithRetryAsync(serverWork, serverFinal, CancellationToken.None)
                    .ConfigureAwait(false);
                journal = journal with { ServerPromoted = true, UpdatedAtUtc = DateTimeOffset.UtcNow };
                _journals[importId] = journal;
                await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
            }

            if (!journal.RuntimePromoted)
            {
                EnsurePathDoesNotExist(runtimeFinal);
                await MoveDirectoryWithRetryAsync(runtimeWork, runtimeFinal, CancellationToken.None)
                    .ConfigureAwait(false);
                journal = journal with { RuntimePromoted = true, UpdatedAtUtc = DateTimeOffset.UtcNow };
                _journals[importId] = journal;
                await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Cancellation is deliberately ignored after promotion. Completing the registry commit
        // is the only safe outcome once either final tree became visible.
        journal = await TransitionAsync(journal, ProductServerImportState.Registering)
            .ConfigureAwait(false);
        var registration = ToRegistration(journal.Server);
        ValidatePromotedLaunchFiles(registration);
        await _runtime.UpsertAsync(registration, CancellationToken.None).ConfigureAwait(false);
        var receipt = new ImportReceipt(
            SchemaVersion: 1,
            journal.ImportId,
            journal.Server.ServerId,
            journal.MigrationKey,
            journal.ManifestSha256!,
            DateTimeOffset.UtcNow);
        if (!string.IsNullOrWhiteSpace(receipt.MigrationKey))
        {
            await WriteReceiptAsync(receipt, CancellationToken.None).ConfigureAwait(false);
        }

        journal = journal with
        {
            State = ProductServerImportState.Completed,
            CompletedBytes = journal.TotalBytes,
            CompletedFiles = journal.TotalFiles,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DeleteIfExistsAsync(_layout.Imports, StagingDirectory(importId)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Completion is already durable. Startup orphan cleanup retries this private staging
            // tree and never follows reparse points.
        }
    }

    private async Task CopyManifestAsync(
        Guid importId,
        ProductServerImportManifest manifest,
        string serverWork,
        string runtimeWork,
        ImportJournal journal,
        bool copyServer,
        bool copyRuntime,
        CancellationToken cancellationToken)
    {
        var completedBytes = journal.CompletedBytes;
        var completedFiles = journal.CompletedFiles;
        foreach (var entry in manifest.Files.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isServerEntry = entry.Path.StartsWith("server/", StringComparison.Ordinal);
            if (isServerEntry ? !copyServer : !copyRuntime)
            {
                continue;
            }

            var mapping = ResolveManifestEntry(importId, entry.Path, serverWork, runtimeWork);
            Directory.CreateDirectory(Path.GetDirectoryName(mapping.Destination)!);
            EnsureNoReparseAncestors(mapping.DestinationRoot, Path.GetDirectoryName(mapping.Destination)!);
            await CopyAndVerifyAsync(mapping.Source, mapping.Destination, entry, cancellationToken)
                .ConfigureAwait(false);
            completedBytes = checked(completedBytes + entry.Length);
            completedFiles++;
            journal = journal with
            {
                CompletedBytes = completedBytes,
                CompletedFiles = completedFiles,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            if (completedFiles == manifest.Files.Count || completedFiles % 32 == 0)
            {
                await PersistJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task VerifyStagingAsync(
        Guid importId,
        ProductServerImportManifest manifest,
        CancellationToken cancellationToken)
    {
        EnsureStagingCapability(importId);
        var payload = PayloadDirectory(importId);
        EnsureNoReparseTree(payload);
        var actual = EnumerateFilesNoFollow(payload)
            .Select(path => NormalizeManifestPath(
                NormalizeRelativePath(Path.GetRelativePath(payload, path))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = manifest.Files.Select(value => value.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException("Import payload files do not exactly match the manifest.");
        }

        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveManifestEntry(importId, entry.Path, string.Empty, string.Empty).Source;
            RejectReparse(source);
            var info = new FileInfo(source);
            if (!info.Exists || info.Length != entry.Length)
            {
                throw new InvalidDataException("Import payload file size does not match the manifest.");
            }

            var actualHash = await HashUntrustedFileAsync(source, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, ParseSha256(entry.Sha256)))
            {
                throw new InvalidDataException("Import payload file hash does not match the manifest.");
            }
        }
    }

    private async Task CopyAndVerifyAsync(
        string source,
        string destination,
        ProductServerImportManifestEntry entry,
        CancellationToken cancellationToken)
    {
        await using var sourceLease = ProductNoFollowFileReader.Open(_layout.Imports, source);
        var input = sourceLease.Stream;
        if (input.Length != entry.Length)
        {
            throw new InvalidDataException("Import source changed during copy.");
        }

        await using (var output = new FileStream(
                         destination,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        var copiedHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(copiedHash, ParseSha256(entry.Sha256)))
        {
            throw new InvalidDataException("Copied import file hash does not match the manifest.");
        }
    }

    private async Task<bool> PrepareWorkingTreeAsync(
        string ownedRoot,
        string workingDirectory,
        ProductServerImportManifest manifest,
        string manifestPrefix,
        bool alreadyPromoted)
    {
        if (alreadyPromoted)
        {
            await DeleteIfExistsAsync(ownedRoot, workingDirectory).ConfigureAwait(false);
            return false;
        }

        if (Directory.Exists(workingDirectory) &&
            TreeMatchesManifest(workingDirectory, manifest, manifestPrefix))
        {
            return true;
        }

        await DeleteIfExistsAsync(ownedRoot, workingDirectory).ConfigureAwait(false);
        Directory.CreateDirectory(workingDirectory);
        SafePath.EnsureNoReparsePointsUnderRoot(ownedRoot, workingDirectory);
        return false;
    }

    private async Task MoveDirectoryWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _moveDirectory(source, destination);
                return;
            }
            catch (Exception error) when (
                (error is IOException or UnauthorizedAccessException) &&
                attempt < DirectoryMoveMaximumAttempts - 1)
            {
                await _delayAsync(DirectoryMoveRetryDelays[attempt], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private PromotionDetection DetectOwnedPromotions(
        ImportJournal journal,
        ProductServerImportManifest manifest,
        string serverFinal,
        string runtimeFinal,
        bool mayRecoverOwnedPromotion)
    {
        var serverExists = Directory.Exists(serverFinal);
        var runtimeExists = Directory.Exists(runtimeFinal);
        if (serverExists && !journal.ServerPromoted)
        {
            if (!mayRecoverOwnedPromotion ||
                !TreeMatchesManifest(serverFinal, manifest, "server/"))
            {
                throw new IOException("The final server directory already exists.");
            }

            journal = journal with { ServerPromoted = true };
        }

        if (runtimeExists && !journal.RuntimePromoted)
        {
            if (!mayRecoverOwnedPromotion ||
                !TreeMatchesManifest(runtimeFinal, manifest, "runtime/"))
            {
                throw new IOException("The final runtime directory already exists.");
            }

            journal = journal with { RuntimePromoted = true };
        }

        if (journal.ServerPromoted && !serverExists || journal.RuntimePromoted && !runtimeExists)
        {
            throw new IOException("A promoted import tree is missing.");
        }

        return new PromotionDetection(journal, serverExists, runtimeExists);
    }

    private bool TreeMatchesManifest(
        string root,
        ProductServerImportManifest manifest,
        string prefix)
    {
        try
        {
            EnsureNoReparseTree(root);
            var expected = manifest.Files
                .Where(value => value.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    value => value.Path[prefix.Length..],
                    value => value,
                    StringComparer.OrdinalIgnoreCase);
            var files = EnumerateFilesNoFollow(root).ToArray();
            if (files.Length != expected.Count)
            {
                return false;
            }

            foreach (var file in files)
            {
                var relative = NormalizeRelativePath(Path.GetRelativePath(root, file));
                if (!expected.TryGetValue(relative, out var entry) ||
                    new FileInfo(file).Length != entry.Length)
                {
                    return false;
                }

                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.SequentialScan);
                var hash = SHA256.HashData(stream);
                if (!CryptographicOperations.FixedTimeEquals(hash, ParseSha256(entry.Sha256)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private void ValidatePromotedLaunchFiles(ProductServerRegistration registration)
    {
        var serverRoot = ProductServerRegistrationValidator.ResolveOwnedPath(
            _layout.Servers,
            registration.ServerDirectory,
            allowRoot: false);
        var runtime = ProductServerRegistrationValidator.ResolveOwnedPath(
            _layout.Runtimes,
            registration.JavaRuntimePath,
            allowRoot: false);
        EnsureNoReparseTree(serverRoot);
        RejectReparse(runtime);
        if (!File.Exists(runtime))
        {
            throw new FileNotFoundException("Imported Java executable is missing.", runtime);
        }

        if (registration.LaunchKind == ProductServerLaunchKind.ExecutableJar)
        {
            var jar = SafePath.EnsureWithinRoot(serverRoot, registration.ServerJarPath, allowRoot: false);
            RejectReparse(jar);
            if (!File.Exists(jar))
            {
                throw new FileNotFoundException("Imported server JAR is missing.", jar);
            }
        }
        else
        {
            foreach (var relative in registration.JavaArgumentFilePaths)
            {
                var path = SafePath.EnsureWithinRoot(serverRoot, relative, allowRoot: false);
                RejectReparse(path);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Imported Java argument file is missing.", path);
                }
            }
        }
    }

    private ProductServerRegistration ToRegistration(ProductServerImportDefinition definition)
    {
        var id = definition.ServerId.ToString("N");
        var registration = new ProductServerRegistration
        {
            Id = definition.ServerId,
            Name = definition.Name,
            ServerDirectory = id,
            JavaRuntimePath = NormalizeRelativePath(Path.Combine(id, definition.JavaExecutablePath)),
            LaunchKind = definition.LaunchKind,
            ServerJarPath = NormalizeRelativePath(definition.ServerJarPath),
            JavaArgumentFilePaths = definition.JavaArgumentFilePaths
                .Select(NormalizeRelativePath)
                .ToArray(),
            CoreType = definition.CoreType,
            MinecraftVersion = definition.MinecraftVersion,
            MinimumMemoryMb = definition.MinimumMemoryMb,
            MaximumMemoryMb = definition.MaximumMemoryMb,
            JvmArguments = definition.JvmArguments.ToArray(),
            ServerArguments = definition.ServerArguments.ToArray(),
            StopCommand = definition.StopCommand,
            Port = definition.Port,
            AutoRestart = definition.AutoRestart,
            ModpackProviderId = definition.ModpackProviderId,
            ModpackSource = definition.ModpackSource,
            ModpackProjectId = definition.ModpackProjectId,
            ModpackVersionId = definition.ModpackVersionId,
            ModpackVersionName = definition.ModpackVersionName,
            IsInstallerArtifact = definition.IsInstallerArtifact,
        };
        ProductServerRegistrationValidator.ValidateAndThrow(registration, _layout);
        return registration;
    }

    private void ValidateDefinition(ProductServerImportBeginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Server);
        _ = ToRegistration(request.Server);
        _ = NormalizeMigrationKey(request.MigrationKey);
        ValidatePayloadRelativePath(request.Server.JavaExecutablePath, "Java executable path");
        ValidatePayloadRelativePath(request.Server.ServerJarPath, "Server JAR path");
        foreach (var path in request.Server.JavaArgumentFilePaths)
        {
            ValidatePayloadRelativePath(path, "Java argument-file path");
        }
    }

    private static (long TotalBytes, int TotalFiles) ValidateManifest(
        ProductServerImportManifest manifest,
        Guid importId)
    {
        if (manifest.SchemaVersion != 1 || manifest.ImportId != importId ||
            manifest.Files is null ||
            manifest.Files.Count is < 1 or > MaximumManifestFiles)
        {
            throw new InvalidDataException("Import manifest header or file count is invalid.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var hasServer = false;
        var hasRuntime = false;
        foreach (var entry in manifest.Files)
        {
            if (entry is null)
            {
                throw new InvalidDataException("Import manifest contains an empty file entry.");
            }

            var normalized = NormalizeManifestPath(entry.Path);
            if (!string.Equals(normalized, entry.Path, StringComparison.Ordinal) || !paths.Add(normalized))
            {
                throw new InvalidDataException("Import manifest contains a non-canonical or duplicate path.");
            }

            hasServer |= normalized.StartsWith("server/", StringComparison.Ordinal);
            hasRuntime |= normalized.StartsWith("runtime/", StringComparison.Ordinal);
            if (entry.Length < 0 || entry.Length > MaximumImportBytes)
            {
                throw new InvalidDataException("Import manifest file length is invalid.");
            }

            _ = ParseSha256(entry.Sha256);
            total = checked(total + entry.Length);
            if (total > MaximumImportBytes)
            {
                throw new InvalidDataException("Import manifest exceeds the total byte limit.");
            }
        }

        if (!hasServer || !hasRuntime)
        {
            throw new InvalidDataException("Import manifest must contain server and runtime payloads.");
        }

        return (total, manifest.Files.Count);
    }

    private void PreflightDiskSpace(long totalBytes)
    {
        var required = checked(totalBytes + DiskSafetyMarginBytes);
        if (_diskSpace.GetAvailableBytes(_layout.Servers) < required ||
            _diskSpace.GetAvailableBytes(_layout.Runtimes) < required)
        {
            throw new IOException("Insufficient disk space for the Service-owned import transaction.");
        }
    }

    private async Task<ImportJournal> TransitionAsync(
        ImportJournal journal,
        ProductServerImportState state)
    {
        journal = journal with { State = state, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        return journal;
    }

    private async Task PersistJournalAsync(ImportJournal journal, CancellationToken cancellationToken)
    {
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            _journals[journal.ImportId] = journal;
        }
        finally
        {
            _journalGate.Release();
        }
    }

    private async Task MarkCancelledAsync(Guid importId)
    {
        var journal = GetJournal(importId);
        if (journal.State == ProductServerImportState.Completed)
        {
            return;
        }

        if (journal.ServerPromoted || journal.RuntimePromoted)
        {
            journal = journal with
            {
                State = ProductServerImportState.Registering,
                ErrorCode = "import.resume_required",
                ErrorMessage = "Cancellation arrived after the atomic promotion boundary.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        journal = journal with
        {
            State = ProductServerImportState.Cancelled,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        await CleanupPrePromotionAsync(importId).ConfigureAwait(false);
    }

    private async Task MarkFailedAsync(Guid importId, Exception error)
    {
        var journal = GetJournal(importId);
        if (journal.State == ProductServerImportState.Completed)
        {
            return;
        }
        if (journal.ServerPromoted || journal.RuntimePromoted)
        {
            // A transient failure after promotion remains resumable; do not strand an unregistered
            // final tree by turning the journal terminal.
            journal = journal with
            {
                State = ProductServerImportState.Registering,
                ErrorCode = "import.resume_required",
                ErrorMessage = Truncate(error.Message, 512),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }
        else
        {
            journal = journal with
            {
                State = ProductServerImportState.Failed,
                ErrorCode = MapErrorCode(error),
                ErrorMessage = Truncate(error.Message, 512),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        if (journal.State == ProductServerImportState.Failed)
        {
            await CleanupPrePromotionAsync(importId).ConfigureAwait(false);
        }
    }

    private async Task MarkRecoveryStartedAsync(Guid importId)
    {
        var journal = GetJournal(importId);
        if (!IsResumeRequired(journal))
        {
            return;
        }

        journal = journal with
        {
            State = ProductServerImportState.Registering,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task MarkRecoveryExhaustedAsync(Guid importId, Exception? error)
    {
        var journal = GetJournal(importId);
        if (!IsResumeRequired(journal))
        {
            return;
        }

        journal = journal with
        {
            State = ProductServerImportState.Failed,
            ErrorCode = "import.resume_required",
            ErrorMessage = Truncate(
                $"Automatic recovery was exhausted; restart the Service to resume. {error?.Message}",
                512),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CleanupPrePromotionAsync(Guid importId)
    {
        await DeleteIfExistsAsync(_layout.Imports, StagingDirectory(importId)).ConfigureAwait(false);
        await DeleteIfExistsAsync(_layout.Servers, ServerWorkingDirectory(importId)).ConfigureAwait(false);
        await DeleteIfExistsAsync(_layout.Runtimes, RuntimeWorkingDirectory(importId)).ConfigureAwait(false);
    }

    private async Task CleanupOrphanedTransactionTreesAsync(CancellationToken cancellationToken)
    {
        foreach (var staging in Directory.EnumerateDirectories(_layout.Imports, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(staging);
            if (name.Equals("modpack-updates", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var known = Guid.TryParseExact(name, "N", out var id) &&
                        _journals.TryGetValue(id, out var journal) &&
                        (!IsTerminal(journal.State) || IsResumeRequired(journal));
            if (!known)
            {
                await DeleteIfExistsAsync(_layout.Imports, staging).ConfigureAwait(false);
            }
        }

        foreach (var (root, pattern) in new[]
                 {
                     (_layout.Servers, ".import-*"),
                     (_layout.Runtimes, ".import-*"),
                 })
        {
            foreach (var working in Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(working);
                var idText = name[".import-".Length..];
                var known = Guid.TryParseExact(idText, "N", out var id) &&
                            _journals.TryGetValue(id, out var journal) &&
                            (!IsTerminal(journal.State) || IsResumeRequired(journal));
                if (!known)
                {
                    await DeleteIfExistsAsync(root, working).ConfigureAwait(false);
                }
            }
        }
    }

    private static string MapErrorCode(Exception error) => error switch
    {
        InvalidDataException or CryptographicException => "import.integrity_failed",
        UnauthorizedAccessException => "import.access_denied",
        IOException when error.Message.Contains("space", StringComparison.OrdinalIgnoreCase) =>
            "import.disk_insufficient",
        IOException => "import.io_failed",
        ArgumentException => "import.definition_invalid",
        _ => "import.failed",
    };

    private static bool IsTerminal(ProductServerImportState state)
        => state is ProductServerImportState.Completed
            or ProductServerImportState.Cancelled
            or ProductServerImportState.Failed;

    private static bool IsResumeRequired(ImportJournal journal)
        => string.Equals(journal.ErrorCode, "import.resume_required", StringComparison.Ordinal) &&
           (journal.ServerPromoted || journal.RuntimePromoted);

    private static bool IsResumable(ImportJournal journal)
        => journal.State is ProductServerImportState.Queued
            or ProductServerImportState.Verifying
            or ProductServerImportState.Copying
            or ProductServerImportState.Promoting
            or ProductServerImportState.Registering ||
           journal.State == ProductServerImportState.Failed && IsResumeRequired(journal);

    private async Task<ManifestRead> ReadUntrustedManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var lease = ProductNoFollowFileReader.Open(_layout.Imports, path);
        var stream = lease.Stream;
        if (stream.Length is < 2 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("Import manifest size is outside the allowed range.");
        }

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        try
        {
            var manifest = await JsonSerializer.DeserializeAsync<ProductServerImportManifest>(
                                   stream,
                                   JsonOptions,
                                   cancellationToken)
                               .ConfigureAwait(false)
                           ?? throw new InvalidDataException("Import manifest is empty.");
            return new ManifestRead(manifest, hash);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Import manifest JSON is invalid.", error);
        }
    }

    private async Task<ImportJournal> ReadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 2 or > 1024 * 1024)
        {
            throw new InvalidDataException("Import journal size is outside the allowed range.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            return await JsonSerializer.DeserializeAsync<ImportJournal>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   .ConfigureAwait(false)
                   ?? throw new InvalidDataException("Import journal is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Import journal JSON is invalid.", error);
        }
    }

    private Task WriteJournalAsync(ImportJournal journal, CancellationToken cancellationToken)
        => WriteAtomicJsonAsync(JournalPath(journal.ImportId), journal, cancellationToken);

    private Task WriteReceiptAsync(ImportReceipt receipt, CancellationToken cancellationToken)
        => WriteAtomicJsonAsync(ReceiptPath(receipt.MigrationKey!), receipt, cancellationToken);

    private async Task<ImportReceipt?> TryReadReceiptAsync(
        string migrationKey,
        CancellationToken cancellationToken)
    {
        var path = ReceiptPath(migrationKey);
        if (!File.Exists(path))
        {
            return null;
        }

        RejectReparse(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 2 or > 64 * 1024)
        {
            throw new InvalidDataException("Import receipt size is invalid.");
        }

        var receipt = await JsonSerializer.DeserializeAsync<ImportReceipt>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Import receipt is empty.");
        if (receipt.SchemaVersion != 1 || receipt.ImportId == Guid.Empty || receipt.ServerId == Guid.Empty ||
            !string.Equals(receipt.MigrationKey, migrationKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Import receipt is invalid.");
        }

        return receipt;
    }

    private static async Task WriteAtomicJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
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
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> HashUntrustedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var lease = ProductNoFollowFileReader.Open(_layout.Imports, path);
        return await SHA256.HashDataAsync(lease.Stream, cancellationToken).ConfigureAwait(false);
    }

    private ManifestMapping ResolveManifestEntry(
        Guid importId,
        string manifestPath,
        string serverDestination,
        string runtimeDestination)
    {
        var normalized = NormalizeManifestPath(manifestPath);
        var slash = normalized.IndexOf('/');
        var kind = normalized[..slash];
        var relative = normalized[(slash + 1)..];
        var sourceRoot = Path.Combine(PayloadDirectory(importId), kind);
        var source = SafePath.EnsureWithinRoot(sourceRoot, relative, allowRoot: false);
        var destinationRoot = kind == "server" ? serverDestination : runtimeDestination;
        var destination = string.IsNullOrEmpty(destinationRoot)
            ? string.Empty
            : SafePath.EnsureWithinRoot(destinationRoot, relative, allowRoot: false);
        return new ManifestMapping(source, destinationRoot, destination);
    }

    private static string NormalizeManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 520 || path.Contains('\\') ||
            path.StartsWith('/') || path.EndsWith('/') ||
            path.Any(character => character is '\0' or '\r' or '\n' || char.IsControl(character)))
        {
            throw new InvalidDataException("Import manifest path is invalid.");
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length < 2 || segments[0] is not ("server" or "runtime") ||
            segments.Skip(1).Any(IsUnsafePathSegment))
        {
            throw new InvalidDataException("Import manifest path escapes its payload root.");
        }

        return string.Join('/', segments);
    }

    private static bool IsUnsafePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
            segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return true;
        }

        var stem = segment.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9');
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/');

    private static void ValidatePayloadRelativePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{label} is invalid.");
        }

        var segments = NormalizeRelativePath(path).Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException($"{label} must be a root-confined relative path.");
        }
    }

    private static string? NormalizeMigrationKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > 128 || value.Any(character => character is '\0' or '\r' or '\n' || char.IsControl(character)))
        {
            throw new ArgumentException("Migration key is invalid.");
        }

        return value;
    }

    private static byte[] ParseSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("SHA-256 value is invalid.");
        }

        try
        {
            var bytes = Convert.FromHexString(value);
            return bytes.Length == 32
                ? bytes
                : throw new InvalidDataException("SHA-256 value is invalid.");
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("SHA-256 value is invalid.", error);
        }
    }

    private static IEnumerable<string> EnumerateFilesNoFollow(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparse(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Import payload cannot contain a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static void EnsureNoReparseTree(string root)
    {
        _ = EnumerateFilesNoFollow(root).Count();
    }

    private static void EnsureNoReparseAncestors(string root, string path)
    {
        SafePath.EnsureNoReparsePointsUnderRoot(root, path);
    }

    private static void RejectReparse(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("Expected import path was not found.", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Import path cannot be a reparse point.");
        }
    }

    private static void EnsureSafeDirectory(string root, string directory)
    {
        var resolved = SafePath.EnsureWithinRoot(root, directory, allowRoot: false);
        Directory.CreateDirectory(resolved);
        SafePath.EnsureNoReparsePointsUnderRoot(root, resolved);
    }

    private void EnsureStagingCapability(Guid importId)
    {
        var staging = SafePath.EnsureWithinRoot(
            _layout.Imports,
            StagingDirectory(importId),
            allowRoot: false);
        SafePath.EnsureNoReparsePointsUnderRoot(_layout.Imports, staging);
    }

    private static void EnsurePathDoesNotExist(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Import destination already exists.");
        }
    }

    private static async Task DeleteIfExistsAsync(string root, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                root,
                path,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void ValidateJournal(ImportJournal journal)
    {
        if (journal.SchemaVersion != 1 || journal.ImportId == Guid.Empty ||
            journal.Server.ServerId == Guid.Empty || !Enum.IsDefined(journal.State))
        {
            throw new InvalidDataException("Import journal is invalid.");
        }

        ValidateDefinition(new ProductServerImportBeginRequest(journal.Server, journal.MigrationKey));
        if (!string.Equals(
                Path.GetFileName(JournalPath(journal.ImportId)),
                $"{journal.ImportId:N}.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Import journal id is invalid.");
        }
    }

    private ImportJournal GetJournal(Guid importId)
    {
        if (importId == Guid.Empty || !_journals.TryGetValue(importId, out var journal))
        {
            throw new KeyNotFoundException("Import transaction was not found.");
        }

        return journal;
    }

    private ProductServerImportStatus ToStatus(ImportJournal journal)
        => new(
            journal.ImportId,
            journal.Server.ServerId,
            journal.State,
            journal.State is ProductServerImportState.Staging or ProductServerImportState.Queued
                ? StagingDirectory(journal.ImportId)
                : null,
            journal.TotalBytes,
            journal.CompletedBytes,
            journal.TotalFiles,
            journal.CompletedFiles,
            journal.ErrorCode,
            journal.ErrorMessage,
            journal.UpdatedAtUtc);

    private static ProductServerImportStatus ReceiptStatus(ImportReceipt receipt)
        => new(
            receipt.ImportId,
            receipt.ServerId,
            ProductServerImportState.Completed,
            null,
            0,
            0,
            0,
            0,
            null,
            null,
            receipt.CompletedAtUtc);

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new InvalidOperationException("Import service has not been initialized.");
        }

        if (_shutdown.IsCancellationRequested)
        {
            throw new InvalidOperationException("Import service is shutting down.");
        }
    }

    private string JournalDirectory => Path.Combine(_layout.Operations, "imports");
    private string ReceiptDirectory => Path.Combine(JournalDirectory, "receipts");
    private string JournalPath(Guid id) => Path.Combine(JournalDirectory, $"{id:N}.json");
    private string StagingDirectory(Guid id) => Path.Combine(_layout.Imports, id.ToString("N"));
    private string PayloadDirectory(Guid id) => Path.Combine(StagingDirectory(id), "payload");
    private string ManifestPath(Guid id) => Path.Combine(StagingDirectory(id), ManifestFileName);
    private string ServerWorkingDirectory(Guid id) => Path.Combine(_layout.Servers, $".import-{id:N}");
    private string RuntimeWorkingDirectory(Guid id) => Path.Combine(_layout.Runtimes, $".import-{id:N}");
    private string ServerFinalDirectory(Guid serverId) => Path.Combine(_layout.Servers, serverId.ToString("N"));
    private string RuntimeFinalDirectory(Guid serverId) => Path.Combine(_layout.Runtimes, serverId.ToString("N"));
    private string ReceiptPath(string migrationKey)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migrationKey)));
        return Path.Combine(ReceiptDirectory, $"{name}.json");
    }

    private static string? Truncate(string? value, int maximum)
        => value is null || value.Length <= maximum ? value : value[..maximum];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };

    private sealed record ImportJournal(
        int SchemaVersion,
        Guid ImportId,
        ProductServerImportDefinition Server,
        string? MigrationKey,
        ProductServerImportState State,
        string? ManifestSha256,
        long TotalBytes,
        long CompletedBytes,
        int TotalFiles,
        int CompletedFiles,
        bool ServerPromoted,
        bool RuntimePromoted,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    private sealed record ImportReceipt(
        int SchemaVersion,
        Guid ImportId,
        Guid ServerId,
        string? MigrationKey,
        string ManifestSha256,
        DateTimeOffset CompletedAtUtc);

    private sealed record ManifestMapping(string Source, string DestinationRoot, string Destination);
    private sealed record ManifestRead(ProductServerImportManifest Manifest, byte[] Sha256);
    private sealed record PromotionDetection(ImportJournal Journal, bool ServerExists, bool RuntimeExists);
}
