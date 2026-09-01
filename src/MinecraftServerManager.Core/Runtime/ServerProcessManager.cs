using System.Collections.Concurrent;
using System.Diagnostics;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>
/// Owns independent Java processes for any number of server instances. Every mutating operation
/// is serialized per instance, while unrelated instances can start, stop, or receive commands
/// concurrently.
/// </summary>
public sealed class ServerProcessManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, InstanceSlot> _slots = new();
    private readonly ServerProcessManagerOptions _options;
    private readonly IServerProcessFactory _processFactory;
    private readonly IServerLaunchDefinitionResolver _launchResolver;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private int _lifecycleState;

    public ServerProcessManager(
        ServerProcessManagerOptions? options = null,
        IServerProcessFactory? processFactory = null,
        IServerLaunchDefinitionResolver? launchResolver = null)
    {
        _options = options ?? new ServerProcessManagerOptions();
        ValidateOptions(_options);
        _processFactory = processFactory ?? new SystemServerProcessFactory();
        _launchResolver = launchResolver ?? new JavaJarLaunchDefinitionResolver();
    }

    public event EventHandler<ConsoleLineReceivedEventArgs>? ConsoleLineReceived;

    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;

    public event EventHandler<ServerResourceSampledEventArgs>? ResourceSampled;

    /// <summary>Starts one instance and returns its unique process-session ID.</summary>
    public async Task<Guid> StartAsync(
        ServerInstance instance,
        CancellationToken cancellationToken = default)
        => await StartAsync(instance, default, cancellationToken).ConfigureAwait(false);

    /// <summary>Starts one instance with authorization scoped to this exact start operation.</summary>
    public async Task<Guid> StartAsync(
        ServerInstance instance,
        ServerStartContext startContext,
        CancellationToken cancellationToken = default)
        => await StartCoreAsync(instance, restartGuard: null, startContext, cancellationToken)
               .ConfigureAwait(false)
           ?? throw new InvalidOperationException("A manual server start was unexpectedly cancelled.");

    private async Task<Guid?> StartCoreAsync(
        ServerInstance instance,
        ProcessSession? restartGuard,
        ServerStartContext startContext,
        CancellationToken cancellationToken)
    {
        ThrowIfNotAcceptingOperations();
        ArgumentNullException.ThrowIfNull(instance);

        // Snapshot mutable settings so an in-flight process and its auto-restart definition cannot
        // be changed from another UI thread.
        var instanceSnapshot = SnapshotInstance(instance);
        ValidateInstanceStopCommand(instanceSnapshot.StopCommand);
        var snapshotInstanceId = instanceSnapshot.Id;
        var slot = _slots.GetOrAdd(
            instanceSnapshot.Id,
            id => new InstanceSlot(id, _options.MaximumRetainedConsoleLines));

        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IDisposable? unownedDirectoryLease = null;
        var preparationCompleted = false;
        var startCommitted = false;
        try
        {
            ThrowIfNotAcceptingOperations();

            if (restartGuard is not null && !IsRestartCandidate(slot, restartGuard))
            {
                return null;
            }

            lock (slot.Sync)
            {
                if (slot.CurrentSession is not null)
                {
                    throw new InvalidOperationException(
                        $"Server instance '{instanceSnapshot.Name}' is already running or exiting.");
                }
            }

            if (restartGuard is not null && _options.RefreshAutoRestartSnapshotAsync is { } refresh)
            {
                await refresh(instanceSnapshot, cancellationToken).ConfigureAwait(false);
                if (instanceSnapshot.Id != snapshotInstanceId)
                {
                    throw new InvalidOperationException(
                        "RefreshAutoRestartSnapshotAsync cannot change the server instance ID.");
                }

                ValidateInstanceStopCommand(instanceSnapshot.StopCommand);
            }

            var lockedDirectoryPath = Path.GetFullPath(instanceSnapshot.DirectoryPath);

            // A port can be reassigned safely; a world directory cannot be shared safely. Hold
            // this OS-level file lock for the complete process session so another GUI/process,
            // or another instance ID in this manager, cannot start the same directory.
            unownedDirectoryLease = (_options.AcquireDirectoryLease ?? ServerDirectoryLock.Acquire)(
                    instanceSnapshot.DirectoryPath)
                ?? throw new InvalidOperationException("The directory lease provider returned null.");

            if (_options.PrepareStartWithContextAsync is { } prepareStartWithContext)
            {
                await prepareStartWithContext(instanceSnapshot, startContext, cancellationToken)
                    .ConfigureAwait(false);
                preparationCompleted = true;
            }
            else if (_options.PrepareStartAsync is { } prepareStart)
            {
                await prepareStart(instanceSnapshot, cancellationToken).ConfigureAwait(false);
                preparationCompleted = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (instanceSnapshot.Id != snapshotInstanceId
                || !string.Equals(
                    Path.GetFullPath(instanceSnapshot.DirectoryPath),
                    lockedDirectoryPath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "PrepareStartAsync cannot change the server instance ID or directory path.");
            }

            // The preparation hook intentionally receives a mutable private snapshot. Revalidate
            // the per-session stdin command after that hook so it cannot introduce a second line
            // after the initial public-instance validation above.
            ValidateInstanceStopCommand(instanceSnapshot.StopCommand);

            if (restartGuard is not null)
            {
                // The exact exited generation/session and the live UI policy are validated while
                // this instance's gate is held. A concurrent explicit Stop therefore either wins
                // before this check and cancels the queued restart, or waits and safely stops the
                // newly committed process; its intent can no longer disappear in the gap between
                // the preparation hook and StartAsync.
                if (!IsRestartCandidate(slot, restartGuard)
                    || !await IsAutoRestartEnabledAsync(restartGuard).ConfigureAwait(false)
                    || !IsRestartCandidate(slot, restartGuard))
                {
                    return null;
                }
            }

            // Resolve only after preparation so changes made to the private snapshot become the
            // exact executable/arguments used by this launch. Failures still release the lock.
            var startInfo = BuildStartInfo(instanceSnapshot);

            var process = _processFactory.Create()
                ?? throw new InvalidOperationException("The process factory returned null.");
            var session = new ProcessSession(
                Guid.NewGuid(),
                ++slot.Generation,
                instanceSnapshot,
                process,
                unownedDirectoryLease);
            unownedDirectoryLease = null;
            try
            {
                session.OutputHandler = (_, eventArgs) =>
                    HandleProcessText(slot, session, eventArgs.Text, ConsoleStream.StandardOutput);
                session.ErrorHandler = (_, eventArgs) =>
                    HandleProcessText(slot, session, eventArgs.Text, ConsoleStream.StandardError);
                process.OutputReceived += session.OutputHandler;
                process.ErrorReceived += session.ErrorHandler;

                lock (slot.Sync)
                {
                    slot.CurrentSession = session;
                    slot.LastSessionId = session.SessionId;
                    slot.LastExitCode = null;
                    slot.LastError = null;
                    slot.LastResourceSample = null;
                }

                RaiseStateChanged(TransitionState(slot, session.SessionId, ServerState.Starting));

                if (!process.Start(startInfo))
                {
                    throw new InvalidOperationException("The operating system did not start Java.");
                }

                session.ProcessId = process.Id;
                session.StartedAtUtc = DateTimeOffset.UtcNow;
                RaiseStateChanged(TransitionState(slot, session.SessionId, ServerState.Running));

                session.SamplingTask = SampleResourcesAsync(slot, session);
                session.MonitorTask = MonitorExitAsync(slot, session);
                lock (slot.Sync)
                {
                    slot.LastMonitorTask = session.MonitorTask;
                }

                startCommitted = true;
                return session.SessionId;
            }
            catch (Exception error)
            {
                lock (slot.Sync)
                {
                    if (ReferenceEquals(slot.CurrentSession, session))
                    {
                        slot.CurrentSession = null;
                    }
                }

                await DetachAndDisposeAsync(session).ConfigureAwait(false);
                RaiseStateChanged(TransitionState(
                    slot,
                    session.SessionId,
                    ServerState.Faulted,
                    error: error));
                throw;
            }
        }
        finally
        {
            if (preparationCompleted && !startCommitted)
            {
                NotifyPreparedStartAborted(snapshotInstanceId);
            }

            unownedDirectoryLease?.Dispose();
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// Executes a durable instance mutation under the same gate used by start, stop, and automatic
    /// restart. The callback runs only while no process session exists, closing the check/write
    /// race between Service settings changes and restart session commit.
    /// </summary>
    public async Task<TResult> ExecuteWhileInactiveAsync<TResult>(
        Guid instanceId,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotAcceptingOperations();
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("Server instance id must not be empty.", nameof(instanceId));
        }

        ArgumentNullException.ThrowIfNull(operation);
        var slot = _slots.GetOrAdd(
            instanceId,
            id => new InstanceSlot(id, _options.MaximumRetainedConsoleLines));
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotAcceptingOperations();
            lock (slot.Sync)
            {
                if (slot.CurrentSession is not null)
                {
                    throw new InvalidOperationException(
                        $"Server instance '{instanceId}' has an active process session.");
                }
            }

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async Task ExecuteWhileInactiveAsync(
        Guid instanceId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteWhileInactiveAsync(
                instanceId,
                async token =>
                {
                    await operation(token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an operation under the same per-instance lifecycle gate used by start, stop, and
    /// automatic restart. Unlike <see cref="ExecuteWhileInactiveAsync{TResult}"/>, an active
    /// process session is allowed, making this suitable for reads that must not race start
    /// preparation or a concurrent lifecycle transition.
    /// </summary>
    public async Task<TResult> ExecuteSerializedAsync<TResult>(
        Guid instanceId,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotAcceptingOperations();
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("Server instance id must not be empty.", nameof(instanceId));
        }

        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var slot = _slots.GetOrAdd(
            instanceId,
            id => new InstanceSlot(id, _options.MaximumRetainedConsoleLines));
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotAcceptingOperations();
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private void NotifyPreparedStartAborted(Guid instanceId)
    {
        try
        {
            _options.PreparedStartAborted?.Invoke(instanceId);
        }
        catch
        {
            // Preparation cleanup is best-effort and must never replace the launch failure or
            // turn a deliberately cancelled automatic restart into a new exception.
        }
    }

    /// <summary>
    /// Writes exactly one command line to the selected instance. Newline characters are rejected
    /// so one UI action cannot accidentally inject multiple commands.
    /// </summary>
    public async Task SendCommandAsync(
        Guid instanceId,
        string command,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotAcceptingOperations();
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Contains('\r') || command.Contains('\n') || command.Contains('\0'))
        {
            throw new ArgumentException("A server command must contain exactly one line.", nameof(command));
        }

        if (!_slots.TryGetValue(instanceId, out var slot))
        {
            throw new KeyNotFoundException($"Server instance '{instanceId}' is not managed.");
        }

        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotAcceptingOperations();
            ProcessSession session;
            lock (slot.Sync)
            {
                session = slot.CurrentSession
                    ?? throw new InvalidOperationException("The selected server is not running.");
                if (slot.State is not ServerState.Running)
                {
                    throw new InvalidOperationException(
                        $"Commands cannot be sent while the server state is {slot.State}.");
                }
            }

            var trimmedCommand = command.Trim();
            var manualStopCommand = string.Equals(
                    trimmedCommand,
                    ResolveStopCommand(session.Instance),
                    StringComparison.OrdinalIgnoreCase);
            if (manualStopCommand)
            {
                session.MarkManualStop();
                slot.Generation++;
                RaiseStateChanged(TransitionState(
                    slot,
                    session.SessionId,
                    ServerState.Stopping));
            }

            await session.Process.WriteLineAsync(
                    trimmedCommand,
                    manualStopCommand ? CancellationToken.None : cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// Requests a normal Minecraft shutdown, waits for the configured grace period, then kills the
    /// whole Java process tree if necessary. A manager-initiated stop never auto-restarts.
    /// </summary>
    public Task<bool> StopAsync(
        Guid instanceId,
        TimeSpan? gracefulTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotAcceptingOperations();
        return StopAndReturnBooleanAsync(instanceId, gracefulTimeout, cancellationToken);
    }

    /// <summary>
    /// Requests the same graceful-then-forced shutdown as <see cref="StopAsync"/>, but also reports
    /// which path was required. This is useful for watchdog and crash diagnostics.
    /// </summary>
    public Task<ServerStopResult> StopDetailedAsync(
        Guid instanceId,
        TimeSpan? gracefulTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotAcceptingOperations();
        return StopCoreAsync(instanceId, gracefulTimeout, cancellationToken, allowDisposing: false);
    }

    private async Task<bool> StopAndReturnBooleanAsync(
        Guid instanceId,
        TimeSpan? gracefulTimeout,
        CancellationToken cancellationToken)
        => (await StopCoreAsync(
                instanceId,
                gracefulTimeout,
                cancellationToken,
                allowDisposing: false)
            .ConfigureAwait(false)).WasRunning;

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotAcceptingOperations();
        var tasks = _slots.Keys
            .Select(id => StopCoreAsync(id, null, cancellationToken, allowDisposing: false));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public ProcessStartInfo BuildStartInfo(ServerInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var definition = _launchResolver.Resolve(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = definition.ExecutablePath,
            WorkingDirectory = Path.GetFullPath(definition.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = _options.StandardInputEncoding,
        };

        foreach (var argument in definition.Arguments)
        {
            if (argument is null
                || argument.Contains('\0')
                || argument.Contains('\r')
                || argument.Contains('\n'))
            {
                throw new ArgumentException("A launch definition contains an invalid argument.");
            }

            // ArgumentList bypasses cmd.exe and performs correct OS-specific quoting.
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public bool TryGetSnapshot(Guid instanceId, out ServerProcessSnapshot snapshot)
    {
        if (!_slots.TryGetValue(instanceId, out var slot))
        {
            snapshot = null!;
            return false;
        }

        lock (slot.Sync)
        {
            var session = slot.CurrentSession;
            snapshot = new ServerProcessSnapshot(
                instanceId,
                session?.SessionId ?? slot.LastSessionId,
                slot.State,
                session?.ProcessId,
                session?.StartedAtUtc,
                slot.LastExitCode,
                session?.ManualStopRequested ?? false,
                slot.LastResourceSample,
                slot.LastError);
            return true;
        }
    }

    public IReadOnlyList<ServerProcessSnapshot> GetSnapshots()
    {
        var snapshots = new List<ServerProcessSnapshot>(_slots.Count);
        foreach (var instanceId in _slots.Keys.Order())
        {
            if (TryGetSnapshot(instanceId, out var snapshot))
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    public IReadOnlyList<ConsoleLine> GetRecentConsoleLines(Guid instanceId) =>
        _slots.TryGetValue(instanceId, out var slot)
            ? slot.ConsoleLines.Snapshot()
            : [];

    public bool ClearConsole(Guid instanceId)
    {
        if (!_slots.TryGetValue(instanceId, out var slot))
        {
            return false;
        }

        lock (slot.Sync)
        {
            slot.ConsoleLines.Clear();
            // Do not leave an invisible diagnostic root active after its retained history was
            // cleared. The next orphaned stack frame must begin unclassified.
            slot.CurrentSession?.ConsoleClassifier.Reset();
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _lifecycleState, 1, 0) != 0)
        {
            return;
        }

        _shutdownCancellation.Cancel();
        var stopTasks = _slots.Keys
            .Select(id => StopCoreAsync(id, null, CancellationToken.None, allowDisposing: true))
            .ToArray();

        Exception? disposalError = null;
        try
        {
            await Task.WhenAll(stopTasks).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            disposalError = error;
        }

        var monitorTasks = _slots.Values
            .Select(slot =>
            {
                lock (slot.Sync)
                {
                    return slot.CurrentSession?.MonitorTask ?? slot.LastMonitorTask;
                }
            })
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();

        try
        {
            if (monitorTasks.Length > 0)
            {
                await Task.WhenAll(monitorTasks)
                    .WaitAsync(_options.MonitorDrainTimeout)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            disposalError ??= error;
        }
        finally
        {
            // A monitor belonging to an OS process that ignored termination may finish later. It
            // keeps owning the process and directory lock until then, but it is detached from all
            // public subscribers so a disposed manager cannot call back into a closing UI.
            ConsoleLineReceived = null;
            StateChanged = null;
            ResourceSampled = null;
            _shutdownCancellation.Dispose();
            Volatile.Write(ref _lifecycleState, 2);
        }

        if (disposalError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalError).Throw();
        }
    }

    private async Task<ServerStopResult> StopCoreAsync(
        Guid instanceId,
        TimeSpan? gracefulTimeout,
        CancellationToken cancellationToken,
        bool allowDisposing)
    {
        if (!allowDisposing)
        {
            ThrowIfNotAcceptingOperations();
        }

        var timeout = gracefulTimeout ?? _options.GracefulStopTimeout;
        ValidateTimeout(timeout, nameof(gracefulTimeout));

        if (!_slots.TryGetValue(instanceId, out var slot))
        {
            return new ServerStopResult(false, null, ServerStopMode.NotRunning, TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateReleased = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessSession? session;
            lock (slot.Sync)
            {
                session = slot.CurrentSession;
                slot.Generation++;
            }

            if (session is null)
            {
                Guid? lastSessionId;
                lock (slot.Sync)
                {
                    lastSessionId = slot.LastSessionId;
                }

                if (lastSessionId.HasValue)
                {
                    RaiseStateChanged(TransitionState(
                        slot,
                        lastSessionId.Value,
                        ServerState.Stopped));
                }

                stopwatch.Stop();
                return new ServerStopResult(
                    false,
                    lastSessionId,
                    ServerStopMode.NotRunning,
                    stopwatch.Elapsed);
            }

            session.MarkManualStop();
            RaiseStateChanged(TransitionState(slot, session.SessionId, ServerState.Stopping));

            if (!session.Process.HasExited)
            {
                try
                {
                    // Once a stop request has been delivered, complete the safe shutdown sequence
                    // even if the caller cancels its UI wait.
                    await session.Process.WriteLineAsync(
                            ResolveStopCommand(session.Instance),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException
                                                  or InvalidOperationException
                                                  or ObjectDisposedException)
                {
                    PublishSystemLine(
                        slot,
                        session,
                        "The graceful stop command could not be delivered; waiting before forced termination.",
                        ConsoleLineSeverity.Warning);
                }
            }

            var exited = await WaitForExitWithinAsync(session.Process, timeout).ConfigureAwait(false);
            var stopMode = ServerStopMode.Graceful;
            if (!exited)
            {
                stopMode = ServerStopMode.Forced;
                PublishSystemLine(
                    slot,
                    session,
                    "Graceful stop timed out; terminating the Java process tree.",
                    ConsoleLineSeverity.Warning);
                if (!session.Process.HasExited)
                {
                    session.Process.Kill(entireProcessTree: true);
                }

                if (!await WaitForExitWithinAsync(
                        session.Process,
                        _options.ForcedKillWaitTimeout).ConfigureAwait(false))
                {
                    throw new TimeoutException(
                        $"Process {session.ProcessId?.ToString() ?? "(unknown)"} did not exit after a forced kill.");
                }
            }

            // The exit monitor owns terminal-state publication and process disposal. Waiting for
            // it here removes the race where StopAsync returned and an immediate restart still saw
            // the exited session as current.
            var monitorTask = session.MonitorTask;
            slot.Gate.Release();
            gateReleased = true;
            if (monitorTask is not null)
            {
                await monitorTask.ConfigureAwait(false);
            }

            stopwatch.Stop();
            return new ServerStopResult(true, session.SessionId, stopMode, stopwatch.Elapsed);
        }
        finally
        {
            if (!gateReleased)
            {
                slot.Gate.Release();
            }
        }
    }

    private async Task MonitorExitAsync(InstanceSlot slot, ProcessSession session)
    {
        Exception? waitError = null;
        try
        {
            await session.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            waitError = error;
            try
            {
                if (!session.Process.HasExited)
                {
                    session.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception) when (error is not OutOfMemoryException)
            {
                // The original process-monitor error is the useful failure to report.
            }
        }

        session.SamplingCancellation.Cancel();
        int? exitCode = null;
        try
        {
            exitCode = session.Process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // A failed wait may not expose an exit code.
        }

        var shouldRestart = false;
        await slot.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (slot.Sync)
            {
                if (!ReferenceEquals(slot.CurrentSession, session))
                {
                    return;
                }

                slot.CurrentSession = null;
                slot.LastExitCode = exitCode;
            }

            var terminalState = waitError is not null
                ? ServerState.Faulted
                : session.ManualStopRequested || exitCode == 0
                    ? ServerState.Stopped
                    : ServerState.Crashed;
            RaiseStateChanged(TransitionState(
                slot,
                session.SessionId,
                terminalState,
                exitCode,
                waitError));

            shouldRestart = terminalState is ServerState.Crashed or ServerState.Faulted
                && (session.Instance.AutoRestart || _options.ShouldAutoRestartAsync is not null)
                && !session.ManualStopRequested
                && !_shutdownCancellation.IsCancellationRequested;
        }
        finally
        {
            // Keep the per-instance gate closed until the directory lease has been released. A
            // Start triggered immediately by the terminal-state event must not transiently fail
            // against the just-exited session's still-open lock stream.
            try
            {
                await DetachAndDisposeAsync(session).ConfigureAwait(false);
            }
            finally
            {
                slot.Gate.Release();
            }
        }

        if (shouldRestart)
        {
            await RestartAfterDelayAsync(slot, session).ConfigureAwait(false);
        }
    }

    private async Task RestartAfterDelayAsync(InstanceSlot slot, ProcessSession exitedSession)
    {
        try
        {
            if (!await IsAutoRestartEnabledAsync(exitedSession).ConfigureAwait(false))
            {
                return;
            }

            var restartDelay = _options.GetAutoRestartDelayAsync is { } getRestartDelay
                ? await getRestartDelay(
                        exitedSession.Instance.Id,
                        exitedSession.SessionId,
                        _shutdownCancellation.Token)
                    .ConfigureAwait(false)
                : _options.AutoRestartDelay;
            if (restartDelay < TimeSpan.Zero || restartDelay > TimeSpan.FromHours(1))
            {
                throw new InvalidOperationException(
                    $"Automatic restart delay {restartDelay} is outside the allowed range.");
            }

            PublishSystemLine(
                slot,
                exitedSession,
                $"Server exited unexpectedly; restarting in {restartDelay.TotalSeconds:0.##} seconds.",
                ConsoleLineSeverity.Warning,
                requireCurrentSession: false);
            await Task.Delay(restartDelay, _shutdownCancellation.Token)
                .ConfigureAwait(false);

            if (!await IsAutoRestartEnabledAsync(exitedSession).ConfigureAwait(false))
            {
                return;
            }

            await slot.Gate.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
            var mayRestart = false;
            try
            {
                lock (slot.Sync)
                {
                    mayRestart = slot.CurrentSession is null
                        && slot.Generation == exitedSession.Generation
                        && slot.LastSessionId == exitedSession.SessionId;
                }
            }
            finally
            {
                slot.Gate.Release();
            }

            if (mayRestart)
            {
                var restartInstance = SnapshotInstance(exitedSession.Instance);
                if (_options.PrepareAutoRestartAsync is { } prepareAutoRestart)
                {
                    await prepareAutoRestart(restartInstance, _shutdownCancellation.Token)
                        .ConfigureAwait(false);
                }

                await StartCoreAsync(
                        restartInstance,
                        exitedSession,
                        default,
                        _shutdownCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            // Manager disposal intentionally cancels queued restarts.
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _lifecycleState) != 0)
        {
            // Manager disposal raced the queued restart.
        }
        catch (InvalidOperationException) when (HasNewerSession(slot, exitedSession))
        {
            // A user-started replacement won the race; never disturb that newer session.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            PublishSystemLine(
                slot,
                exitedSession,
                $"Automatic restart failed: {error.Message}",
                ConsoleLineSeverity.Error,
                requireCurrentSession: false);
            // StartCoreAsync publishes a Faulted terminal event for a process that reached its own
            // new session. Do not then publish another stale Faulted event for the old crashed
            // session: that would double-count one restart failure in crash-loop protection.
            if (IsLastKnownSession(slot, exitedSession.SessionId))
            {
                RaiseStateChanged(TransitionState(
                    slot,
                    exitedSession.SessionId,
                    ServerState.Faulted,
                    error: error));
            }
        }
    }

    private Task<bool> IsAutoRestartEnabledAsync(ProcessSession exitedSession)
        => _options.ShouldAutoRestartAsync is { } livePolicy
            ? livePolicy(exitedSession.Instance.Id, _shutdownCancellation.Token)
            : Task.FromResult(exitedSession.Instance.AutoRestart);

    private static bool HasNewerSession(InstanceSlot slot, ProcessSession exitedSession)
    {
        lock (slot.Sync)
        {
            return slot.CurrentSession is { } current
                && current.SessionId != exitedSession.SessionId;
        }
    }

    private static bool IsRestartCandidate(InstanceSlot slot, ProcessSession exitedSession)
    {
        lock (slot.Sync)
        {
            return slot.CurrentSession is null
                && slot.Generation == exitedSession.Generation
                && slot.LastSessionId == exitedSession.SessionId;
        }
    }

    private static bool IsLastKnownSession(InstanceSlot slot, Guid sessionId)
    {
        lock (slot.Sync)
        {
            return slot.CurrentSession is null && slot.LastSessionId == sessionId;
        }
    }

    private async Task SampleResourcesAsync(InstanceSlot slot, ProcessSession session)
    {
        if (_options.ResourceSamplingInterval == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        try
        {
            var previousMetrics = session.Process.CaptureMetrics();
            var previousTimestamp = DateTimeOffset.UtcNow;
            using var timer = new PeriodicTimer(_options.ResourceSamplingInterval);

            while (await timer.WaitForNextTickAsync(session.SamplingCancellation.Token)
                       .ConfigureAwait(false))
            {
                lock (slot.Sync)
                {
                    if (!ReferenceEquals(slot.CurrentSession, session))
                    {
                        return;
                    }
                }

                var timestamp = DateTimeOffset.UtcNow;
                var metrics = session.Process.CaptureMetrics();
                var elapsed = timestamp - previousTimestamp;
                var processorDelta = metrics.TotalProcessorTime - previousMetrics.TotalProcessorTime;
                var cpuPercent = elapsed <= TimeSpan.Zero
                    ? 0
                    : processorDelta.TotalMilliseconds
                      / elapsed.TotalMilliseconds
                      / Math.Max(1, Environment.ProcessorCount)
                      * 100;
                cpuPercent = Math.Clamp(cpuPercent, 0, 100);

                var sample = new ServerResourceSample(
                    session.Instance.Id,
                    session.SessionId,
                    timestamp,
                    cpuPercent,
                    metrics.WorkingSetBytes,
                    metrics.PrivateMemoryBytes,
                    timestamp - session.StartedAtUtc!.Value);

                lock (slot.Sync)
                {
                    if (!ReferenceEquals(slot.CurrentSession, session))
                    {
                        return;
                    }

                    slot.LastResourceSample = sample;
                }

                RaiseSafely(
                    ResourceSampled,
                    new ServerResourceSampledEventArgs(sample));
                previousMetrics = metrics;
                previousTimestamp = timestamp;
            }
        }
        catch (OperationCanceledException) when (session.SamplingCancellation.IsCancellationRequested)
        {
            // Normal session shutdown.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!session.Process.HasExited)
            {
                PublishSystemLine(
                    slot,
                    session,
                    $"Resource sampling stopped: {error.Message}",
                    ConsoleLineSeverity.Warning);
            }
        }
    }

    private void HandleProcessText(
        InstanceSlot slot,
        ProcessSession session,
        string text,
        ConsoleStream stream)
    {
        ConsoleLine line;
        lock (slot.Sync)
        {
            // Output callbacks can arrive after Process.Exit. Only the active session may mutate
            // UI state or the retained log, preventing an old process from polluting a new one.
            if (!ReferenceEquals(slot.CurrentSession, session))
            {
                return;
            }

            var classification = session.ConsoleClassifier.Classify(text, stream);
            line = new ConsoleLine(DateTimeOffset.UtcNow, text, stream)
            {
                ServerInstanceId = session.Instance.Id,
                SessionId = session.SessionId,
                Severity = classification.Severity,
                DiagnosticId = classification.DiagnosticId,
                IsDiagnosticContinuation = classification.IsDiagnosticContinuation,
            };
            slot.ConsoleLines.Add(line);
        }

        RaiseSafely(
            ConsoleLineReceived,
            new ConsoleLineReceivedEventArgs(
                session.Instance.Id,
                session.SessionId,
                line));
    }

    private void PublishSystemLine(
        InstanceSlot slot,
        ProcessSession session,
        string text,
        ConsoleLineSeverity severity = ConsoleLineSeverity.Information,
        bool requireCurrentSession = true)
    {
        ConsoleLine line;
        lock (slot.Sync)
        {
            if (requireCurrentSession && !ReferenceEquals(slot.CurrentSession, session))
            {
                return;
            }

            Guid? diagnosticId = severity is
                ConsoleLineSeverity.Warning or
                ConsoleLineSeverity.Error or
                ConsoleLineSeverity.Fatal
                    ? Guid.NewGuid()
                    : null;
            line = new ConsoleLine(DateTimeOffset.UtcNow, text, ConsoleStream.System)
            {
                ServerInstanceId = session.Instance.Id,
                SessionId = session.SessionId,
                Severity = severity,
                DiagnosticId = diagnosticId,
            };
            slot.ConsoleLines.Add(line);
        }

        RaiseSafely(
            ConsoleLineReceived,
            new ConsoleLineReceivedEventArgs(
                session.Instance.Id,
                session.SessionId,
                line));
    }

    private ServerStateChangedEventArgs? TransitionState(
        InstanceSlot slot,
        Guid sessionId,
        ServerState state,
        int? exitCode = null,
        Exception? error = null)
    {
        lock (slot.Sync)
        {
            var previousState = slot.State;
            slot.State = state;
            if (exitCode.HasValue)
            {
                slot.LastExitCode = exitCode;
            }

            if (error is not null)
            {
                slot.LastError = error;
            }

            return previousState == state && exitCode is null && error is null
                ? null
                : new ServerStateChangedEventArgs(
                    slot.InstanceId,
                    sessionId,
                    previousState,
                    state,
                    exitCode,
                    error);
        }
    }

    private void RaiseStateChanged(ServerStateChangedEventArgs? eventArgs)
    {
        if (eventArgs is not null)
        {
            RaiseSafely(StateChanged, eventArgs);
        }
    }

    private void RaiseSafely<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // Subscriber failures must never terminate a managed Minecraft process.
            }
        }
    }

    private static async Task<bool> WaitForExitWithinAsync(
        IServerProcess process,
        TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return true;
        }

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    private static async Task DetachAndDisposeAsync(ProcessSession session)
    {
        session.SamplingCancellation.Cancel();
        if (session.OutputHandler is not null)
        {
            session.Process.OutputReceived -= session.OutputHandler;
        }

        if (session.ErrorHandler is not null)
        {
            session.Process.ErrorReceived -= session.ErrorHandler;
        }

        if (session.SamplingTask is not null)
        {
            try
            {
                await session.SamplingTask.ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Resource sampling is best-effort and cannot block process cleanup.
            }
        }

        try
        {
            session.Process.Dispose();
        }
        finally
        {
            // Releasing directory ownership must not depend on a process adapter disposing
            // cleanly. The lock file itself deliberately remains on disk.
            try
            {
                session.DirectoryLease.Dispose();
            }
            finally
            {
                session.SamplingCancellation.Dispose();
            }
        }
    }

    private static void ValidateOptions(ServerProcessManagerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRetainedConsoleLines, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StopCommand);
        ArgumentNullException.ThrowIfNull(options.StandardInputEncoding);
        ValidateTimeout(options.ResourceSamplingInterval, nameof(options.ResourceSamplingInterval));
        ValidateTimeout(options.GracefulStopTimeout, nameof(options.GracefulStopTimeout));
        ValidateTimeout(options.ForcedKillWaitTimeout, nameof(options.ForcedKillWaitTimeout));
        ValidateBoundedTimeout(options.MonitorDrainTimeout, nameof(options.MonitorDrainTimeout));
        ValidateTimeout(options.AutoRestartDelay, nameof(options.AutoRestartDelay));

        if (options.PrepareStartAsync is not null
            && options.PrepareStartWithContextAsync is not null)
        {
            throw new ArgumentException(
                "Only one server-start preparation hook may be configured.",
                nameof(options));
        }

        if (options.ResourceSamplingInterval == TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ResourceSamplingInterval),
                "Resource sampling interval must be positive or infinite.");
        }
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout cannot be negative.");
        }
    }

    private static void ValidateBoundedTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be finite and non-negative.");
        }
    }

    private void ThrowIfNotAcceptingOperations()
    {
        if (Volatile.Read(ref _lifecycleState) != 0)
        {
            throw new ObjectDisposedException(nameof(ServerProcessManager));
        }
    }

    private static ServerInstance SnapshotInstance(ServerInstance instance) => new()
    {
        Id = instance.Id,
        Name = instance.Name,
        DirectoryPath = instance.DirectoryPath,
        ServerJarPath = instance.ServerJarPath,
        LaunchKind = instance.LaunchKind,
        JavaArgumentFilePaths = instance.JavaArgumentFilePaths is null
            ? []
            : [.. instance.JavaArgumentFilePaths],
        SourceLaunchScriptPath = instance.SourceLaunchScriptPath,
        CoreType = instance.CoreType,
        MinecraftVersion = instance.MinecraftVersion,
        JavaMajorVersion = instance.JavaMajorVersion,
        JavaExecutablePath = instance.JavaExecutablePath,
        MinimumMemoryMb = instance.MinimumMemoryMb,
        MaximumMemoryMb = instance.MaximumMemoryMb,
        MemoryAllocationMode = instance.MemoryAllocationMode,
        JvmArguments = instance.JvmArguments is null ? [] : [.. instance.JvmArguments],
        ServerArguments = instance.ServerArguments is null ? [] : [.. instance.ServerArguments],
        StopCommand = instance.StopCommand,
        Port = instance.Port,
        AutoRestart = instance.AutoRestart,
        SeparateDiagnosticOutput = instance.SeparateDiagnosticOutput,
        EnableHangWatchdog = instance.EnableHangWatchdog,
        WatchdogCheckIntervalSeconds = instance.WatchdogCheckIntervalSeconds,
        WatchdogProbeTimeoutSeconds = instance.WatchdogProbeTimeoutSeconds,
        WatchdogFailureThreshold = instance.WatchdogFailureThreshold,
        WatchdogStartupGraceSeconds = instance.WatchdogStartupGraceSeconds,
        EnableAutomaticRecoveryPoints = instance.EnableAutomaticRecoveryPoints,
        RecoveryPointIntervalMinutes = instance.RecoveryPointIntervalMinutes,
        RecoveryPointRetentionCount = instance.RecoveryPointRetentionCount,
        BackgroundImagePath = instance.BackgroundImagePath,
        BackgroundImageOpacity = instance.BackgroundImageOpacity,
        IconImagePath = instance.IconImagePath,
        CatalogIconImagePath = instance.CatalogIconImagePath,
        CatalogPreviewImagePath = instance.CatalogPreviewImagePath,
        ModpackProviderId = instance.ModpackProviderId,
        ModpackSource = instance.ModpackSource,
        ModpackProjectId = instance.ModpackProjectId,
        ModpackVersionId = instance.ModpackVersionId,
        ModpackVersionName = instance.ModpackVersionName,
        IsInstallerArtifact = instance.IsInstallerArtifact,
        CreatedAtUtc = instance.CreatedAtUtc,
    };

    private string ResolveStopCommand(ServerInstance instance)
        => string.IsNullOrWhiteSpace(instance.StopCommand)
            ? _options.StopCommand.Trim()
            : instance.StopCommand.Trim();

    private static void ValidateInstanceStopCommand(string? command)
    {
        if (command is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(command)
            || command.Contains('\r')
            || command.Contains('\n')
            || command.Contains('\0'))
        {
            throw new ArgumentException(
                "A per-instance stop command must contain exactly one non-empty line.",
                nameof(ServerInstance.StopCommand));
        }
    }

    private sealed class InstanceSlot(Guid instanceId, int logCapacity)
    {
        public Guid InstanceId { get; } = instanceId;

        public object Sync { get; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public BoundedLogBuffer<ConsoleLine> ConsoleLines { get; } = new(logCapacity);

        public ProcessSession? CurrentSession { get; set; }

        public Guid? LastSessionId { get; set; }

        public ServerState State { get; set; } = ServerState.Stopped;

        public int? LastExitCode { get; set; }

        public Exception? LastError { get; set; }

        public ServerResourceSample? LastResourceSample { get; set; }

        public long Generation { get; set; }

        public Task? LastMonitorTask { get; set; }
    }

    private sealed class ProcessSession(
        Guid sessionId,
        long generation,
        ServerInstance instance,
        IServerProcess process,
        IDisposable directoryLease)
    {
        private int _manualStopRequested;

        public Guid SessionId { get; } = sessionId;

        public long Generation { get; } = generation;

        public ServerInstance Instance { get; } = instance;

        public IServerProcess Process { get; } = process;

        public ConsoleLineClassifier ConsoleClassifier { get; } = new();

        public IDisposable DirectoryLease { get; } = directoryLease;

        public CancellationTokenSource SamplingCancellation { get; } = new();

        public EventHandler<ProcessTextReceivedEventArgs>? OutputHandler { get; set; }

        public EventHandler<ProcessTextReceivedEventArgs>? ErrorHandler { get; set; }

        public int? ProcessId { get; set; }

        public DateTimeOffset? StartedAtUtc { get; set; }

        public Task? SamplingTask { get; set; }

        public Task? MonitorTask { get; set; }

        public bool ManualStopRequested => Volatile.Read(ref _manualStopRequested) != 0;

        public void MarkManualStop() => Interlocked.Exchange(ref _manualStopRequested, 1);
    }
}
