using System.Text;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Runtime;

public sealed record ServerProcessManagerOptions
{
    private Encoding _standardInputEncoding = new UTF8Encoding(false);

    public int MaximumRetainedConsoleLines { get; init; } = 5_000;

    public TimeSpan ResourceSamplingInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ForcedKillWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum additional time disposal waits for process-monitor tasks after all graceful and
    /// forced stop attempts have completed. A process that ignores termination must not make the
    /// manager's disposal wait forever.
    /// </summary>
    public TimeSpan MonitorDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan AutoRestartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional per-crash delay selector. It is evaluated after the first live-policy check and
    /// receives the exact exited session so callers can implement bounded exponential backoff.
    /// Returned delays must be non-negative and no greater than one hour.
    /// </summary>
    public Func<Guid, Guid, CancellationToken, Task<TimeSpan>>? GetAutoRestartDelayAsync { get; init; }

    /// <summary>
    /// Optional live policy queried after an unexpected exit and again immediately before restart.
    /// When supplied, it overrides the launch-time AutoRestart snapshot so UI changes made while a
    /// server is running take effect without another launch.
    /// </summary>
    public Func<Guid, CancellationToken, Task<bool>>? ShouldAutoRestartAsync { get; init; }

    /// <summary>
    /// Optional asynchronous hook invoked for every launch after the manager has acquired the
    /// server directory's exclusive execution lock, but before resolving the launch definition
    /// or starting the process. The supplied instance is a private snapshot; changes made by the
    /// hook (for example, selecting a port and updating server.properties) apply to that launch.
    /// The instance identity and server directory must not be changed by the hook.
    /// </summary>
    public Func<ServerInstance, CancellationToken, Task>? PrepareStartAsync { get; init; }

    /// <summary>
    /// Optional synchronous cleanup hook invoked exactly once when <see cref="PrepareStartAsync"/>
    /// completed successfully but that prepared launch did not commit a running process session.
    /// The supplied value is the original server instance ID. Implementations should be short and
    /// non-throwing; exceptions from this best-effort cleanup hook are ignored by the manager.
    /// </summary>
    public Action<Guid>? PreparedStartAborted { get; init; }

    /// <summary>
    /// Optional legacy hook invoked with a private instance snapshot before an automatic restart.
    /// This hook runs before the directory lock is acquired, so it must not read or write files in
    /// the server directory. Use <see cref="PrepareStartAsync"/> for port allocation and other
    /// filesystem preparation that must be protected by the exclusive directory lock.
    /// </summary>
    public Func<ServerInstance, CancellationToken, Task>? PrepareAutoRestartAsync { get; init; }

    public string StopCommand { get; init; } = "stop";

    /// <summary>
    /// Encoding used only when writing administrative commands to Java stdin. Redirected stdout
    /// and stderr are decoded independently from raw bytes by the process adapter.
    /// </summary>
    public Encoding StandardInputEncoding
    {
        get => _standardInputEncoding;
        init => _standardInputEncoding = value;
    }

    /// <summary>
    /// Backward-compatible alias for <see cref="StandardInputEncoding"/>. Output streams no longer
    /// use this value because they are decoded independently from raw bytes.
    /// </summary>
    [Obsolete("Use StandardInputEncoding. stdout and stderr now use hybrid raw-byte decoding.")]
    public Encoding ConsoleEncoding
    {
        get => _standardInputEncoding;
        init => _standardInputEncoding = value;
    }
}

public sealed record ServerResourceSample(
    Guid InstanceId,
    Guid SessionId,
    DateTimeOffset Timestamp,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    TimeSpan Uptime);

public sealed record ServerProcessSnapshot(
    Guid InstanceId,
    Guid? SessionId,
    ServerState State,
    int? ProcessId,
    DateTimeOffset? StartedAtUtc,
    int? LastExitCode,
    bool ManualStopRequested,
    ServerResourceSample? LastResourceSample,
    Exception? LastError);

public enum ServerStopMode
{
    NotRunning,
    Graceful,
    Forced
}

/// <summary>
/// Describes how a manager-requested shutdown completed. A forced result means the normal
/// Minecraft <c>stop</c> command did not make the process exit within the configured grace period,
/// so the complete Java process tree was terminated.
/// </summary>
public sealed record ServerStopResult(
    bool WasRunning,
    Guid? SessionId,
    ServerStopMode Mode,
    TimeSpan Elapsed);

public sealed class ConsoleLineReceivedEventArgs(
    Guid instanceId,
    Guid sessionId,
    ConsoleLine line) : EventArgs
{
    public Guid InstanceId { get; } = instanceId;

    public Guid SessionId { get; } = sessionId;

    public ConsoleLine Line { get; } = line;
}

public sealed class ServerStateChangedEventArgs(
    Guid instanceId,
    Guid sessionId,
    ServerState previousState,
    ServerState state,
    int? exitCode = null,
    Exception? error = null) : EventArgs
{
    public Guid InstanceId { get; } = instanceId;

    public Guid SessionId { get; } = sessionId;

    public ServerState PreviousState { get; } = previousState;

    public ServerState State { get; } = state;

    public int? ExitCode { get; } = exitCode;

    public Exception? Error { get; } = error;
}

public sealed class ServerResourceSampledEventArgs(ServerResourceSample sample) : EventArgs
{
    public Guid InstanceId => Sample.InstanceId;

    public Guid SessionId => Sample.SessionId;

    public ServerResourceSample Sample { get; } = sample;
}
