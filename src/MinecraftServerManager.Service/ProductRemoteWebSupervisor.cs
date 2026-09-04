using System.Threading.Channels;
using MinecraftServerManager.Contracts;
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
    DateTimeOffset? NextRetryAtUtc)
{
    public bool RouteConfigured { get; init; }

    public bool RouteStatusCached { get; init; }

    public DateTimeOffset? RouteLastVerifiedAtUtc { get; init; }
}

public interface IProductRemoteWebSupervisor
{
    ProductRemoteWebStatus Snapshot { get; }

    Task<ProductRemoteWebStatus> EnableAsync(CancellationToken cancellationToken);

    Task<ProductRemoteWebStatus> DisableAsync(CancellationToken cancellationToken);

    Task<ProductRemoteWebStatus> ReconnectAsync(CancellationToken cancellationToken);

    Task<ProductRemoteAccessRouteChallenge> PrepareRouteAsync(
        string publicUrl,
        CancellationToken cancellationToken);

    Task<ProductRemoteWebStatus> CommitRouteAsync(
        Guid operationId,
        DateTimeOffset verifiedAtUtc,
        CancellationToken cancellationToken);

    Task<ProductRemoteAccessRouteChallenge> PrepareRouteRemovalAsync(
        string? recoveryPublicUrl,
        CancellationToken cancellationToken);

    Task<ProductRemoteWebStatus> CommitRouteRemovalAsync(
        Guid operationId,
        DateTimeOffset verifiedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns the formal remote Web listener. Console/dev mode may own a foreground Funnel process;
/// the installed Windows Service instead accepts a narrowly validated route receipt from the
/// authenticated interactive desktop client because Tailscale's Windows LocalAPI is single-user.
/// A receipt is configuration evidence only and is never reported as live Funnel health.
/// </summary>
internal sealed class ProductRemoteWebSupervisor(
    ProductServiceOptions serviceOptions,
    ProductServiceState serviceState,
    ProductRemoteWebIntentStore intentStore,
    ProductRemoteWebRouteStore routeStore,
    IProductRemoteWebHostFactory hostFactory,
    IProductTailscalePlatform tailscale,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider timeProvider,
    ILogger<ProductRemoteWebSupervisor> logger) : BackgroundService, IProductRemoteWebSupervisor
{
    public const int LocalWebPort = 42871;
    internal const string RequiredMachineHostname = "x-mcsv";
    private const int StartupProbeAttempts = 20;
    private const int HostnameProbeAttempts = 20;
    private const int RemovalProbeAttempts = 4;
    private static readonly TimeSpan StartupProbeDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HostnameProbeDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RemovalProbeDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RunningProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RouteChallengeLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan VerificationClockTolerance = TimeSpan.FromSeconds(5);

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
    private Uri? _knownPublicOrigin;
    private ProductRemoteWebRouteReceipt? _routeReceipt;
    private RouteChallenge? _routeChallenge;
    private Uri? _preparedOrigin;
    private bool _intentLoaded;
    private bool _routeLoaded;
    private bool _routeReceiptInvalid;
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
            EnsureRouteLoaded();
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
            EnsureRouteLoaded();
            if (UsesInteractiveRouteReceipts())
            {
                if (_routeReceipt is not null ||
                    _preparedOrigin is not null ||
                    _knownPublicOrigin is not null ||
                    _host is not null ||
                    _routeReceiptInvalid)
                {
                    return Publish("blocked", "tailscale.interactive_route_removal_required");
                }

                // No Service-authorized route or runtime has ever been established. Disabling the
                // durable intent is safe and does not mutate any unrelated Tailscale configuration.
                intentStore.WriteDesiredEnabled(false);
                _desiredEnabled = false;
                return Publish("disabled", null);
            }
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
            EnsureRouteLoaded();
            intentStore.WriteDesiredEnabled(true);
            _desiredEnabled = true;
            if (UsesInteractiveRouteReceipts())
            {
                // Keep the verified host bound while the desktop process validates/replaces the
                // persistent route. Stopping it here would create a public route to an empty port.
                return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            await StopRuntimeCoreAsync(shutdown: false, CancellationToken.None).ConfigureAwait(false);
            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            Signal();
        }
    }

    public async Task<ProductRemoteAccessRouteChallenge> PrepareRouteAsync(
        string publicUrl,
        CancellationToken cancellationToken)
    {
        var origin = ProductRemoteWebRouteStore.ValidateCanonicalPublicOrigin(publicUrl);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            EnsureReceiptMode();
            EnsureIntentLoaded();
            EnsureRouteLoaded();
            if (!serviceState.IsReady)
            {
                throw new ProductRemoteRouteOperationException(
                    "remote.service_not_ready",
                    "Remote Web cannot prepare a route until the Service is ready.");
            }

            var reusable = ReuseOrRejectActiveChallenge(RouteChallengeKind.Provision, origin);

            if (_routeReceipt is { } existing && !SameOrigin(existing.PublicOrigin, origin))
            {
                throw new ProductRemoteRouteOperationException(
                    "tailscale.route_origin_changed",
                    "Remove the previously verified route before changing Tailnet origins.");
            }

            if (_host is not null &&
                _knownPublicOrigin is { } hostedOrigin &&
                !SameOrigin(hostedOrigin, origin))
            {
                throw new ProductRemoteRouteOperationException(
                    "tailscale.route_origin_changed",
                    "The currently bound Web host belongs to another Tailnet origin.");
            }

            if (_host is null)
            {
                _host = await hostFactory.StartAsync(
                        origin,
                        LocalWebPort,
                        applicationLifetime.ApplicationStopping,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _knownPublicOrigin = origin;
            _preparedOrigin = origin;
            intentStore.WriteDesiredEnabled(true);
            _desiredEnabled = true;
            if (reusable is not null)
            {
                Publish("awaiting_route_verification", null, publicUrl: origin.ToString());
                return reusable;
            }

            var now = timeProvider.GetUtcNow();
            var challenge = new RouteChallenge(
                Guid.NewGuid(),
                RouteChallengeKind.Provision,
                origin,
                now,
                now + RouteChallengeLifetime);
            _routeChallenge = challenge;
            Publish("awaiting_route_verification", null, publicUrl: origin.ToString());
            return ToPublicChallenge(challenge);
        }
        finally
        {
            _operationGate.Release();
            Signal();
        }
    }

    public async Task<ProductRemoteWebStatus> CommitRouteAsync(
        Guid operationId,
        DateTimeOffset verifiedAtUtc,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            EnsureReceiptMode();
            EnsureIntentLoaded();
            EnsureRouteLoaded();
            var challenge = ValidateChallenge(
                operationId,
                verifiedAtUtc,
                RouteChallengeKind.Provision);
            if (_host is null ||
                _knownPublicOrigin is null ||
                !SameOrigin(_knownPublicOrigin, challenge.PublicOrigin))
            {
                throw new ProductRemoteRouteOperationException(
                    "remote.route_host_not_ready",
                    "The loopback Web host is no longer bound for this route.");
            }

            var receipt = routeStore.WriteVerified(
                challenge.PublicOrigin.ToString(),
                verifiedAtUtc);
            // This commit trusts an attestation from the authenticated ServiceManage desktop
            // client. It is not an independent Service-side proof of current Tailscale state.
            intentStore.WriteDesiredEnabled(true);
            _routeReceipt = receipt;
            _routeLoaded = true;
            _routeReceiptInvalid = false;
            _desiredEnabled = true;
            _preparedOrigin = null;
            _routeChallenge = null;
            return Publish("configured", null, publicUrl: receipt.PublicOrigin.ToString());
        }
        finally
        {
            _operationGate.Release();
            Signal();
        }
    }

    public async Task<ProductRemoteAccessRouteChallenge> PrepareRouteRemovalAsync(
        string? recoveryPublicUrl,
        CancellationToken cancellationToken)
    {
        var recoveryOrigin = recoveryPublicUrl is null
            ? null
            : ProductRemoteWebRouteStore.ValidateCanonicalPublicOrigin(recoveryPublicUrl);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            EnsureReceiptMode();
            EnsureIntentLoaded();
            EnsureRouteLoaded();
            var origin = _routeReceipt?.PublicOrigin ?? _preparedOrigin ?? _knownPublicOrigin;
            var requestedOrigin = recoveryOrigin ?? origin;
            var reusable = requestedOrigin is null
                ? null
                : ReuseOrRejectActiveChallenge(RouteChallengeKind.Remove, requestedOrigin);
            if (recoveryOrigin is not null)
            {
                var recoveringPreviouslyAdoptedOrigin =
                    _routeReceiptInvalid &&
                    _routeReceipt is null &&
                    _preparedOrigin is not null &&
                    SameOrigin(_preparedOrigin, recoveryOrigin);
                if (!_routeReceiptInvalid ||
                    _routeReceipt is not null ||
                    (origin is not null && !recoveringPreviouslyAdoptedOrigin))
                {
                    throw new ProductRemoteRouteOperationException(
                        "remote.route_recovery_not_allowed",
                        "A recovery origin is accepted only when a corrupt receipt left no known origin.");
                }

                origin = recoveryOrigin;
            }

            if (_routeReceiptInvalid && origin is null)
            {
                throw new ProductRemoteRouteOperationException(
                    "remote.route_recovery_url_required",
                    "A canonical origin from the authenticated desktop is required to remove this corrupt receipt.");
            }

            if (origin is null)
            {
                throw new ProductRemoteRouteOperationException(
                    "tailscale.route_not_configured",
                    "No managed Tailscale route is available for removal.");
            }

            if (_host is not null &&
                _knownPublicOrigin is { } hostedOrigin &&
                !SameOrigin(hostedOrigin, origin))
            {
                throw new ProductRemoteRouteOperationException(
                    "remote.route_host_origin_mismatch",
                    "The loopback Web host is bound for another route origin.");
            }

            if (_host is null)
            {
                _host = await hostFactory.StartAsync(
                        origin,
                        _routeReceipt?.LocalPort ?? LocalWebPort,
                        applicationLifetime.ApplicationStopping,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _knownPublicOrigin = origin;
            if (_routeReceiptInvalid)
            {
                _preparedOrigin = origin;
            }

            if (reusable is not null)
            {
                Publish("awaiting_route_removal", null, publicUrl: origin.ToString());
                return reusable;
            }

            var now = timeProvider.GetUtcNow();
            var challenge = new RouteChallenge(
                Guid.NewGuid(),
                RouteChallengeKind.Remove,
                origin,
                now,
                now + RouteChallengeLifetime);
            _routeChallenge = challenge;
            // Deliberately retain the bound host and durable receipt until the interactive
            // caller has proved this is the only configured Funnel route, issued `funnel reset`,
            // and independently observed an empty configuration.
            Publish("awaiting_route_removal", null, publicUrl: origin.ToString());
            return ToPublicChallenge(challenge);
        }
        finally
        {
            _operationGate.Release();
            Signal();
        }
    }

    public async Task<ProductRemoteWebStatus> CommitRouteRemovalAsync(
        Guid operationId,
        DateTimeOffset verifiedAtUtc,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            EnsureReceiptMode();
            EnsureIntentLoaded();
            EnsureRouteLoaded();
            _ = ValidateChallenge(operationId, verifiedAtUtc, RouteChallengeKind.Remove);

            // Tailscale 1.102 no longer accepts the historical `off` form. ServiceManage is an
            // authenticated trusted desktop client; its commit attests that it proved the complete
            // Funnel config contained only this managed route, issued `funnel reset`, and observed
            // an empty config. The Service cannot independently query that user's LocalAPI session.
            // Stop serving first; if receipt deletion then fails, durable intent enables recovery.
            var stopped = await StopRuntimeCoreAsync(shutdown: false, CancellationToken.None)
                .ConfigureAwait(false);
            if (stopped.HostRunning)
            {
                throw new IOException("Remote Web host did not stop after route removal.");
            }

            routeStore.Delete();
            intentStore.WriteDesiredEnabled(false);
            _routeReceipt = null;
            _routeLoaded = true;
            _routeReceiptInvalid = false;
            _routeChallenge = null;
            _preparedOrigin = null;
            _knownPublicOrigin = null;
            _desiredEnabled = false;
            return Publish("disabled", null);
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
                    EnsureRouteLoaded();
                    if (_funnelProcess is { HasExited: true })
                    {
                        var processError = ProductTailscalePlatform.ClassifyLocalApiFailure(
                            _funnelProcess.StandardOutput,
                            _funnelProcess.StandardError,
                            "tailscale.funnel_process_exited");
                        await StopRuntimeCoreAsync(shutdown: false, stoppingToken).ConfigureAwait(false);
                        Publish("retrying", processError);
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
                        if (current.FunnelRunning ||
                            (UsesInteractiveRouteReceipts() &&
                             current.HostRunning &&
                             current.RouteConfigured))
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

    private async Task<ProductRemoteWebStatus> EnsureReceiptBackedHostStartedAsync(
        CancellationToken cancellationToken)
    {
        EnsureRouteLoaded();
        if (_routeReceiptInvalid)
        {
            return Publish("blocked", "remote.route_receipt_invalid");
        }

        if (_host is not null)
        {
            var expected = _routeReceipt?.PublicOrigin ?? _preparedOrigin;
            if (expected is null ||
                _knownPublicOrigin is null ||
                !SameOrigin(expected, _knownPublicOrigin))
            {
                return Publish("blocked", "remote.route_host_origin_mismatch");
            }

            return Publish(
                _routeReceipt is null ? "awaiting_route_verification" : "configured_cached",
                null,
                publicUrl: expected.ToString());
        }

        if (_routeReceipt is null)
        {
            return Publish("awaiting_interactive_route", "tailscale.interactive_route_required");
        }

        _host = await hostFactory.StartAsync(
                _routeReceipt.PublicOrigin,
                _routeReceipt.LocalPort,
                applicationLifetime.ApplicationStopping,
                cancellationToken)
            .ConfigureAwait(false);
        _knownPublicOrigin = _routeReceipt.PublicOrigin;
        logger.LogInformation("Remote Web host restored from a verified persistent-route receipt.");
        return Publish(
            "configured_cached",
            null,
            publicUrl: _routeReceipt.PublicOrigin.ToString());
    }

    private async Task<ProductRemoteWebStatus> EnsureStartedWithinDeadlineAsync(
        CancellationToken cancellationToken)
    {
        if (UsesInteractiveRouteReceipts())
        {
            return await EnsureReceiptBackedHostStartedAsync(cancellationToken).ConfigureAwait(false);
        }

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
        if (!node.IsConnected || node.DnsName is null)
        {
            return Publish("unavailable", node.ErrorCode ?? "tailscale.status_unavailable");
        }

        var hasRequiredMachineHostname = HasRequiredMachineHostname(node.DnsName);
        if (!hasRequiredMachineHostname)
        {
            // A connected node with another identity proves that a previously remembered origin
            // no longer describes this node. Do not show a stale URL while the rename is pending.
            _knownPublicOrigin = null;
        }

        // Even when MagicDNS currently happens to expose x-mcsv, query the explicit preference.
        // An empty override is not durable against a later Windows computer-name change. The
        // platform performs a read first and issues `set` only when the override is absent/different.
        var hostname = await tailscale.EnsureMachineHostnameAsync(
                RequiredMachineHostname,
                cancellationToken)
            .ConfigureAwait(false);
        if (!hostname.Succeeded)
        {
            return Publish(
                hostname.ErrorCode?.EndsWith("_timeout", StringComparison.Ordinal) == true
                    ? "retrying"
                    : "blocked",
                hostname.ErrorCode ?? "tailscale.hostname_set_failed");
        }

        if (hostname.Changed || !hasRequiredMachineHostname)
        {
            node = await WaitForRequiredMachineHostnameAsync(node, cancellationToken)
                .ConfigureAwait(false);
            if (!node.IsConnected || node.DnsName is null)
            {
                return Publish("unavailable", node.ErrorCode ?? "tailscale.hostname_verification_failed");
            }

            if (!HasRequiredMachineHostname(node.DnsName))
            {
                // The requested preference is already set at this point. A different MagicDNS
                // label therefore indicates a conflicting/unavailable name or control-plane
                // propagation that never completed within the bounded verification window.
                return Publish("blocked", "tailscale.hostname_unavailable");
            }
        }

        if (node.PublicOrigin is null)
        {
            return Publish("unavailable", node.ErrorCode ?? "tailscale.https_not_enabled");
        }

        RememberCanonicalPublicOrigin(node.PublicOrigin);

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
                            ProductTailscalePlatform.ClassifyLocalApiFailure(
                                startingProcess.StandardOutput,
                                startingProcess.StandardError,
                                "tailscale.funnel_process_exited"),
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

    private async Task<ProductTailscaleNodeStatus> WaitForRequiredMachineHostnameAsync(
        ProductTailscaleNodeStatus initial,
        CancellationToken cancellationToken)
    {
        var current = initial;
        for (var attempt = 0; attempt < HostnameProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = await tailscale.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
            if (current.IsConnected &&
                current.DnsName is not null &&
                HasRequiredMachineHostname(current.DnsName))
            {
                return current;
            }

            if (current.ErrorCode is "tailscale.backend_not_running" or "tailscale.not_installed")
            {
                return current;
            }

            if (attempt + 1 < HostnameProbeAttempts)
            {
                await Task.Delay(HostnameProbeDelay, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return current;
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

    private void EnsureRouteLoaded()
    {
        if (_routeLoaded)
        {
            return;
        }

        try
        {
            _routeReceipt = routeStore.Read();
            _knownPublicOrigin = _routeReceipt?.PublicOrigin;
            _routeReceiptInvalid = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A corrupt or inaccessible receipt must never become authority for a public origin.
            // Keep the durable intent intact so an authenticated desktop can replace the receipt.
            _routeReceipt = null;
            _knownPublicOrigin = null;
            _routeReceiptInvalid = true;
            logger.LogError(error, "Remote Web route receipt could not be loaded; no cached route will be trusted.");
        }
        finally
        {
            _routeLoaded = true;
        }
    }

    private bool UsesInteractiveRouteReceipts()
        => WindowsServiceHelpers.IsWindowsService() || serviceOptions.UseInteractiveTailscaleRouteReceipts;

    private void EnsureReceiptMode()
    {
        if (!UsesInteractiveRouteReceipts())
        {
            throw new ProductRemoteRouteOperationException(
                "remote.route_receipts_unavailable",
                "Interactive route receipts are available only for the installed Service mode.");
        }
    }

    private ProductRemoteAccessRouteChallenge? ReuseOrRejectActiveChallenge(
        RouteChallengeKind requestedKind,
        Uri requestedOrigin)
    {
        if (_routeChallenge is not { } active)
        {
            return null;
        }

        if (timeProvider.GetUtcNow() > active.ExpiresAtUtc)
        {
            _routeChallenge = null;
            return null;
        }

        if (active.Kind == requestedKind && SameOrigin(active.PublicOrigin, requestedOrigin))
        {
            return ToPublicChallenge(active);
        }

        throw new ProductRemoteRouteOperationException(
            "remote.route_operation_in_progress",
            "Another unexpired route operation must finish before a different operation can begin.");
    }

    private RouteChallenge ValidateChallenge(
        Guid operationId,
        DateTimeOffset verifiedAtUtc,
        RouteChallengeKind expectedKind)
    {
        if (operationId == Guid.Empty || verifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-empty operation id and UTC verification time are required.");
        }

        var challenge = _routeChallenge;
        if (challenge is null ||
            challenge.OperationId != operationId ||
            challenge.Kind != expectedKind)
        {
            throw new ProductRemoteRouteOperationException(
                "remote.route_challenge_invalid",
                "The route challenge is missing or does not match this operation.");
        }

        var now = timeProvider.GetUtcNow();
        if (now > challenge.ExpiresAtUtc)
        {
            _routeChallenge = null;
            throw new ProductRemoteRouteOperationException(
                "remote.route_challenge_expired",
                "The route challenge expired before verification was committed.");
        }

        if (verifiedAtUtc < challenge.IssuedAtUtc - VerificationClockTolerance ||
            verifiedAtUtc > now + VerificationClockTolerance ||
            verifiedAtUtc > challenge.ExpiresAtUtc + VerificationClockTolerance)
        {
            throw new ProductRemoteRouteOperationException(
                "remote.route_verification_time_invalid",
                "The route verification time is outside this challenge's validity window.");
        }

        return challenge;
    }

    private static ProductRemoteAccessRouteChallenge ToPublicChallenge(RouteChallenge challenge)
        => new(
            challenge.OperationId,
            challenge.PublicOrigin.ToString(),
            challenge.ExpiresAtUtc,
            LocalWebPort);

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
            var receiptMode = UsesInteractiveRouteReceipts();
            var status = new ProductRemoteWebStatus(
                _desiredEnabled,
                _host is not null,
                !receiptMode && _funnelProcess is { HasExited: false },
                publicUrl ?? _knownPublicOrigin?.ToString(),
                state,
                errorCode,
                timeProvider.GetUtcNow(),
                nextRetry)
            {
                RouteConfigured = receiptMode && _routeReceipt is not null,
                // The Service cannot safely query Tailscale's live LocalAPI while the tray owns
                // another SID. Even a freshly committed receipt becomes cached status afterward.
                RouteStatusCached = receiptMode && _routeReceipt is not null,
                RouteLastVerifiedAtUtc = receiptMode ? _routeReceipt?.VerifiedAtUtc : null,
            };
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

    private static bool HasRequiredMachineHostname(string dnsName)
    {
        if (!ProductTailscaleProtocol.TryNormalizeDnsName(dnsName, out var normalized))
        {
            return false;
        }

        var separator = normalized.IndexOf('.');
        return separator == RequiredMachineHostname.Length &&
               normalized.AsSpan(0, separator).Equals(
                   RequiredMachineHostname.AsSpan(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private void RememberCanonicalPublicOrigin(Uri origin)
    {
        if (origin.Scheme != Uri.UriSchemeHttps ||
            origin.UserInfo.Length != 0 ||
            origin.Port != 443 ||
            (origin.AbsolutePath.Length != 0 && origin.AbsolutePath != "/") ||
            origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 ||
            !HasRequiredMachineHostname(origin.Host))
        {
            throw new InvalidDataException("Tailscale public origin is not canonical for this product.");
        }

        _knownPublicOrigin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    private static bool HasExactForegroundSuccessMarker(
        IProductOwnedFunnelProcess process,
        Uri publicOrigin)
        => process.StandardOutput.Contains(
            publicOrigin.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);

    private enum RouteChallengeKind
    {
        Provision,
        Remove,
    }

    private sealed record RouteChallenge(
        Guid OperationId,
        RouteChallengeKind Kind,
        Uri PublicOrigin,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}

internal sealed class ProductRemoteRouteOperationException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
