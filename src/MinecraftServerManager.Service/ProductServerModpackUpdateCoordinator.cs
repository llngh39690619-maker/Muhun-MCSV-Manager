using System.Collections.Concurrent;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Service-owned, staged modpack update transaction. The caller may write only to the capability
/// directory returned by Begin; filesystem commit, registry persistence, first-launch health
/// validation and rollback all remain inside the Windows Service.
/// </summary>
public sealed class ProductServerModpackUpdateCoordinator : IAsyncDisposable
{
    public const string ManifestFileName = "manifest.v1.json";
    internal const int MaximumFiles = 100_000;
    internal const long MaximumBytes = 1024L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan DefaultHealthValidationTimeout = TimeSpan.FromMinutes(30);
    private const long MaximumManifestBytes = 32L * 1024 * 1024;
    private readonly ProductDataLayout _layout;
    private readonly ProductServerRegistry _registry;
    private readonly ProductServerRuntime _runtime;
    private readonly ProductServerRestartBlocker _restartBlocker;
    private readonly ModpackUpdateTransactionService _transactions;
    private readonly ModpackUpdateBackupPlanner _backupPlanner;
    private readonly BackupService _backupService;
    private readonly IMinecraftStatusProbe _statusProbe;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _healthValidationTimeout;
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private readonly SemaphoreSlim _operationConcurrency = new(2, 2);
    private readonly ConcurrentDictionary<Guid, UpdateJournal> _journals = [];
    private readonly ConcurrentDictionary<Guid, Task> _operations = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _operationCancellation = [];
    private readonly ConcurrentDictionary<Guid, Task> _finalizations = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _probeCancellation = [];
    private readonly ConcurrentDictionary<Guid, Task> _probeTasks = [];
    private readonly ConcurrentDictionary<Guid, Guid> _manualStops = [];
    private readonly ConcurrentDictionary<Guid, ProductServerModpackUpdateState> _announcedTerminalStates = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _initialized;
    private int _disposed;

    public event EventHandler<ProductServerModpackUpdateStatus>? TerminalStatePersisted;

    public ProductServerModpackUpdateCoordinator(
        ProductDataLayout layout,
        ProductServerRegistry registry,
        ProductServerRuntime runtime,
        ProductServerRestartBlocker restartBlocker,
        BackupService backupService,
        IMinecraftStatusProbe statusProbe)
        : this(
            layout,
            registry,
            runtime,
            restartBlocker,
            backupService,
            statusProbe,
            new ModpackUpdateTransactionService(),
            new ModpackUpdateBackupPlanner(),
            TimeProvider.System,
            DefaultHealthValidationTimeout)
    {
    }

    internal ProductServerModpackUpdateCoordinator(
        ProductDataLayout layout,
        ProductServerRegistry registry,
        ProductServerRuntime runtime,
        ProductServerRestartBlocker restartBlocker,
        BackupService backupService,
        IMinecraftStatusProbe statusProbe,
        ModpackUpdateTransactionService transactions,
        ModpackUpdateBackupPlanner backupPlanner,
        TimeProvider? timeProvider = null,
        TimeSpan? healthValidationTimeout = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _restartBlocker = restartBlocker ?? throw new ArgumentNullException(nameof(restartBlocker));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _statusProbe = statusProbe ?? throw new ArgumentNullException(nameof(statusProbe));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _backupPlanner = backupPlanner ?? throw new ArgumentNullException(nameof(backupPlanner));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthValidationTimeout = healthValidationTimeout ?? DefaultHealthValidationTimeout;
        if (_healthValidationTimeout < TimeSpan.FromMilliseconds(100) ||
            _healthValidationTimeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(healthValidationTimeout),
                "Health validation timeout must be between 100 milliseconds and 24 hours.");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        _layout.EnsureCreated();
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(JournalRoot);
        Directory.CreateDirectory(PrivatePayloadRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_layout.Imports, StagingRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_layout.Operations, JournalRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_layout.Operations, PrivatePayloadRoot);
        foreach (var path in Directory.EnumerateFiles(JournalRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparse(path);
            var journal = await ReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
            ValidateJournal(journal);
            if (!_journals.TryAdd(journal.UpdateId, journal))
            {
                throw new InvalidDataException("Modpack update journal contains a duplicate id.");
            }
        }

        foreach (var journal in _journals.Values.OrderBy(value => value.CreatedAtUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!journal.IsTerminal)
            {
                await RecoverAsync(journal, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var registration in _registry.GetAll())
        {
            var live = _runtime.CreateCoreInstance(registration);
            if (_transactions.HasPendingArtifacts(live) &&
                !_journals.Values.Any(value => value.ServerId == registration.Id && !value.IsTerminal))
            {
                throw new InvalidDataException(
                    "A Service-owned server has modpack transaction artifacts without its Service journal.");
            }
        }

        await CleanupOrphansAsync(cancellationToken).ConfigureAwait(false);
        _runtime.ConsoleLineObserved += OnConsoleLineObserved;
        _runtime.StateChanged += OnStateChanged;
        _runtime.ManualStopRequested += OnManualStopRequested;
        _runtime.ManualStopRequestCancelled += OnManualStopRequestCancelled;

        foreach (var journal in _journals.Values)
        {
            if (journal.State is ProductServerModpackUpdateState.Queued
                or ProductServerModpackUpdateState.Verifying
                or ProductServerModpackUpdateState.BackingUp)
            {
                Schedule(journal.UpdateId);
            }

            // Test hosts and in-process recovery can re-create the coordinator while a Java
            // session remains alive. Resume the persisted health window instead of waiting for
            // another StateChanged edge that may never arrive.
            if (journal.State == ProductServerModpackUpdateState.AwaitingHealth)
            {
                var status = _runtime.GetStatus(journal.ServerId);
                if (status.Server.State == ProductServerState.Running && status.SessionId is { } sessionId)
                {
                    SetSessionInMemory(journal.UpdateId, sessionId);
                    StartStatusProbe(journal.UpdateId, sessionId);
                }
            }
        }
    }

    public async Task<ProductServerModpackUpdateStatus> BeginAsync(
        ProductServerModpackUpdateBeginRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(request);
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registration = ValidateBegin(request);
            if (_runtime.GetStatus(registration.Id).Server.State != ProductServerState.Stopped)
            {
                throw new InvalidOperationException(
                    "Modpack updates can begin only while the server is completely stopped.");
            }

            var existing = _journals.Values.FirstOrDefault(value =>
                value.ServerId == request.ServerId && !value.IsTerminal);
            if (existing is not null)
            {
                return ToStatus(existing);
            }

            var updateId = Guid.NewGuid();
            var staging = StagingDirectory(updateId);
            EnsurePathDoesNotExist(staging);
            Directory.CreateDirectory(CandidateDirectory(updateId));
            SafePath.EnsureNoReparsePointsUnderRoot(StagingRoot, CandidateDirectory(updateId));
            var now = DateTimeOffset.UtcNow;
            var journal = new UpdateJournal(
                SchemaVersion: 1,
                Revision: 1,
                updateId,
                request.ServerId,
                request.ExpectedCurrentVersionId.Trim(),
                request.Target,
                registration,
                ProductServerModpackUpdateState.Staging,
                ManifestSha256: null,
                CoreTransactionId: null,
                SessionId: null,
                TotalBytes: 0,
                CompletedBytes: 0,
                TotalFiles: 0,
                CompletedFiles: 0,
                BackupArchivePath: null,
                ErrorCode: null,
                ErrorMessage: null,
                CreatedAtUtc: now,
                UpdatedAtUtc: now,
                CancellationRequested: false);
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            _journals[updateId] = journal;
            return ToStatus(journal);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public async Task<ProductServerModpackUpdateStatus> CommitAsync(
        Guid updateId,
        string manifestSha256,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        var expectedHash = ParseSha256(manifestSha256);
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = GetJournal(updateId);
            if (journal.State != ProductServerModpackUpdateState.Staging)
            {
                return ToStatus(journal);
            }

            EnsureStagingCapability(updateId);
            var read = await ReadManifestAsync(ManifestPath(updateId), cancellationToken)
                .ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(read.Sha256, expectedHash))
            {
                throw new InvalidDataException("Modpack update manifest hash does not match.");
            }

            var totals = ValidateManifest(read.Manifest, updateId);
            journal = journal with
            {
                Revision = journal.Revision + 1,
                State = ProductServerModpackUpdateState.Queued,
                ManifestSha256 = Convert.ToHexString(read.Sha256),
                TotalBytes = totals.TotalBytes,
                TotalFiles = totals.TotalFiles,
                CompletedBytes = 0,
                CompletedFiles = 0,
                ErrorCode = null,
                ErrorMessage = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            _journals[updateId] = journal;
            Schedule(updateId);
            return ToStatus(journal);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public ProductServerModpackUpdateStatus GetStatus(Guid updateId)
    {
        ThrowIfUnavailable();
        return ToStatus(GetJournal(updateId));
    }

    public async Task<ProductServerModpackUpdateStatus> CancelAsync(
        Guid updateId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = GetJournal(updateId);
            if (journal.IsTerminal)
            {
                return ToStatus(journal);
            }

            if (journal.State == ProductServerModpackUpdateState.HealthyAwaitingStop)
            {
                throw new InvalidOperationException(
                    "A healthy committed update cannot be cancelled; stop the server to finalize it.");
            }

            if (journal.State is ProductServerModpackUpdateState.AwaitingHealth
                or ProductServerModpackUpdateState.RollingBack)
            {
                journal = journal with
                {
                    Revision = journal.Revision + 1,
                    State = ProductServerModpackUpdateState.RollingBack,
                    CancellationRequested = true,
                    ErrorCode = "modpack_update.cancelled",
                    ErrorMessage = "The committed update was cancelled before health validation completed.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                _journals[updateId] = journal;
                await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
                _restartBlocker.Block(journal.ServerId);
                QueueFinalization(updateId, rollback: true);
                return ToStatus(journal);
            }

            if (_operationCancellation.TryGetValue(updateId, out var owner))
            {
                journal = journal with
                {
                    Revision = journal.Revision + 1,
                    CancellationRequested = true,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                _journals[updateId] = journal;
                await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
                owner.Cancel();
                return ToStatus(journal);
            }

            journal = journal with
            {
                Revision = journal.Revision + 1,
                State = ProductServerModpackUpdateState.Cancelled,
                CancellationRequested = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await CleanupStagingAsync(updateId).ConfigureAwait(false);
            _journals[updateId] = journal;
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            return ToStatus(journal);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _runtime.ConsoleLineObserved -= OnConsoleLineObserved;
        _runtime.StateChanged -= OnStateChanged;
        _runtime.ManualStopRequested -= OnManualStopRequested;
        _runtime.ManualStopRequestCancelled -= OnManualStopRequestCancelled;
        _shutdown.Cancel();
        foreach (var updateId in _probeCancellation.Keys)
        {
            // Remove ownership before cancelling. The probe's finally block disposes the source;
            // enumerating raw values can otherwise race that disposal during shutdown.
            CancelProbe(updateId);
        }

        var work = _operations.Values
            .Concat(_finalizations.Values)
            .Concat(_probeTasks.Values)
            .ToArray();
        if (work.Length > 0)
        {
            await Task.WhenAll(work).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            foreach (var owner in _operationCancellation.Values)
            {
                owner.Dispose();
            }

            foreach (var probe in _probeCancellation.Values)
            {
                probe.Dispose();
            }

            _shutdown.Dispose();
            _operationConcurrency.Dispose();
            _journalGate.Dispose();
        }
    }

    private void Schedule(Guid updateId)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_operations.TryAdd(updateId, completion.Task))
        {
            return;
        }

        _ = CompleteScheduledAsync(updateId, completion);
    }

    private async Task CompleteScheduledAsync(Guid updateId, TaskCompletionSource completion)
    {
        try
        {
            await ProcessScheduledAsync(updateId).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            if (_operations.TryGetValue(updateId, out var current) &&
                ReferenceEquals(current, completion.Task))
            {
                _operations.TryRemove(updateId, out _);
            }
        }
    }

    private async Task ProcessScheduledAsync(Guid updateId)
    {
        using var owner = new CancellationTokenSource();
        if (!_operationCancellation.TryAdd(updateId, owner))
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, _shutdown.Token);
        try
        {
            await _operationConcurrency.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await ProcessAsync(updateId, linked.Token).ConfigureAwait(false);
                if (owner.IsCancellationRequested)
                {
                    await HonorLateCancellationAsync(updateId).ConfigureAwait(false);
                }
            }
            finally
            {
                _operationConcurrency.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !owner.IsCancellationRequested)
        {
            // Non-terminal journal and candidate remain durable for startup resume.
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            await MarkCancelledAsync(updateId).ConfigureAwait(false);
        }
        catch (ModpackUpdateRollbackException error)
        {
            await MarkRollbackRequiredAsync(updateId, error).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await HandleProcessFailureAsync(updateId, error).ConfigureAwait(false);
        }
        finally
        {
            _operationCancellation.TryRemove(updateId, out _);
        }
    }

    private async Task ProcessAsync(Guid updateId, CancellationToken cancellationToken)
    {
        var journal = GetJournal(updateId);
        if (journal.IsTerminal)
        {
            return;
        }

        journal = await TransitionAsync(journal, ProductServerModpackUpdateState.Verifying)
            .ConfigureAwait(false);
        EnsureStagingCapability(updateId);
        var read = await ReadManifestAsync(ManifestPath(updateId), cancellationToken)
            .ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                read.Sha256,
                ParseSha256(journal.ManifestSha256 ?? string.Empty)))
        {
            throw new InvalidDataException("Committed modpack update manifest changed.");
        }

        var totals = ValidateManifest(read.Manifest, updateId);
        if (totals.TotalBytes != journal.TotalBytes || totals.TotalFiles != journal.TotalFiles)
        {
            throw new InvalidDataException("Committed modpack update manifest totals changed.");
        }

        await VerifyCandidateAsync(updateId, read.Manifest, cancellationToken).ConfigureAwait(false);
        journal = await TransitionAsync(journal, ProductServerModpackUpdateState.BackingUp)
            .ConfigureAwait(false);

        await _runtime.ExecuteStoppedMutationAsync(
            journal.ServerId,
            async (registration, gateCancellation) =>
            {
                ValidateRegistrationStillMatches(journal, registration);
                var live = _runtime.CreateCoreInstance(registration);
                var candidate = CreateCandidate(journal, registration);
                BackupResult? backup = null;
                var result = await _transactions.CommitAsync(
                    live,
                    candidate,
                    async backupCancellation =>
                    {
                        var plan = await _backupPlanner.CreatePlanAsync(
                                live,
                                journal.Target.ModpackVersionName,
                                backupCancellation)
                            .ConfigureAwait(false);
                        backup = await _backupService.CreateBackupAsync(
                                live,
                                plan.Options,
                                progress: null,
                                backupCancellation)
                            .ConfigureAwait(false);
                        await SetBackupAndApplyingAsync(updateId, backup.ArchivePath)
                            .ConfigureAwait(false);
                    },
                    gateCancellation).ConfigureAwait(false);

                // The filesystem commit is now durable. Registry and Service journal commits use
                // CancellationToken.None so client disconnect cannot strand mismatched metadata.
                var updated = ApplyLaunchFields(registration, result.LaunchFields, journal.Target);
                try
                {
                    await _registry.UpsertAsync(updated, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    await _transactions.RequestCommittedRollbackAsync(
                            live,
                            result.TransactionId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await _transactions.RollbackCommittedAsync(
                            live,
                            result.TransactionId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }

                await MarkAwaitingHealthAsync(
                        updateId,
                        result.TransactionId,
                        backup?.ArchivePath)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverAsync(UpdateJournal journal, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(journal.ServerId, out var registration))
        {
            throw new InvalidDataException("Modpack update references a missing server registration.");
        }

        var live = _runtime.CreateCoreInstance(registration);
        var hasCoreArtifacts = _transactions.HasPendingArtifacts(live);
        if (!hasCoreArtifacts)
        {
            if (journal.CancellationRequested)
            {
                journal = journal with
                {
                    Revision = journal.Revision + 1,
                    State = ProductServerModpackUpdateState.Cancelled,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                await CleanupStagingAsync(journal.UpdateId).ConfigureAwait(false);
                _restartBlocker.Unblock(journal.ServerId);
                await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (journal.State == ProductServerModpackUpdateState.Applying)
            {
                // Applying is written from the backup callback before Core writes its own journal.
                // A crash in that narrow window therefore has no filesystem mutation to recover;
                // return to the durable queue and verify the untouched candidate again.
                journal = journal with
                {
                    Revision = journal.Revision + 1,
                    State = ProductServerModpackUpdateState.Queued,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (journal.State is ProductServerModpackUpdateState.AwaitingHealth
                or ProductServerModpackUpdateState.HealthyAwaitingStop
                or ProductServerModpackUpdateState.RollingBack)
            {
                throw new InvalidDataException(
                    "A committed modpack update Service journal is missing its Core transaction artifacts.");
            }

            return;
        }

        _restartBlocker.Block(journal.ServerId);
        var recovery = await RetryDirectoryHandoffAsync(
                () => _transactions.RecoverPendingAsync(live, cancellationToken))
            .ConfigureAwait(false);
        if (recovery.Action == ModpackUpdateRecoveryAction.RolledBack)
        {
            await _registry.UpsertAsync(journal.PreviousRegistration, CancellationToken.None)
                .ConfigureAwait(false);
            await _runtime.PreventAutomaticRestartAsync(journal.ServerId, CancellationToken.None)
                .ConfigureAwait(false);
            await CompleteRollbackJournalAsync(journal.UpdateId, recovery.TransactionId)
                .ConfigureAwait(false);
            return;
        }

        if (recovery.Action != ModpackUpdateRecoveryAction.CommittedAwaitingAcknowledgement ||
            recovery.TransactionId is not { } transactionId ||
            recovery.LaunchFields is not { } launchFields)
        {
            throw new InvalidDataException("Pending Core modpack update recovery is inconsistent.");
        }

        var updated = ApplyLaunchFields(registration, launchFields, journal.Target);
        await _registry.UpsertAsync(updated, CancellationToken.None).ConfigureAwait(false);
        journal = journal with
        {
            Revision = journal.Revision + 1,
            CoreTransactionId = transactionId,
            State = journal.CancellationRequested ||
                    journal.State == ProductServerModpackUpdateState.RollingBack
                ? ProductServerModpackUpdateState.RollingBack
                : journal.State == ProductServerModpackUpdateState.HealthyAwaitingStop
                    ? ProductServerModpackUpdateState.HealthyAwaitingStop
                    : ProductServerModpackUpdateState.AwaitingHealth,
            SessionId = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);

        if (journal.State == ProductServerModpackUpdateState.RollingBack)
        {
            await RollbackAsync(journal.UpdateId).ConfigureAwait(false);
        }
        else if (journal.State == ProductServerModpackUpdateState.HealthyAwaitingStop)
        {
            await AcknowledgeAsync(journal.UpdateId).ConfigureAwait(false);
        }
        else
        {
            _restartBlocker.Unblock(journal.ServerId);
        }
    }

    private void OnManualStopRequested(Guid serverId)
    {
        if (_runtime.GetStatus(serverId).SessionId is { } sessionId)
        {
            _manualStops[serverId] = sessionId;
        }
    }

    private void OnManualStopRequestCancelled(Guid serverId)
        => _manualStops.TryRemove(serverId, out _);

    private void OnConsoleLineObserved(object? sender, ConsoleLineReceivedEventArgs eventArgs)
    {
        if (!MinecraftServerReadinessDetector.IsReadyLine(eventArgs.Line.Text))
        {
            return;
        }

        var journal = FindPendingHealth(eventArgs.InstanceId);
        if (journal is null || journal.SessionId != eventArgs.SessionId)
        {
            return;
        }

        MarkHealthyInMemory(journal.UpdateId, eventArgs.SessionId);
    }

    private void OnStateChanged(object? sender, ServerStateChangedEventArgs eventArgs)
    {
        var journal = FindPendingHealth(eventArgs.InstanceId);
        if (journal is null)
        {
            return;
        }

        if (eventArgs.State == ServerState.Starting)
        {
            _manualStops.TryRemove(journal.ServerId, out _);
            SetSessionInMemory(journal.UpdateId, eventArgs.SessionId);
            return;
        }

        if (eventArgs.State == ServerState.Running)
        {
            SetSessionInMemory(journal.UpdateId, eventArgs.SessionId);
            StartStatusProbe(journal.UpdateId, eventArgs.SessionId);
            return;
        }

        if (eventArgs.State is not (ServerState.Stopped or ServerState.Crashed or ServerState.Faulted) ||
            journal.SessionId != eventArgs.SessionId)
        {
            return;
        }

        CancelProbe(journal.UpdateId);
        journal = GetJournal(journal.UpdateId);
        if (journal.State == ProductServerModpackUpdateState.HealthyAwaitingStop)
        {
            _manualStops.TryRemove(journal.ServerId, out _);
            QueueFinalization(journal.UpdateId, rollback: false);
            return;
        }

        if (_manualStops.TryGetValue(journal.ServerId, out var manualSessionId) &&
            manualSessionId == eventArgs.SessionId &&
            _manualStops.TryRemove(journal.ServerId, out _))
        {
            ClearSessionInMemory(journal.UpdateId, eventArgs.SessionId);
            return;
        }

        _restartBlocker.Block(journal.ServerId);
        MarkRollingBackInMemory(journal.UpdateId, eventArgs.SessionId);
        QueueFinalization(journal.UpdateId, rollback: true);
    }

    private void StartStatusProbe(Guid updateId, Guid sessionId)
    {
        CancelProbe(updateId);
        var owner = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (!_probeCancellation.TryAdd(updateId, owner))
        {
            owner.Dispose();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeTaskId = Guid.NewGuid();
        _probeTasks[probeTaskId] = completion.Task;
        _ = CompleteStatusProbeAsync(updateId, sessionId, probeTaskId, owner, completion);
    }

    private async Task CompleteStatusProbeAsync(
        Guid updateId,
        Guid sessionId,
        Guid probeTaskId,
        CancellationTokenSource owner,
        TaskCompletionSource completion)
    {
        try
        {
            await RunStatusProbeAsync(updateId, sessionId, owner).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            _probeTasks.TryRemove(
                new KeyValuePair<Guid, Task>(probeTaskId, completion.Task));
        }
    }

    private async Task RunStatusProbeAsync(
        Guid updateId,
        Guid sessionId,
        CancellationTokenSource owner)
    {
        try
        {
            // SetSessionInMemory establishes the absolute deadline before this task is queued.
            // Flush that state before waiting or probing so a Service crash cannot silently grant
            // another full health window to the same committed update.
            await PersistCurrentJournalAsync(updateId).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), owner.Token).ConfigureAwait(false);
            while (true)
            {
                owner.Token.ThrowIfCancellationRequested();
                var journal = GetJournal(updateId);
                if (journal.State != ProductServerModpackUpdateState.AwaitingHealth ||
                    journal.SessionId != sessionId ||
                    _runtime.GetStatus(journal.ServerId).SessionId != sessionId)
                {
                    return;
                }

                if (!_registry.TryGet(journal.ServerId, out var registration))
                {
                    return;
                }

                var deadline = journal.HealthDeadlineAtUtc
                    ?? throw new InvalidDataException(
                        "Running modpack health validation has no durable deadline.");
                var now = _timeProvider.GetUtcNow();
                if (now >= deadline)
                {
                    await HandleHealthTimeoutAsync(updateId, sessionId).ConfigureAwait(false);
                    return;
                }

                var result = await _statusProbe.ProbeAsync(
                        "127.0.0.1",
                        registration.Port,
                        TimeSpan.FromSeconds(3),
                        owner.Token)
                    .ConfigureAwait(false);
                if (result.IsHealthy)
                {
                    MarkHealthyInMemory(updateId, sessionId);
                    return;
                }

                var delay = deadline - _timeProvider.GetUtcNow();
                if (delay > TimeSpan.FromSeconds(2))
                {
                    delay = TimeSpan.FromSeconds(2);
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, owner.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            // Done-line recognition remains authoritative and active for the whole session.
        }
        finally
        {
            _probeCancellation.TryRemove(
                new KeyValuePair<Guid, CancellationTokenSource>(updateId, owner));
            owner.Dispose();
        }
    }

    private async Task HandleHealthTimeoutAsync(Guid updateId, Guid sessionId)
    {
        var journal = GetJournal(updateId);
        if (journal.State != ProductServerModpackUpdateState.AwaitingHealth ||
            journal.SessionId != sessionId ||
            _runtime.GetStatus(journal.ServerId).SessionId != sessionId)
        {
            return;
        }

        // Block restarts and make the rollback intent durable before asking the running process
        // to stop. The stop callback may queue the same finalization; the keyed finalization map
        // intentionally coalesces both paths into one rollback transaction.
        _restartBlocker.Block(journal.ServerId);
        journal = journal with
        {
            Revision = journal.Revision + 1,
            State = ProductServerModpackUpdateState.RollingBack,
            ErrorCode = "modpack_update.health_timeout",
            ErrorMessage = "The updated server did not become healthy before its validation deadline.",
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        await _runtime.PreventAutomaticRestartAsync(journal.ServerId, CancellationToken.None)
            .ConfigureAwait(false);

        var runtimeStatus = _runtime.GetStatus(journal.ServerId);
        if (runtimeStatus.SessionId == sessionId &&
            runtimeStatus.Server.State is not ProductServerState.Stopped)
        {
            await _runtime.StopAsync(journal.ServerId, CancellationToken.None).ConfigureAwait(false);
        }

        QueueFinalization(updateId, rollback: true);
    }

    private void QueueFinalization(Guid updateId, bool rollback)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_finalizations.TryAdd(updateId, completion.Task))
        {
            return;
        }

        _ = CompleteFinalizationAsync(updateId, rollback, completion);
    }

    private async Task CompleteFinalizationAsync(
        Guid updateId,
        bool rollback,
        TaskCompletionSource completion)
    {
        try
        {
            if (rollback)
            {
                await RollbackAsync(updateId).ConfigureAwait(false);
            }
            else
            {
                await AcknowledgeAsync(updateId).ConfigureAwait(false);
            }

            completion.TrySetResult();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await RecordFinalizationFailureAsync(updateId, error).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            _finalizations.TryRemove(
                new KeyValuePair<Guid, Task>(updateId, completion.Task));
        }
    }

    private async Task RollbackAsync(Guid updateId)
    {
        var journal = GetJournal(updateId);
        _restartBlocker.Block(journal.ServerId);
        await _runtime.PreventAutomaticRestartAsync(journal.ServerId, CancellationToken.None)
            .ConfigureAwait(false);
        await _runtime.ExecuteStoppedMutationAsync(
            journal.ServerId,
            async (registration, _) =>
            {
                var live = _runtime.CreateCoreInstance(registration);
                if (_transactions.HasPendingArtifacts(live))
                {
                    var recovery = await RetryDirectoryHandoffAsync(
                            () => _transactions.RecoverPendingAsync(live, CancellationToken.None))
                        .ConfigureAwait(false);
                    if (recovery.Action == ModpackUpdateRecoveryAction.CommittedAwaitingAcknowledgement &&
                        recovery.TransactionId is { } transactionId)
                    {
                        await _transactions.RequestCommittedRollbackAsync(
                                live,
                                transactionId,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        await _registry.UpsertAsync(
                                journal.PreviousRegistration,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        await RetryDirectoryHandoffAsync(async () =>
                            {
                                await _transactions.RollbackCommittedAsync(
                                        live,
                                        transactionId,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                                return true;
                            })
                            .ConfigureAwait(false);
                    }
                    else if (recovery.Action == ModpackUpdateRecoveryAction.RolledBack)
                    {
                        await _registry.UpsertAsync(
                                journal.PreviousRegistration,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidDataException("Core did not return a rollback-capable update journal.");
                    }
                }
                else
                {
                    await _registry.UpsertAsync(journal.PreviousRegistration, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                return true;
            },
            CancellationToken.None).ConfigureAwait(false);

        await CompleteRollbackJournalAsync(updateId, journal.CoreTransactionId).ConfigureAwait(false);
    }

    private async Task AcknowledgeAsync(Guid updateId)
    {
        var journal = GetJournal(updateId);
        await _runtime.ExecuteStoppedMutationAsync(
            journal.ServerId,
            async (registration, _) =>
            {
                var live = _runtime.CreateCoreInstance(registration);
                if (_transactions.HasPendingArtifacts(live))
                {
                    var transactionId = journal.CoreTransactionId
                        ?? throw new InvalidDataException("Healthy update journal lacks a transaction id.");
                    await RetryDirectoryHandoffAsync(async () =>
                        {
                            await _transactions.AcknowledgeCommitAsync(
                                    live,
                                    transactionId,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            return true;
                        })
                        .ConfigureAwait(false);
                }

                return true;
            },
            CancellationToken.None).ConfigureAwait(false);

        journal = GetJournal(updateId) with
        {
            Revision = GetJournal(updateId).Revision + 1,
            State = ProductServerModpackUpdateState.Completed,
            SessionId = null,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        // A terminal state is an observable completion barrier. Publish it only after every
        // filesystem cleanup and maintenance-block transition has completed, so callers never
        // observe Completed/RolledBack and race a still-blocked explicit start. If the process
        // stops before persistence, startup recovery safely repeats the preceding nonterminal
        // phase.
        await CleanupStagingAsync(updateId).ConfigureAwait(false);
        _restartBlocker.Unblock(journal.ServerId);
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task MarkCancelledAsync(Guid updateId)
    {
        var journal = GetJournal(updateId);
        if (_transactions.HasPendingArtifacts(_runtime.CreateCoreInstance(journal.PreviousRegistration)))
        {
            await MarkRollbackRequiredAsync(
                    updateId,
                    new IOException("Cancellation reached a durable Core transaction."))
                .ConfigureAwait(false);
            return;
        }

        journal = journal with
        {
            Revision = journal.Revision + 1,
            State = ProductServerModpackUpdateState.Cancelled,
            CancellationRequested = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await CleanupStagingAsync(updateId).ConfigureAwait(false);
        _restartBlocker.Unblock(journal.ServerId);
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HonorLateCancellationAsync(Guid updateId)
    {
        var journal = GetJournal(updateId);
        if (journal.IsTerminal)
        {
            return;
        }

        if (journal.State is ProductServerModpackUpdateState.AwaitingHealth
            or ProductServerModpackUpdateState.RollingBack)
        {
            journal = journal with
            {
                Revision = journal.Revision + 1,
                State = ProductServerModpackUpdateState.RollingBack,
                CancellationRequested = true,
                ErrorCode = "modpack_update.cancelled",
                ErrorMessage = "The committed update was cancelled before health validation completed.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _restartBlocker.Block(journal.ServerId);
            await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
            await RollbackAsync(updateId).ConfigureAwait(false);
            return;
        }

        await MarkCancelledAsync(updateId).ConfigureAwait(false);
    }

    private async Task HandleProcessFailureAsync(Guid updateId, Exception error)
    {
        var journal = GetJournal(updateId);
        var live = _runtime.CreateCoreInstance(journal.PreviousRegistration);
        if (_transactions.HasPendingArtifacts(live))
        {
            await MarkRollbackRequiredAsync(updateId, error).ConfigureAwait(false);
            return;
        }

        journal = journal with
        {
            Revision = journal.Revision + 1,
            State = ProductServerModpackUpdateState.Failed,
            ErrorCode = MapErrorCode(error),
            ErrorMessage = Truncate(error.Message, 512),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await CleanupStagingAsync(updateId).ConfigureAwait(false);
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task MarkRollbackRequiredAsync(Guid updateId, Exception error)
    {
        var journal = GetJournal(updateId) with
        {
            Revision = GetJournal(updateId).Revision + 1,
            State = ProductServerModpackUpdateState.RollingBack,
            ErrorCode = MapErrorCode(error),
            ErrorMessage = Truncate(error.Message, 512),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _restartBlocker.Block(journal.ServerId);
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        await RollbackAsync(updateId).ConfigureAwait(false);
    }

    private async Task CompleteRollbackJournalAsync(Guid updateId, Guid? transactionId)
    {
        var journal = GetJournal(updateId) with
        {
            Revision = GetJournal(updateId).Revision + 1,
            CoreTransactionId = transactionId ?? GetJournal(updateId).CoreTransactionId,
            State = ProductServerModpackUpdateState.RolledBack,
            SessionId = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        // Keep RollingBack externally visible until rollback cleanup and the restart-blocker
        // transition are both complete; terminal state therefore guarantees its postconditions.
        await CleanupStagingAsync(updateId).ConfigureAwait(false);
        _restartBlocker.Unblock(journal.ServerId);
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RecordFinalizationFailureAsync(Guid updateId, Exception error)
    {
        if (!_journals.TryGetValue(updateId, out var journal) || journal.IsTerminal)
        {
            return;
        }

        journal = journal with
        {
            Revision = journal.Revision + 1,
            ErrorCode = MapErrorCode(error),
            ErrorMessage = Truncate(error.Message, 512),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<UpdateJournal> TransitionAsync(
        UpdateJournal journal,
        ProductServerModpackUpdateState state)
    {
        journal = journal with
        {
            Revision = journal.Revision + 1,
            State = state,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        return journal;
    }

    private async Task SetBackupAndApplyingAsync(Guid updateId, string backupPath)
    {
        var journal = GetJournal(updateId) with
        {
            Revision = GetJournal(updateId).Revision + 1,
            State = ProductServerModpackUpdateState.Applying,
            BackupArchivePath = backupPath,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task MarkAwaitingHealthAsync(
        Guid updateId,
        Guid transactionId,
        string? backupPath)
    {
        var journal = GetJournal(updateId) with
        {
            Revision = GetJournal(updateId).Revision + 1,
            State = ProductServerModpackUpdateState.AwaitingHealth,
            CoreTransactionId = transactionId,
            SessionId = null,
            BackupArchivePath = backupPath ?? GetJournal(updateId).BackupArchivePath,
            CompletedBytes = GetJournal(updateId).TotalBytes,
            CompletedFiles = GetJournal(updateId).TotalFiles,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await PersistJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        _restartBlocker.Unblock(journal.ServerId);
    }

    private void SetSessionInMemory(Guid updateId, Guid sessionId)
    {
        if (!_journals.TryGetValue(updateId, out var journal) ||
            journal.State != ProductServerModpackUpdateState.AwaitingHealth)
        {
            return;
        }

        _journals[updateId] = journal with
        {
            Revision = journal.Revision + 1,
            SessionId = sessionId,
            HealthDeadlineAtUtc = journal.HealthDeadlineAtUtc ??
                                  _timeProvider.GetUtcNow().Add(_healthValidationTimeout),
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        _ = PersistCurrentJournalAsync(updateId);
    }

    private void ClearSessionInMemory(Guid updateId, Guid sessionId)
    {
        if (!_journals.TryGetValue(updateId, out var journal) || journal.SessionId != sessionId)
        {
            return;
        }

        _journals[updateId] = journal with
        {
            Revision = journal.Revision + 1,
            SessionId = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _ = PersistCurrentJournalAsync(updateId);
    }

    private void MarkHealthyInMemory(Guid updateId, Guid sessionId)
    {
        if (!_journals.TryGetValue(updateId, out var journal) ||
            journal.State != ProductServerModpackUpdateState.AwaitingHealth ||
            journal.SessionId != sessionId)
        {
            return;
        }

        _journals[updateId] = journal with
        {
            Revision = journal.Revision + 1,
            State = ProductServerModpackUpdateState.HealthyAwaitingStop,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _ = PersistCurrentJournalAsync(updateId);
        CancelProbe(updateId);
    }

    private void MarkRollingBackInMemory(Guid updateId, Guid sessionId)
    {
        if (!_journals.TryGetValue(updateId, out var journal) ||
            journal.State != ProductServerModpackUpdateState.AwaitingHealth ||
            journal.SessionId != sessionId)
        {
            return;
        }

        _journals[updateId] = journal with
        {
            Revision = journal.Revision + 1,
            State = ProductServerModpackUpdateState.RollingBack,
            ErrorCode = "modpack_update.health_failed",
            ErrorMessage = "The updated server stopped before first-launch health validation completed.",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _ = PersistCurrentJournalAsync(updateId);
    }

    private async Task PersistCurrentJournalAsync(Guid updateId)
    {
        try
        {
            await _journalGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_journals.TryGetValue(updateId, out var current))
                {
                    await WriteJournalAsync(current, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _journalGate.Release();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            // Core journal remains authoritative. Startup recovery conservatively retries health.
        }
    }

    private async Task PersistJournalAsync(UpdateJournal journal, CancellationToken cancellationToken)
    {
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            _journals[journal.UpdateId] = journal;
        }
        finally
        {
            _journalGate.Release();
        }
    }

    private ProductServerRegistration ValidateBegin(ProductServerModpackUpdateBeginRequest request)
    {
        if (request.ServerId == Guid.Empty || request.Target is null ||
            string.IsNullOrWhiteSpace(request.ExpectedCurrentVersionId))
        {
            throw new ArgumentException("Modpack update request is incomplete.", nameof(request));
        }

        if (!_registry.TryGet(request.ServerId, out var registration))
        {
            throw new KeyNotFoundException("The modpack update server is not registered.");
        }

        ValidateTarget(request.Target);
        if (registration.ModpackSource == ProductModpackSourceKind.None ||
            registration.ModpackSource != request.Target.ModpackSource ||
            !string.Equals(
                registration.ModpackProjectId,
                request.Target.ModpackProjectId,
                StringComparison.Ordinal) ||
            !string.Equals(
                registration.ModpackVersionId,
                request.ExpectedCurrentVersionId.Trim(),
                StringComparison.Ordinal) ||
            string.Equals(
                registration.ModpackVersionId,
                request.Target.ModpackVersionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Modpack update provenance/current-version precondition did not match the registry.");
        }

        return registration;
    }

    private static void ValidateTarget(ProductServerModpackUpdateDefinition target)
    {
        if (!Enum.IsDefined(target.LaunchKind) ||
            target.ModpackSource is not (ProductModpackSourceKind.Ftb
                or ProductModpackSourceKind.Modrinth
                or ProductModpackSourceKind.CurseForge) ||
            target.IsInstallerArtifact)
        {
            throw new ArgumentException("Modpack update target type is unsupported.");
        }

        ValidateText(target.CoreType, 64, "Core type");
        if (!Enum.TryParse<CoreType>(target.CoreType, ignoreCase: true, out var coreType) ||
            !Enum.IsDefined(coreType))
        {
            throw new ArgumentException("Modpack update target core type is unsupported.");
        }

        ValidateText(target.ModpackProjectId, 256, "Modpack project id");
        ValidateText(target.ModpackVersionId, 256, "Modpack version id");
        ValidateText(target.ModpackVersionName, 256, "Modpack version name");
        ValidateOptionalText(target.ModpackProviderId, 64, "Modpack provider id");
        ValidateOptionalText(target.MinecraftVersion, 64, "Minecraft version");
        if (target.LaunchKind == ProductServerLaunchKind.ExecutableJar)
        {
            ValidateRelativePath(target.ServerJarPath);
            if (!Path.GetExtension(target.ServerJarPath).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Executable modpack targets require a JAR path.");
            }
        }
        else if (!string.IsNullOrEmpty(target.ServerJarPath))
        {
            throw new ArgumentException("Java argument-file targets cannot contain a Server JAR path.");
        }

        if (target.JavaArgumentFilePaths is null || target.JavaArgumentFilePaths.Count > 128 ||
            target.ServerArguments is null || target.ServerArguments.Count > 128)
        {
            throw new ArgumentException("Modpack update argument limits were exceeded.");
        }

        foreach (var path in target.JavaArgumentFilePaths)
        {
            ValidateRelativePath(path);
        }

        if (target.LaunchKind == ProductServerLaunchKind.JavaArgumentFiles &&
            target.JavaArgumentFilePaths.Count == 0)
        {
            throw new ArgumentException("Java argument-file targets require at least one file.");
        }

        foreach (var argument in target.ServerArguments)
        {
            ValidateText(argument, 2048, "Server argument");
        }
    }

    private static void ValidateRegistrationStillMatches(
        UpdateJournal journal,
        ProductServerRegistration registration)
    {
        if (registration.Id != journal.ServerId ||
            !string.Equals(
                registration.ModpackVersionId,
                journal.ExpectedCurrentVersionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                registration.ModpackProjectId,
                journal.Target.ModpackProjectId,
                StringComparison.Ordinal) ||
            registration.ModpackSource != journal.Target.ModpackSource)
        {
            throw new InvalidOperationException(
                "Server registration changed after the modpack update was staged.");
        }
    }

    private ServerInstance CreateCandidate(
        UpdateJournal journal,
        ProductServerRegistration registration)
    {
        var live = _runtime.CreateCoreInstance(registration);
        return new ServerInstance
        {
            Id = journal.UpdateId,
            Name = registration.Name + " update candidate",
            DirectoryPath = PrivateCandidateDirectory(journal.UpdateId),
            ServerJarPath = journal.Target.ServerJarPath,
            LaunchKind = (ServerLaunchKind)journal.Target.LaunchKind,
            JavaArgumentFilePaths = journal.Target.JavaArgumentFilePaths.ToList(),
            CoreType = Enum.Parse<CoreType>(journal.Target.CoreType, ignoreCase: true),
            MinecraftVersion = journal.Target.MinecraftVersion,
            JavaMajorVersion = live.JavaMajorVersion,
            JavaExecutablePath = live.JavaExecutablePath,
            MinimumMemoryMb = live.MinimumMemoryMb,
            MaximumMemoryMb = live.MaximumMemoryMb,
            JvmArguments = [.. live.JvmArguments],
            ServerArguments = [.. journal.Target.ServerArguments],
            StopCommand = live.StopCommand,
            Port = live.Port,
            ModpackProviderId = journal.Target.ModpackProviderId,
            ModpackSource = (ModpackSourceKind)journal.Target.ModpackSource,
            ModpackProjectId = journal.Target.ModpackProjectId,
            ModpackVersionId = journal.Target.ModpackVersionId,
            ModpackVersionName = journal.Target.ModpackVersionName,
            IsInstallerArtifact = false,
        };
    }

    private ProductServerRegistration ApplyLaunchFields(
        ProductServerRegistration registration,
        ModpackUpdateLaunchFields fields,
        ProductServerModpackUpdateDefinition target)
    {
        var liveRoot = ProductServerRegistrationValidator.ResolveOwnedPath(
            _layout.Servers,
            registration.ServerDirectory,
            allowRoot: false);
        var jar = string.IsNullOrWhiteSpace(fields.ServerJarPath)
            ? registration.ServerJarPath
            : NormalizeRelativePath(Path.GetRelativePath(liveRoot, fields.ServerJarPath));
        var updated = registration with
        {
            LaunchKind = (ProductServerLaunchKind)fields.LaunchKind,
            ServerJarPath = jar,
            JavaArgumentFilePaths = fields.JavaArgumentFilePaths.ToArray(),
            CoreType = fields.CoreType.ToString(),
            MinecraftVersion = fields.MinecraftVersion,
            ServerArguments = fields.ServerArguments.ToArray(),
            ModpackProviderId = target.ModpackProviderId,
            ModpackSource = target.ModpackSource,
            ModpackProjectId = target.ModpackProjectId,
            ModpackVersionId = target.ModpackVersionId,
            ModpackVersionName = target.ModpackVersionName,
            IsInstallerArtifact = false,
        };
        ProductServerRegistrationValidator.ValidateAndThrow(updated, _layout);
        return updated;
    }

    private async Task VerifyCandidateAsync(
        Guid updateId,
        ProductServerModpackUpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        EnsureStagingCapability(updateId);
        var candidate = CandidateDirectory(updateId);
        var privateCandidate = PrivateCandidateDirectory(updateId);
        if (Directory.Exists(privateCandidate) || File.Exists(privateCandidate))
        {
            await DeleteOwnedTreeAsync(PrivatePayloadRoot, privateCandidate).ConfigureAwait(false);
        }

        Directory.CreateDirectory(privateCandidate);
        SafePath.EnsureNoReparsePointsUnderRoot(PrivatePayloadRoot, privateCandidate);
        var actual = EnumerateFilesNoFollow(candidate)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(candidate, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = manifest.Files.Select(value => value.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException("Candidate payload does not exactly match its manifest.");
        }

        var completedFiles = 0;
        long completedBytes = 0;
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = SafePath.EnsureWithinRoot(candidate, entry.Path, allowRoot: false);
            await using var lease = ProductNoFollowFileReader.Open(_layout.Imports, source);
            if (lease.Stream.Length != entry.Length)
            {
                throw new InvalidDataException("Candidate payload file length changed.");
            }

            var destination = SafePath.EnsureWithinRoot(
                privateCandidate,
                entry.Path,
                allowRoot: false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SafePath.EnsureNoReparsePointsUnderRoot(
                PrivatePayloadRoot,
                Path.GetDirectoryName(destination)!);
            var hash = await CopyAndHashAsync(
                    lease.Stream,
                    destination,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, ParseSha256(entry.Sha256)))
            {
                throw new InvalidDataException("Candidate payload file hash does not match.");
            }

            completedFiles++;
            completedBytes = checked(completedBytes + entry.Length);
            if (completedFiles % 32 == 0 || completedFiles == manifest.Files.Count)
            {
                var journal = GetJournal(updateId) with
                {
                    CompletedFiles = completedFiles,
                    CompletedBytes = completedBytes,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                await PersistJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            }
        }

        SafePath.EnsureTreeContainsNoReparsePoints(privateCandidate);
    }

    private static async Task<byte[]> CopyAndHashAsync(
        Stream source,
        string destination,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return hash.GetHashAndReset();
        }
        catch
        {
            try
            {
                File.Delete(destination);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async Task<ManifestRead> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var lease = ProductNoFollowFileReader.Open(_layout.Imports, path);
        if (lease.Stream.Length is < 2 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("Modpack update manifest size is invalid.");
        }

        var hash = await SHA256.HashDataAsync(lease.Stream, cancellationToken).ConfigureAwait(false);
        lease.Stream.Position = 0;
        try
        {
            var manifest = await JsonSerializer.DeserializeAsync<ProductServerModpackUpdateManifest>(
                    lease.Stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Modpack update manifest is empty.");
            return new ManifestRead(manifest, hash);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Modpack update manifest JSON is invalid.", error);
        }
    }

    private static (long TotalBytes, int TotalFiles) ValidateManifest(
        ProductServerModpackUpdateManifest manifest,
        Guid updateId)
    {
        if (manifest.SchemaVersion != 1 || manifest.UpdateId != updateId ||
            manifest.Files is null || manifest.Files.Count is < 1 or > MaximumFiles)
        {
            throw new InvalidDataException("Modpack update manifest header or file count is invalid.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in manifest.Files)
        {
            if (entry is null)
            {
                throw new InvalidDataException("Modpack update manifest has an empty entry.");
            }

            ValidateRelativePath(entry.Path);
            var normalized = NormalizeRelativePath(entry.Path);
            if (!string.Equals(entry.Path, normalized, StringComparison.Ordinal) || !paths.Add(normalized) ||
                entry.Length < 0 || entry.Length > MaximumBytes)
            {
                throw new InvalidDataException("Modpack update manifest entry is invalid.");
            }

            _ = ParseSha256(entry.Sha256);
            total = checked(total + entry.Length);
            if (total > MaximumBytes)
            {
                throw new InvalidDataException("Modpack update manifest exceeds its byte limit.");
            }
        }

        return (total, manifest.Files.Count);
    }

    private UpdateJournal? FindPendingHealth(Guid serverId)
        => _journals.Values.FirstOrDefault(value =>
            value.ServerId == serverId &&
            value.State is ProductServerModpackUpdateState.AwaitingHealth
                or ProductServerModpackUpdateState.HealthyAwaitingStop
                or ProductServerModpackUpdateState.RollingBack);

    private async Task CleanupOrphansAsync(CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(StagingRoot, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var known = Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) &&
                        _journals.TryGetValue(id, out var journal) && !journal.IsTerminal;
            if (!known)
            {
                await DeleteOwnedTreeAsync(StagingRoot, directory).ConfigureAwait(false);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     PrivatePayloadRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var known = Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) &&
                        _journals.TryGetValue(id, out var journal) && !journal.IsTerminal;
            if (!known)
            {
                await DeleteOwnedTreeAsync(PrivatePayloadRoot, directory).ConfigureAwait(false);
            }
        }
    }

    private async Task CleanupStagingAsync(Guid updateId)
    {
        var path = StagingDirectory(updateId);
        if (Directory.Exists(path) || File.Exists(path))
        {
            await DeleteOwnedTreeAsync(StagingRoot, path).ConfigureAwait(false);
        }


        var privatePath = PrivateCandidateDirectory(updateId);
        if (Directory.Exists(privatePath) || File.Exists(privatePath))
        {
            await DeleteOwnedTreeAsync(PrivatePayloadRoot, privatePath).ConfigureAwait(false);
        }
    }

    private static Task DeleteOwnedTreeAsync(string root, string path)
        => SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
            root,
            path,
            CancellationToken.None);

    private void EnsureStagingCapability(Guid updateId)
    {
        var staging = SafePath.EnsureWithinRoot(StagingRoot, StagingDirectory(updateId), allowRoot: false);
        SafePath.EnsureNoReparsePointsUnderRoot(StagingRoot, staging);
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
                    throw new InvalidDataException("Modpack candidate cannot contain a reparse point.");
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

    private async Task<UpdateJournal> ReadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 2 or > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("Modpack update journal size is invalid.");
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
            return await JsonSerializer.DeserializeAsync<UpdateJournal>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   .ConfigureAwait(false)
                   ?? throw new InvalidDataException("Modpack update journal is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Modpack update journal JSON is invalid.", error);
        }
    }

    private async Task WriteJournalAsync(UpdateJournal journal, CancellationToken cancellationToken)
    {
        await WriteAtomicJsonAsync(JournalPath(journal.UpdateId), journal, cancellationToken)
            .ConfigureAwait(false);
        if (journal.IsTerminal &&
            _announcedTerminalStates.TryAdd(journal.UpdateId, journal.State))
        {
            TerminalStatePersisted?.Invoke(this, ToStatus(journal));
        }
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

    private void ValidateJournal(UpdateJournal journal)
    {
        if (journal is null || journal.Target is null || journal.PreviousRegistration is null ||
            journal.SchemaVersion != 1 || journal.Revision < 1 || journal.UpdateId == Guid.Empty ||
            journal.ServerId == Guid.Empty || !Enum.IsDefined(journal.State) ||
            journal.PreviousRegistration.Id != journal.ServerId ||
            (journal.HealthDeadlineAtUtc is { } deadline && deadline.Offset != TimeSpan.Zero))
        {
            throw new InvalidDataException("Modpack update journal is invalid.");
        }

        ValidateTarget(journal.Target);
        ProductServerRegistrationValidator.ValidateAndThrow(journal.PreviousRegistration, _layout);
    }

    private UpdateJournal GetJournal(Guid updateId)
        => updateId != Guid.Empty && _journals.TryGetValue(updateId, out var journal)
            ? journal
            : throw new KeyNotFoundException("Modpack update transaction was not found.");

    private ProductServerModpackUpdateStatus ToStatus(UpdateJournal journal)
        => new(
            journal.UpdateId,
            journal.ServerId,
            journal.State,
            journal.State == ProductServerModpackUpdateState.Staging
                ? StagingDirectory(journal.UpdateId)
                : null,
            journal.TotalBytes,
            journal.CompletedBytes,
            journal.TotalFiles,
            journal.CompletedFiles,
            journal.BackupArchivePath,
            journal.ErrorCode,
            journal.ErrorMessage,
            journal.UpdatedAtUtc);

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new InvalidOperationException("Modpack update coordinator is not initialized.");
        }

        if (_shutdown.IsCancellationRequested)
        {
            throw new InvalidOperationException("Modpack update coordinator is shutting down.");
        }
    }

    private static async Task<T> RetryDirectoryHandoffAsync<T>(Func<Task<T>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (ServerDirectoryLockException) when (attempt < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
        }
    }

    private void CancelProbe(Guid updateId)
    {
        if (_probeCancellation.TryRemove(updateId, out var owner))
        {
            owner.Cancel();
        }
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
            return Convert.FromHexString(value);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("SHA-256 value is invalid.", error);
        }
    }

    private static void ValidateRelativePath(string value)
    {
        ValidateText(value, 512, "Candidate path");
        if (value.Contains('\\') || value.StartsWith('/') || value.EndsWith('/') ||
            Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException("Candidate path is not a canonical relative path.");
        }

        foreach (var segment in value.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("Candidate path contains an unsafe segment.");
            }
        }
    }

    private static void ValidateText(string value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(character => character is '\0' or '\r' or '\n' || char.IsControl(character)))
        {
            throw new ArgumentException($"{label} is invalid.");
        }
    }

    private static void ValidateOptionalText(string? value, int maximum, string label)
    {
        if (value is not null)
        {
            ValidateText(value, maximum, label);
        }
    }

    private static void RejectReparse(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("Expected modpack update path was not found.", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Modpack update path cannot be a reparse point.");
        }
    }

    private static void EnsurePathDoesNotExist(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Modpack update destination already exists.");
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string MapErrorCode(Exception error) => error switch
    {
        InvalidDataException or CryptographicException => "modpack_update.integrity_failed",
        UnauthorizedAccessException => "modpack_update.access_denied",
        OperationCanceledException => "modpack_update.cancelled",
        IOException when error.Message.Contains("space", StringComparison.OrdinalIgnoreCase) =>
            "modpack_update.disk_insufficient",
        IOException => "modpack_update.io_failed",
        ArgumentException => "modpack_update.definition_invalid",
        InvalidOperationException => "modpack_update.precondition_failed",
        _ => "modpack_update.failed",
    };

    private static string? Truncate(string? value, int maximum)
        => value is null || value.Length <= maximum ? value : value[..maximum];

    private string StagingRoot => Path.Combine(_layout.Imports, "modpack-updates");
    private string JournalRoot => Path.Combine(_layout.Operations, "modpack-updates");
    private string PrivatePayloadRoot => Path.Combine(JournalRoot, "payloads");
    private string StagingDirectory(Guid id) => Path.Combine(StagingRoot, id.ToString("N"));
    private string CandidateDirectory(Guid id) => Path.Combine(StagingDirectory(id), "candidate");
    private string PrivateCandidateDirectory(Guid id) => Path.Combine(PrivatePayloadRoot, id.ToString("N"));
    private string ManifestPath(Guid id) => Path.Combine(StagingDirectory(id), ManifestFileName);
    private string JournalPath(Guid id) => Path.Combine(JournalRoot, $"{id:N}.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 24,
    };

    private sealed record UpdateJournal(
        int SchemaVersion,
        long Revision,
        Guid UpdateId,
        Guid ServerId,
        string ExpectedCurrentVersionId,
        ProductServerModpackUpdateDefinition Target,
        ProductServerRegistration PreviousRegistration,
        ProductServerModpackUpdateState State,
        string? ManifestSha256,
        Guid? CoreTransactionId,
        Guid? SessionId,
        long TotalBytes,
        long CompletedBytes,
        int TotalFiles,
        int CompletedFiles,
        string? BackupArchivePath,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        bool CancellationRequested = false,
        DateTimeOffset? HealthDeadlineAtUtc = null)
    {
        public bool IsTerminal => State is ProductServerModpackUpdateState.Completed
            or ProductServerModpackUpdateState.RolledBack
            or ProductServerModpackUpdateState.Cancelled
            or ProductServerModpackUpdateState.Failed;
    }

    private sealed record ManifestRead(
        ProductServerModpackUpdateManifest Manifest,
        byte[] Sha256);
}
