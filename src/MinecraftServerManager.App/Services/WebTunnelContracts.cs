namespace MinecraftServerManager.App.Services;

internal enum WebTunnelLifecycleState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

internal enum WebTunnelLogChannel
{
    Service,
    StandardOutput,
    StandardError
}

internal sealed record WebTunnelLogEntry(
    DateTimeOffset TimestampUtc,
    WebTunnelLogChannel Channel,
    string Message);

internal sealed record WebTunnelSnapshot(
    WebTunnelLifecycleState State,
    Uri? PublicUrl,
    int? ProcessId,
    string ExecutableVersion,
    DateTimeOffset? StartedAtUtc,
    TimeSpan? RunningFor,
    string? Error,
    IReadOnlyList<WebTunnelLogEntry> RecentLogs)
{
    public bool IsRunning => State == WebTunnelLifecycleState.Running;
}

/// <summary>
/// Owns one browser-facing tunnel whose lifetime is bounded by the desktop process.
/// Implementations must not install a system service, open a firewall port, or leave a
/// connector running after <see cref="StopAsync"/> or <see cref="DisposeAsync"/> completes.
/// </summary>
internal interface IWebTunnelService : IAsyncDisposable
{
    event EventHandler<WebTunnelSnapshot>? StateChanged;

    WebTunnelSnapshot Snapshot { get; }

    Task<WebTunnelSnapshot> StartAsync(
        int localPort,
        CancellationToken cancellationToken = default);

    Task<WebTunnelSnapshot> StopAsync(CancellationToken cancellationToken = default);
}
