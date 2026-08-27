using System.Threading.Channels;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace MinecraftServerManager.Service;

public sealed record ProductRemoteWebStatus(
    bool DesiredEnabled,
    bool HostRunning,
    bool FunnelRunning,
    string? PublicUrl,
    string State,
    string? ErrorCode,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextRetryAtUtc);

public interface IProductRemoteWebSupervisor
{
    ProductRemoteWebStatus Snapshot { get; }

    Task<ProductRemoteWebStatus> EnableAsync(CancellationToken cancellationToken);

    Task<ProductRemoteWebStatus> DisableAsync(CancellationToken cancellationToken);

    Task<ProductRemoteWebStatus> ReconnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns the formal remote Web listener and its one foreground Tailscale Funnel session. The
/// foreground process is the route ownership token: this class never issues a broad reset command.
/// </summary>
internal sealed class ProductRemoteWebSupervisor(
    ProductServiceOptions serviceOptions,
    ProductServiceState serviceState,
    ProductRemoteWebIntentStore intentStore,
    IProductRemoteWebHostFactory hostFactory,
    IProductTailscalePlatform tailscale,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider timeProvider,
    ILogger<ProductRemoteWebSupervisor> logger) : BackgroundService, IProductRemoteWebSupervisor
{
    public const int LocalWebPort = 42871;
    private const int StartupProbeAttempts = 20;
    private const int RemovalProbeAttempts = 4;
    private static readonly TimeSpan StartupProbeDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RemovalProbeDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RunningProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartOperationTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly object _statusGate = new();
    private IProductRemoteWebHost? _host;
    private IProductRemoteWebHost? _guardedHost;
    private IProductOwnedFunnelProcess? _funnelProcess;
    private string? _dnsName;
    private string? _target;
    private string? _guardedDnsName;
    private bool _intentLoaded;
    private bool _desiredEnabled = true;
    private int _stopping;
    private ProductRemoteWebStatus _status = new(
        true,
        false,
        false,
        null,
        "waiting",
        null,
        DateTimeOffset.MinValue,
        null);

    public ProductRemoteWebStatus Snapshot
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    public async Task<ProductRemoteWebStatus> EnableAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            EnsureIntentLoaded();
            intentStore.WriteDesiredEnabled(true);
            _desiredEnabled = true;
            if (!serviceState.IsReady)
            {
                return Publish("waiting", "remote.service_not_ready");
            }

            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            Signal();
        }
    }

    public async Task<ProductRemoteWebStatus> DisableAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            EnsureIntentLoaded();
            intentStore.WriteDesiredEnabled(false);
            _desiredEnabled = false;
            // Once the durable intent is disabled, a disconnected loopback caller must not be
            // able to cancel ingress teardown midway through its ownership transition.
            return await StopRuntimeCoreAsync(shutdown: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            Signal();
        }
    }

    public async Task<ProductRemoteWebStatus> ReconnectAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            EnsureIntentLoaded();
            intentStore.WriteDesiredEnabled(true);
            _desiredEnabled = true;
            await StopRuntimeCoreAsync(shutdown: false, CancellationToken.None).ConfigureAwait(false);
            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            Signal();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = InitialRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delayAfterIteration = retryDelay;
            try
            {
                await WaitForServiceReadyAsync(stoppingToken).ConfigureAwait(false);
                await _operationGate.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    EnsureIntentLoaded();
                    if (_funnelProcess is { HasExited: true })
                    {
                        await StopRuntimeCoreAsync(shutdown: false, stoppingToken).ConfigureAwait(false);
                        Publish("retrying", "tailscale.funnel_process_exited");
                    }
                    else if (_funnelProcess is { HasExited: false } && _dnsName is { } activeDns)
                    {
                        var route = await tailscale.GetFunnelStatusAsync(
                                activeDns,
                                LocalWebPort,
                                stoppingToken)
                            .ConfigureAwait(false);
                        if (route.Disposition != ProductFunnelRouteDisposition.ExactTarget)
                        {
                            await StopRuntimeCoreAsync(shutdown: false, stoppingToken).ConfigureAwait(false);
                            Publish(
                                route.Disposition == ProductFunnelRouteDisposition.Conflict
                                    ? "blocked"
                                    : "retrying",
                                route.ErrorCode ?? "tailscale.funnel_route_lost");
                        }
                    }

                    if (_desiredEnabled && ShouldAutoStart())
                    {
                        var current = await EnsureStartedCoreAsync(stoppingToken).ConfigureAwait(false);
                        if (current.FunnelRunning)
                        {
                            retryDelay = InitialRetryDelay;
                            delayAfterIteration = RunningProbeInterval;
                        }
                        else
                        {
                            delayAfterIteration = retryDelay;
                            var nextRetry = timeProvider.GetUtcNow() + delayAfterIteration;
                            Publish("retrying", current.ErrorCode, nextRetry);
                            retryDelay = TimeSpan.FromTicks(Math.Min(
                                retryDelay.Ticks * 2,
                                MaximumRetryDelay.Ticks));
                        }
                    }
                    else if (!_desiredEnabled)
                    {
                        retryDelay = InitialRetryDelay;
                        delayAfterIteration = InitialRetryDelay;
                    }
                }
                finally
                {
                    _operationGate.Release();
                }

                await WaitForWakeOrDelayAsync(delayAfterIteration, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                logger.LogWarning(error, "Remote Web supervisor recovered from an internal lifecycle failure.");
                Publish("retrying", "remote.lifecycle_failed", timeProvider.GetUtcNow() + retryDelay);
                await WaitForWakeOrDelayAsync(retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopping, 1);
        Signal();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Public ingress teardown is bounded internally and must finish even if the Service
            // Control Manager's notification token is cancelled.
            await StopRuntimeCoreAsync(shutdown: true, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ProductRemoteWebStatus> EnsureStartedCoreAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartOperationTimeout);
        try
        {
            return await EnsureStartedWithinDeadlineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Publish("retrying", "remote.start_timeout");
        }
    }

    private async Task<ProductRemoteWebStatus> EnsureStartedWithinDeadlineAsync(
        CancellationToken cancellationToken)
    {
        if (_host is not null && _funnelProcess is { HasExited: false } && _status.FunnelRunning)
        {
            return Snapshot;
        }

        if (_guardedHost is not null)
        {
            var released = await TryReleaseGuardedHostAsync(cancellationToken).ConfigureAwait(false);
            if (!released)
            {
                return Publish("blocked", "tailscale.route_removal_unconfirmed");
            }
        }

        var node = await tailscale.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!node.IsConnected || node.DnsName is null || node.PublicOrigin is null)
        {
            return Publish("unavailable", node.ErrorCode ?? "tailscale.status_unavailable");
        }

        var target = ProductTailscalePlatform.CreateTarget(LocalWebPort);
        var before = await tailscale.GetFunnelStatusAsync(node.DnsName, LocalWebPort, cancellationToken)
            .ConfigureAwait(false);
        if (before.Disposition != ProductFunnelRouteDisposition.Absent)
        {
            return Publish(
                "blocked",
                before.ErrorCode ?? "tailscale.funnel_route_conflict");
        }

        IProductRemoteWebHost? startingHost = null;
        IProductOwnedFunnelProcess? startingProcess = null;
        try
        {
            startingHost = await hostFactory.StartAsync(
                    node.PublicOrigin,
                    LocalWebPort,
                    applicationLifetime.ApplicationStopping,
                    cancellationToken)
                .ConfigureAwait(false);

            // Re-check immediately after binding. A concurrently created route or changed node
            // identity must never be treated as this Service's property.
            var confirmedNode = await tailscale.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
            var confirmedRoute = confirmedNode.DnsName is not null
                ? await tailscale.GetFunnelStatusAsync(
                        confirmedNode.DnsName,
                        LocalWebPort,
                        cancellationToken)
                    .ConfigureAwait(false)
                : new ProductFunnelRouteStatus(
                    ProductFunnelRouteDisposition.Indeterminate,
                    "tailscale.status_unavailable");
            if (!confirmedNode.IsConnected ||
                confirmedNode.PublicOrigin is null ||
                !SameOrigin(node.PublicOrigin, confirmedNode.PublicOrigin) ||
                confirmedRoute.Disposition != ProductFunnelRouteDisposition.Absent)
            {
                return await AbortStartAsync(
                        startingHost,
                        null,
                        confirmedNode.DnsName ?? node.DnsName,
                        "tailscale.precondition_changed",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            startingProcess = await tailscale.StartFunnelAsync(LocalWebPort, cancellationToken)
                .ConfigureAwait(false);
            ProductFunnelRouteStatus? lastProbe = null;
            for (var attempt = 0; attempt < StartupProbeAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (startingProcess.HasExited)
                {
                    return await AbortStartAsync(
                            startingHost,
                            startingProcess,
                            node.DnsName,
                            "tailscale.funnel_process_exited",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                lastProbe = await tailscale.GetFunnelStatusAsync(
                        node.DnsName,
                        LocalWebPort,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (lastProbe.Disposition == ProductFunnelRouteDisposition.ExactTarget &&
                    HasExactForegroundSuccessMarker(startingProcess, node.PublicOrigin))
                {
                    _host = startingHost;
                    _funnelProcess = startingProcess;
                    _dnsName = node.DnsName;
                    _target = target;
                    startingHost = null;
                    startingProcess = null;
                    logger.LogInformation("Remote Web Funnel is active at the verified Tailscale origin.");
                    return Publish("running", null, publicUrl: node.PublicOrigin.ToString());
                }

                if (lastProbe.Disposition == ProductFunnelRouteDisposition.Conflict)
                {
                    break;
                }

                await Task.Delay(StartupProbeDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            }

            return await AbortStartAsync(
                    startingHost,
                    startingProcess,
                    node.DnsName,
                    lastProbe?.ErrorCode ?? "tailscale.funnel_start_timeout",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (startingHost is not null || startingProcess is not null)
            {
                await AbortStartAsync(
                        startingHost,
                        startingProcess,
                        node.DnsName,
                        "remote.operation_cancelled",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (startingHost is not null || startingProcess is not null)
            {
                await AbortStartAsync(
                        startingHost,
                        startingProcess,
                        node.DnsName,
                        "remote.start_failed",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            logger.LogWarning(error, "Remote Web startup failed before ownership could be committed.");
            return Publish("retrying", "remote.start_failed");
        }
    }

    private async Task<ProductRemoteWebStatus> StopRuntimeCoreAsync(
        bool shutdown,
        CancellationToken cancellationToken)
    {
        var host = _host;
        var process = _funnelProcess;
        var dnsName = _dnsName;
        _host = null;
        _funnelProcess = null;
        _dnsName = null;
        _target = null;

        QuiesceHost(host);
        var processStopped = await StopOwnedProcessAsync(process, cancellationToken).ConfigureAwait(false);
        var routeAbsent = process is null || await WaitForRouteAbsentAsync(dnsName, cancellationToken)
            .ConfigureAwait(false);

        if (host is not null)
        {
            if (shutdown || routeAbsent)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _guardedHost = host;
                _guardedDnsName = dnsName;
            }
        }

        if (shutdown && _guardedHost is { } guard)
        {
            _guardedHost = null;
            _guardedDnsName = null;
            await guard.DisposeAsync().ConfigureAwait(false);
        }

        if (!processStopped)
        {
            return Publish("blocked", "tailscale.funnel_process_stop_failed");
        }

        if (!routeAbsent && !shutdown)
        {
            return Publish("blocked", "tailscale.route_removal_unconfirmed");
        }

        return Publish(shutdown ? "stopped" : "disabled", null);
    }

    private async Task<ProductRemoteWebStatus> AbortStartAsync(
        IProductRemoteWebHost? host,
        IProductOwnedFunnelProcess? process,
        string? dnsName,
        string errorCode,
        CancellationToken cancellationToken)
    {
        QuiesceHost(host);
        await StopOwnedProcessAsync(process, cancellationToken).ConfigureAwait(false);
        var routeAbsent = process is null || await WaitForRouteAbsentAsync(dnsName, cancellationToken)
            .ConfigureAwait(false);
        if (host is not null)
        {
            if (routeAbsent)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _guardedHost = host;
                _guardedDnsName = dnsName;
            }
        }

        return Publish(routeAbsent ? "retrying" : "blocked", errorCode);
    }

    private async Task<bool> TryReleaseGuardedHostAsync(CancellationToken cancellationToken)
    {
        if (_guardedHost is null)
        {
            return true;
        }

        if (_guardedDnsName is null ||
            !await WaitForRouteAbsentAsync(_guardedDnsName, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var guard = _guardedHost;
        _guardedHost = null;
        _guardedDnsName = null;
        await guard.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private async Task<bool> WaitForRouteAbsentAsync(
        string? dnsName,
        CancellationToken cancellationToken)
    {
        if (dnsName is null)
        {
            return false;
        }

        for (var attempt = 0; attempt < RemovalProbeAttempts; attempt++)
        {
            var status = await tailscale.GetFunnelStatusAsync(dnsName, LocalWebPort, cancellationToken)
                .ConfigureAwait(false);
            if (status.Disposition == ProductFunnelRouteDisposition.Absent)
            {
                return true;
            }

            if (status.Disposition == ProductFunnelRouteDisposition.Conflict)
            {
                // We cannot prove that a multi-candidate conflict no longer contains our old
                // target. Retain the deny-all guard and never mutate the unknown route.
                return false;
            }

            if (attempt + 1 < RemovalProbeAttempts)
            {
                await Task.Delay(RemovalProbeDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static void QuiesceHost(IProductRemoteWebHost? host)
    {
        if (host is null)
        {
            return;
        }

        host.RevokeAllSessions();
        host.EnterFailClosedMode();
    }

    private static async Task<bool> StopOwnedProcessAsync(
        IProductOwnedFunnelProcess? process,
        CancellationToken cancellationToken)
    {
        if (process is null)
        {
            return true;
        }

        try
        {
            await process.StopAsync(cancellationToken).ConfigureAwait(false);
            return process.HasExited;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void EnsureIntentLoaded()
    {
        if (_intentLoaded)
        {
            return;
        }

        try
        {
            _desiredEnabled = intentStore.ReadDesiredEnabled();
            _intentLoaded = true;
            Publish(_desiredEnabled ? "waiting" : "disabled", null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A corrupt intent never silently enables public ingress. It must be replaced by an
            // explicit authenticated Start/Stop API operation.
            _desiredEnabled = false;
            _intentLoaded = true;
            logger.LogError(error, "Remote Web intent could not be loaded; public ingress remains disabled.");
            Publish("blocked", "remote.intent_invalid");
        }
    }

    private bool ShouldAutoStart()
        => WindowsServiceHelpers.IsWindowsService() || serviceOptions.EnableRemoteWebInConsole;

    private async Task WaitForServiceReadyAsync(CancellationToken cancellationToken)
    {
        while (!serviceState.IsReady)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WaitForWakeOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(delay);
        try
        {
            await _wake.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            while (_wake.Reader.TryRead(out _))
            {
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Signal() => _wake.Writer.TryWrite(0);

    private ProductRemoteWebStatus Publish(
        string state,
        string? errorCode,
        DateTimeOffset? nextRetry = null,
        string? publicUrl = null)
    {
        lock (_statusGate)
        {
            var status = new ProductRemoteWebStatus(
                _desiredEnabled,
                _host is not null,
                _funnelProcess is { HasExited: false },
                publicUrl ?? (_status.FunnelRunning ? _status.PublicUrl : null),
                state,
                errorCode,
                timeProvider.GetUtcNow(),
                nextRetry);
            _status = status;
            return status;
        }
    }

    private void ThrowIfStopping()
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            throw new InvalidOperationException("Remote Web supervisor is stopping.");
        }
    }

    private static bool SameOrigin(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
           left.Port == right.Port;

    private static bool HasExactForegroundSuccessMarker(
        IProductOwnedFunnelProcess process,
        Uri publicOrigin)
        => process.StandardOutput.Contains(
            publicOrigin.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);
}
