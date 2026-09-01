using System.Collections.Concurrent;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Service ownership boundary for the shared Core process manager. No process orchestration is
/// duplicated here; this class maps durable product contracts to the Core engine.
/// </summary>
public sealed class ProductServerRuntime : IAsyncDisposable
{
    public const int RetainedConsoleLinesPerServer = 2_000;

    // The Service publishes its own cursor journal, so the Core manager only needs a small local
    // diagnostic tail. This avoids retaining a second copy of thousands of potentially 64 KiB
    // Java lines while preserving Core's classifier/session behavior.
    public const int CoreRetainedConsoleLinesPerServer = 100;
    private readonly ProductServerRegistry _registry;
    private readonly ProductDataLayout _layout;
    private readonly ServerProcessManager _processManager;
    private readonly ProductDesiredRunIntentStore _desiredRunIntent;
    private readonly ProductServerRestartBlocker _restartBlocker;
    private readonly ProductServerEulaCoordinator? _eulaCoordinator;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _operationGates = new();
    private readonly ConcurrentDictionary<Guid, ProductConsoleJournal> _journals = new();
    private int _shutdown;

    public ProductServerRuntime(
        ProductServerRegistry registry,
        ProductDataLayout layout,
        ServerProcessManager processManager,
        ProductDesiredRunIntentStore desiredRunIntent,
        ProductServerRestartBlocker? restartBlocker = null,
        ProductServerEulaCoordinator? eulaCoordinator = null)
    {
        _registry = registry;
        _layout = layout;
        _processManager = processManager;
        _desiredRunIntent = desiredRunIntent;
        _restartBlocker = restartBlocker ?? new ProductServerRestartBlocker();
        _eulaCoordinator = eulaCoordinator;
        _processManager.ConsoleLineReceived += OnConsoleLineReceived;
        _processManager.StateChanged += OnStateChanged;
    }

    internal event EventHandler<ConsoleLineReceivedEventArgs>? ConsoleLineObserved;

    internal event EventHandler<ServerStateChangedEventArgs>? StateChanged;

    internal event Action<Guid>? ManualStopRequested;

    internal event Action<Guid>? ManualStopRequestCancelled;

    public IReadOnlyList<ProductServerSummary> List()
        => _registry.GetAll().Select(ToSummary).ToArray();

    public ProductServerStatus GetStatus(Guid serverId)
    {
        var registration = GetRegistration(serverId);
        return ToStatus(registration);
    }

    public async Task UpsertAsync(
        ProductServerRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        var gate = GetGate(registration.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _processManager.ExecuteWhileInactiveAsync(
                    registration.Id,
                    token => _registry.UpsertAsync(registration, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProductServerSettingsUpdateResult> UpdateSettingsAsync(
        Guid serverId,
        ProductServerSettingsUpdateRequest settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfShuttingDown();
        var gate = GetGate(serverId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _processManager.ExecuteWhileInactiveAsync(
                    serverId,
                    async token =>
                    {
                        var current = GetRegistration(serverId);
                        var updated = current with
                        {
                            Name = settings.Name,
                            MinimumMemoryMb = settings.MinimumMemoryMb,
                            MaximumMemoryMb = settings.MaximumMemoryMb,
                            Port = settings.Port,
                            AutoRestart = settings.AutoRestart,
                        };
                        await _registry.UpsertAsync(updated, token).ConfigureAwait(false);
                        return new ProductServerSettingsUpdateResult(updated, ToStatus(updated));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        var gate = GetGate(serverId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _processManager.ExecuteWhileInactiveAsync(
                    serverId,
                    async token =>
                    {
                        // Clear restart intent before deleting the registration. A crash between
                        // these commits can leave a harmless stopped registration, never an
                        // orphaned auto-start.
                        await _desiredRunIntent.SetDesiredAsync(serverId, false, token)
                            .ConfigureAwait(false);
                        var removed = await _registry.RemoveAsync(serverId, token).ConfigureAwait(false);
                        if (removed)
                        {
                            _journals.TryRemove(serverId, out _);
                        }

                        return removed;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Resolves the absolute directory only for the ACL-protected local administrator IPC. The
    /// durable registry remains relative-path-only and the Web API never receives this payload.
    /// </summary>
    public ProductServerDirectoryInfo GetDirectoryInfo(Guid serverId)
    {
        var registration = GetRegistration(serverId);
        var directory = ResolveServerDirectory(registration);
        if (File.Exists(directory) && !Directory.Exists(directory))
        {
            throw new InvalidDataException("The registered server path is not a directory.");
        }

        var exists = Directory.Exists(directory);
        if (exists)
        {
            _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, directory);
        }

        return new ProductServerDirectoryInfo(serverId, directory, exists);
    }

    /// <summary>
    /// Stops the Service-owned process, blocks automatic restart, and removes exactly the
    /// registered tree through the Core no-follow deleter. The registry is committed only after
    /// the owned directory is absent.
    /// </summary>
    public async Task<ProductServerDeletionResult> DeletePermanentlyAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        _restartBlocker.Block(serverId);
        var gate = GetGate(serverId);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var registration = GetRegistration(serverId);
                await _desiredRunIntent.SetDesiredAsync(serverId, false, cancellationToken)
                    .ConfigureAwait(false);
                if (_processManager.TryGetSnapshot(serverId, out var running) &&
                    running.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
                {
                    ManualStopRequested?.Invoke(serverId);
                }

                await _processManager.StopAsync(serverId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (_processManager.TryGetSnapshot(serverId, out var stopped) &&
                    stopped.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
                {
                    throw new InvalidOperationException(
                        "The server process is still active; permanent deletion was cancelled.");
                }

                var directory = ResolveServerDirectory(registration);
                if (File.Exists(directory) && !Directory.Exists(directory))
                {
                    throw new InvalidDataException("The registered server path is not a directory.");
                }

                if (Directory.Exists(directory))
                {
                    _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, directory);
                    using var expectedIdentity = SafePath.CaptureExistingObjectIdentityLease(directory);
                    var protectedIdentities = CaptureOtherManagedDirectoryIdentities(serverId);
                    await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                            _layout.Servers,
                            directory,
                            expectedIdentity.Identity,
                            protectedIdentities,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (Directory.Exists(directory) || File.Exists(directory))
                {
                    throw new IOException("The Service-owned server directory was not fully deleted.");
                }

                if (!await _registry.RemoveAsync(serverId, cancellationToken).ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Server '{serverId}' is not registered.");
                }

                _journals.TryRemove(serverId, out _);
                return new ProductServerDeletionResult(
                    serverId,
                    Deleted: true,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            _restartBlocker.Unblock(serverId);
        }
    }

    public async Task<ProductServerMutationResult> StartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => await StartAsync(
                serverId,
                acceptMinecraftEula: false,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<ProductServerMutationResult> StartAsync(
        Guid serverId,
        bool acceptMinecraftEula,
        CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        var gate = GetGate(serverId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureEulaContextAvailable(acceptMinecraftEula);
            var registration = GetRegistration(serverId);
            if (_restartBlocker.IsBlocked(serverId))
            {
                throw new InvalidOperationException(
                    "The server is temporarily blocked by a fail-closed maintenance operation.");
            }

            if (_processManager.TryGetSnapshot(serverId, out var existing) &&
                existing.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
            {
                await _desiredRunIntent.SetDesiredAsync(serverId, true, cancellationToken)
                    .ConfigureAwait(false);
                return new ProductServerMutationResult(serverId, false, ToStatus(registration));
            }

            var started = false;
            try
            {
                await _processManager.StartAsync(
                        ToCoreInstance(registration),
                        new ServerStartContext(acceptMinecraftEula),
                        cancellationToken)
                    .ConfigureAwait(false);
                started = true;
                // Commit desired=true only after the Core process manager accepted the launch.
                await _desiredRunIntent.SetDesiredAsync(serverId, true, cancellationToken)
                    .ConfigureAwait(false);
                return new ProductServerMutationResult(
                    serverId,
                    true,
                    ToStatus(GetRegistration(serverId)));
            }
            catch
            {
                if (started)
                {
                    await CompensateUncommittedStartAsync(serverId).ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProductServerMutationResult> StopAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        var gate = GetGate(serverId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registration = GetRegistration(serverId);
            // Explicit stop intent wins even if graceful process shutdown subsequently fails.
            // This prevents a Service restart from undoing the operator's stop request.
            await _desiredRunIntent.SetDesiredAsync(serverId, false, cancellationToken)
                .ConfigureAwait(false);
            if (_processManager.TryGetSnapshot(serverId, out var existing) &&
                existing.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
            {
                ManualStopRequested?.Invoke(serverId);
            }

            var stopped = await _processManager.StopAsync(serverId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new ProductServerMutationResult(serverId, stopped, ToStatus(registration));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProductServerMutationResult> RestartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => await RestartAsync(
                serverId,
                acceptMinecraftEula: false,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<ProductServerMutationResult> RestartAsync(
        Guid serverId,
        bool acceptMinecraftEula,
        CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        var gate = GetGate(serverId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureEulaContextAvailable(acceptMinecraftEula);
            var registration = GetRegistration(serverId);
            var startContext = new ServerStartContext(acceptMinecraftEula);
            if (_eulaCoordinator is not null)
            {
                await _eulaCoordinator.EnsureRestartMayProceedAsync(
                        ToCoreInstance(registration),
                        startContext,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            await _desiredRunIntent.LoadAsync(cancellationToken).ConfigureAwait(false);
            var wasDesired = _desiredRunIntent.IsDesired(serverId);
            // A restart never clears an existing desired=true commit during its stop/start gap.
            if (_processManager.TryGetSnapshot(serverId, out var existing) &&
                existing.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
            {
                ManualStopRequested?.Invoke(serverId);
            }

            await _processManager.StopAsync(serverId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var started = false;
            try
            {
                await _processManager.StartAsync(
                        ToCoreInstance(registration),
                        startContext,
                        cancellationToken)
                    .ConfigureAwait(false);
                started = true;
                await _desiredRunIntent.SetDesiredAsync(serverId, true, cancellationToken)
                    .ConfigureAwait(false);
                return new ProductServerMutationResult(
                    serverId,
                    true,
                    ToStatus(GetRegistration(serverId)));
            }
            catch
            {
                if (started && !wasDesired)
                {
                    await CompensateUncommittedStartAsync(serverId).ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureEulaContextAvailable(bool acceptMinecraftEula)
    {
        if (acceptMinecraftEula && _eulaCoordinator is null)
        {
            throw new InvalidOperationException(
                "Minecraft EULA confirmation cannot be applied because launch preflight is unavailable.");
        }
    }

    public async Task SendCommandAsync(
        Guid serverId,
        string command,
        CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        var registration = GetRegistration(serverId);
        if (command.Length > 512)
        {
            throw new ArgumentException("Server command exceeds its length limit.", nameof(command));
        }

        var normalized = command.Trim();
        var stopCommand = string.IsNullOrWhiteSpace(registration.StopCommand)
            ? "stop"
            : registration.StopCommand.Trim();
        var manualStop = string.Equals(normalized, stopCommand, StringComparison.OrdinalIgnoreCase) &&
                         _processManager.TryGetSnapshot(serverId, out var existing) &&
                         existing.State is ServerState.Starting or ServerState.Running or ServerState.Stopping;
        if (manualStop)
        {
            ManualStopRequested?.Invoke(serverId);
        }

        try
        {
            await _processManager.SendCommandAsync(serverId, command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (manualStop)
            {
                ManualStopRequestCancelled?.Invoke(serverId);
            }

            throw;
        }
    }

    public ProductConsolePage ReadConsole(Guid serverId, long afterCursor, int limit)
    {
        _ = GetRegistration(serverId);
        return _journals
            .GetOrAdd(serverId, _ => new ProductConsoleJournal(RetainedConsoleLinesPerServer))
            .Read(serverId, afterCursor, limit);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }

        _processManager.ConsoleLineReceived -= OnConsoleLineReceived;
        _processManager.StateChanged -= OnStateChanged;
        await _processManager.StopAllAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ProductDesiredServerRestoreResult> RestoreIfDesiredAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        var gate = GetGate(serverId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // This check shares the same per-server gate as explicit stop. Whichever operation
            // commits first determines the outcome without a stale startup snapshot race.
            if (!_desiredRunIntent.IsDesired(serverId))
            {
                return ProductDesiredServerRestoreResult.NotDesired;
            }

            if (!_registry.TryGet(serverId, out var registration))
            {
                return ProductDesiredServerRestoreResult.MissingRegistration;
            }

            if (_processManager.TryGetSnapshot(serverId, out var existing) &&
                existing.State is ServerState.Starting or ServerState.Running or ServerState.Stopping)
            {
                return ProductDesiredServerRestoreResult.AlreadyRunning;
            }

            await _processManager.StartAsync(ToCoreInstance(registration), cancellationToken)
                .ConfigureAwait(false);
            return ProductDesiredServerRestoreResult.Restored;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _processManager.DisposeAsync().ConfigureAwait(false);
            foreach (var gate in _operationGates.Values)
            {
                gate.Dispose();
            }
        }
    }

    public ProductServerRegistration GetRegistration(Guid serverId)
        => _registry.TryGet(serverId, out var registration)
            ? registration
            : throw new KeyNotFoundException($"Server '{serverId}' is not registered.");

    private ProductServerSummary ToSummary(ProductServerRegistration registration)
    {
        var state = _processManager.TryGetSnapshot(registration.Id, out var snapshot)
            ? (ProductServerState)snapshot.State
            : ProductServerState.Stopped;
        return new ProductServerSummary(
            registration.Id,
            registration.Name,
            state,
            registration.Port,
            registration.CoreType,
            registration.MinecraftVersion);
    }

    private ProductServerStatus ToStatus(ProductServerRegistration registration)
    {
        if (!_processManager.TryGetSnapshot(registration.Id, out var snapshot))
        {
            return new ProductServerStatus(
                ToSummary(registration),
                null,
                null,
                null,
                null,
                null,
                null);
        }

        var resource = snapshot.LastResourceSample is { } sample
            ? new ProductServerResourceSample(
                sample.Timestamp,
                sample.CpuPercent,
                sample.WorkingSetBytes,
                sample.PrivateMemoryBytes,
                sample.Uptime)
            : null;
        return new ProductServerStatus(
            ToSummary(registration),
            snapshot.SessionId,
            snapshot.ProcessId,
            snapshot.StartedAtUtc,
            snapshot.LastExitCode,
            resource,
            Truncate(snapshot.LastError?.Message, 512));
    }

    private ServerInstance ToCoreInstance(ProductServerRegistration registration)
    {
        ProductServerRegistrationValidator.ValidateAndThrow(registration, _layout);
        var serverDirectory = ProductServerRegistrationValidator.ResolveOwnedPath(
            _layout.Servers,
            registration.ServerDirectory,
            allowRoot: false);
        var javaExecutable = ProductServerRegistrationValidator.ResolveOwnedPath(
            _layout.Runtimes,
            registration.JavaRuntimePath,
            allowRoot: false);
        if (!Directory.Exists(serverDirectory))
        {
            throw new DirectoryNotFoundException(
                "The Service-owned server directory was not found: " + serverDirectory);
        }

        if (!File.Exists(javaExecutable))
        {
            throw new FileNotFoundException(
                "The Service-owned Java executable was not found.",
                javaExecutable);
        }

        SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, serverDirectory);
        SafePath.EnsureNoReparsePointsUnderRoot(_layout.Runtimes, javaExecutable);

        var instance = new ServerInstance();
        ApplyRegistrationLaunchSnapshot(instance, registration, _layout);
        return instance;
    }

    internal static void ApplyRegistrationLaunchSnapshot(
        ServerInstance instance,
        ProductServerRegistration registration,
        ProductDataLayout layout)
    {
        ProductServerRegistrationValidator.ValidateAndThrow(registration, layout);
        var serverDirectory = ProductServerRegistrationValidator.ResolveOwnedPath(
            layout.Servers,
            registration.ServerDirectory,
            allowRoot: false);
        var javaExecutable = ProductServerRegistrationValidator.ResolveOwnedPath(
            layout.Runtimes,
            registration.JavaRuntimePath,
            allowRoot: false);
        if (!Enum.TryParse<CoreType>(registration.CoreType, ignoreCase: true, out var coreType)
            || !Enum.IsDefined(coreType))
        {
            throw new InvalidDataException("Stored core type is unsupported.");
        }

        instance.Id = registration.Id;
        instance.Name = registration.Name;
        instance.DirectoryPath = serverDirectory;
        instance.JavaExecutablePath = javaExecutable;
        instance.LaunchKind = (ServerLaunchKind)registration.LaunchKind;
        // Core launch models use an absolute JAR path. The public registry deliberately keeps
        // only a root-confined relative path, so resolve it at this ownership boundary.
        instance.ServerJarPath = SafePath.EnsureWithinRoot(
            serverDirectory,
            registration.ServerJarPath,
            allowRoot: false);
        instance.JavaArgumentFilePaths = registration.JavaArgumentFilePaths.ToList();
        instance.CoreType = coreType;
        instance.MinecraftVersion = registration.MinecraftVersion;
        instance.MinimumMemoryMb = registration.MinimumMemoryMb;
        instance.MaximumMemoryMb = registration.MaximumMemoryMb;
        instance.JvmArguments = registration.JvmArguments.ToList();
        instance.ServerArguments = registration.ServerArguments.ToList();
        instance.StopCommand = registration.StopCommand;
        instance.Port = registration.Port;
        instance.AutoRestart = registration.AutoRestart;
        instance.ModpackProviderId = registration.ModpackProviderId;
        instance.ModpackSource = (ModpackSourceKind)registration.ModpackSource;
        instance.ModpackProjectId = registration.ModpackProjectId;
        instance.ModpackVersionId = registration.ModpackVersionId;
        instance.ModpackVersionName = registration.ModpackVersionName;
        instance.IsInstallerArtifact = registration.IsInstallerArtifact;
    }

    internal ServerInstance CreateCoreInstance(ProductServerRegistration registration)
        => ToCoreInstance(registration);

    private string ResolveServerDirectory(ProductServerRegistration registration)
    {
        ProductServerRegistrationValidator.ValidateAndThrow(registration, _layout);
        return ProductServerRegistrationValidator.ResolveOwnedPath(
            _layout.Servers,
            registration.ServerDirectory,
            allowRoot: false);
    }

    private IReadOnlySet<SafePathObjectIdentity> CaptureOtherManagedDirectoryIdentities(
        Guid excludedServerId)
    {
        var identities = new HashSet<SafePathObjectIdentity>();
        foreach (var registration in _registry.GetAll().Where(item => item.Id != excludedServerId))
        {
            var directory = ResolveServerDirectory(registration);
            if (!Directory.Exists(directory))
            {
                if (File.Exists(directory))
                {
                    throw new InvalidDataException(
                        "Another registered server path is not a directory.");
                }

                continue;
            }

            _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, directory);
            identities.Add(SafePath.GetExistingObjectIdentity(directory));
        }

        return identities;
    }

    internal Task<TResult> ExecuteStoppedMutationAsync<TResult>(
        Guid serverId,
        Func<ProductServerRegistration, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        => ExecuteStoppedMutationCoreAsync(
            serverId,
            operation,
            requireExplicitStoppedState: false,
            cancellationToken);

    internal Task<TResult> ExecuteExplicitlyStoppedMutationAsync<TResult>(
        Guid serverId,
        Func<ProductServerRegistration, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        => ExecuteStoppedMutationCoreAsync(
            serverId,
            operation,
            requireExplicitStoppedState: true,
            cancellationToken);

    private async Task<TResult> ExecuteStoppedMutationCoreAsync<TResult>(
        Guid serverId,
        Func<ProductServerRegistration, CancellationToken, Task<TResult>> operation,
        bool requireExplicitStoppedState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfShuttingDown();
        var gate = GetGate(serverId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _processManager.ExecuteWhileInactiveAsync(
                    serverId,
                    async token =>
                    {
                        var registration = GetRegistration(serverId);
                        if (requireExplicitStoppedState &&
                            _processManager.TryGetSnapshot(serverId, out var snapshot) &&
                            snapshot.State != ServerState.Stopped)
                        {
                            throw new InvalidOperationException(
                                "This maintenance operation requires the server to be completely stopped.");
                        }

                        return await operation(registration, token).ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task PreventAutomaticRestartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        _restartBlocker.Block(serverId);
        await _desiredRunIntent.SetDesiredAsync(serverId, false, cancellationToken)
            .ConfigureAwait(false);
    }

    internal void AllowAutomaticRestart(Guid serverId) => _restartBlocker.Unblock(serverId);

    private void OnConsoleLineReceived(object? sender, ConsoleLineReceivedEventArgs eventArgs)
    {
        _journals
            .GetOrAdd(eventArgs.InstanceId, _ => new ProductConsoleJournal(RetainedConsoleLinesPerServer))
            .Add(eventArgs.SessionId, eventArgs.Line);
        ConsoleLineObserved?.Invoke(this, eventArgs);
    }

    private void OnStateChanged(object? sender, ServerStateChangedEventArgs eventArgs)
        => StateChanged?.Invoke(this, eventArgs);

    private SemaphoreSlim GetGate(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(id));
        }

        return _operationGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    }

    private void ThrowIfShuttingDown()
    {
        if (Volatile.Read(ref _shutdown) != 0)
        {
            throw new InvalidOperationException("Server runtime is shutting down.");
        }
    }

    private async Task CompensateUncommittedStartAsync(Guid serverId)
    {
        try
        {
            await _processManager.StopAsync(serverId, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Preserve the original durable-intent failure. The process remains owned by the
            // Service/Job and normal shutdown still terminates it.
        }
    }

    private static string? Truncate(string? value, int maximumLength)
    {
        if (value is null || value.Length <= maximumLength)
        {
            return value;
        }

        var result = value[..maximumLength];
        return result.Length > 0 && char.IsHighSurrogate(result[^1]) ? result[..^1] : result;
    }
}

internal enum ProductDesiredServerRestoreResult
{
    NotDesired,
    MissingRegistration,
    AlreadyRunning,
    Restored,
}
