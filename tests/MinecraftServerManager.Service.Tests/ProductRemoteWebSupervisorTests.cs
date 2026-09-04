using MinecraftServerManager.Service;
using MinecraftServerManager.Remote;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductRemoteWebSupervisorTests
{
    [Fact]
    public void FormalHostOptions_ArePublicFunnelLoopbackWithMandatoryDurableAudit()
    {
        var options = ProductRemoteWebHostFactory.CreateOptions(
            new Uri("https://box.tail.ts.net"),
            ProductRemoteWebSupervisor.LocalWebPort,
            new CancellationToken(canceled: true));

        Assert.Equal(ProductRemoteWebSupervisor.LocalWebPort, options.Port);
        Assert.Equal(RemoteIngressMode.TailscaleFunnel, options.IngressMode);
        Assert.Empty(options.AllowedGoogleLogins);
        Assert.True(options.RequireDurableSecurityAudit);
        Assert.True(options.OperationCancellationToken.IsCancellationRequested);
        RemoteControlOptionsValidator.ValidateAndThrow(options);
    }

    [Fact]
    public async Task ConsoleHost_DoesNotAutomaticallyTouchTailscaleWithoutExplicitOptIn()
    {
        var events = new List<string>();
        var platform = new FakePlatform(events, []);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, _) = CreateSupervisor(platform, hostFactory);

        await supervisor.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await supervisor.StopAsync(CancellationToken.None);

        Assert.Equal(0, platform.NodeStatusCount);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(0, hostFactory.StartCount);
    }

    [Fact]
    public async Task EnableThenDisable_OwnsExactRouteAndStopsIngressBeforeHost()
    {
        var events = new List<string>();
        var platform = new FakePlatform(events,
        [
            ProductFunnelRouteDisposition.Absent,
            ProductFunnelRouteDisposition.Absent,
            ProductFunnelRouteDisposition.ExactTarget,
            ProductFunnelRouteDisposition.Absent,
        ]);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, intentStore) = CreateSupervisor(platform, hostFactory);

        var enabled = await supervisor.EnableAsync(CancellationToken.None);
        var disabled = await supervisor.DisableAsync(CancellationToken.None);

        Assert.True(enabled.DesiredEnabled);
        Assert.True(enabled.HostRunning);
        Assert.True(enabled.FunnelRunning);
        Assert.Equal("https://x-mcsv.tail.ts.net/", enabled.PublicUrl);
        Assert.False(disabled.DesiredEnabled);
        Assert.False(disabled.HostRunning);
        Assert.False(disabled.FunnelRunning);
        Assert.Equal("disabled", disabled.State);
        Assert.False(intentStore.ReadDesiredEnabled());
        Assert.Equal(ProductRemoteWebSupervisor.LocalWebPort, hostFactory.Port);
        Assert.Equal("https://x-mcsv.tail.ts.net/", hostFactory.Origin?.ToString());
        Assert.Equal(1, platform.EnsureHostnameCount);
        Assert.Equal(
            ["funnel", "--yes", "--https=443", "http://127.0.0.1:42871"],
            platform.StartArguments);
        AssertOrder(events, "host.revoke", "host.quiesce", "process.stop", "host.dispose");
    }

    [Fact]
    public async Task ExistingOrUnknownRoute_IsNeverOverwritten()
    {
        var events = new List<string>();
        var platform = new FakePlatform(events, [ProductFunnelRouteDisposition.Conflict]);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, _) = CreateSupervisor(platform, hostFactory);

        var status = await supervisor.EnableAsync(CancellationToken.None);

        Assert.Equal("blocked", status.State);
        Assert.Equal("tailscale.funnel_route_conflict", status.ErrorCode);
        Assert.Equal(0, hostFactory.StartCount);
        Assert.Equal(0, platform.StartCount);
        Assert.DoesNotContain(events, value => value.Contains("reset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChangedPreconditionAfterHostBind_QuiescesAndDisposesWithoutStartingFunnel()
    {
        var events = new List<string>();
        var platform = new FakePlatform(events,
        [
            ProductFunnelRouteDisposition.Absent,
            ProductFunnelRouteDisposition.Conflict,
        ]);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, _) = CreateSupervisor(platform, hostFactory);

        var status = await supervisor.EnableAsync(CancellationToken.None);

        Assert.Equal("retrying", status.State);
        Assert.Equal("tailscale.precondition_changed", status.ErrorCode);
        Assert.Equal(1, hostFactory.StartCount);
        Assert.Equal(0, platform.StartCount);
        AssertOrder(events, "host.start", "host.revoke", "host.quiesce", "host.dispose");
    }

    [Fact]
    public async Task ConnectedNode_IsRenamedToXMcsvBeforePublishingFixedUrl()
    {
        var events = new List<string>();
        var platform = new FakePlatform(events,
        [
            ProductFunnelRouteDisposition.Absent,
            ProductFunnelRouteDisposition.Absent,
            ProductFunnelRouteDisposition.ExactTarget,
        ]);
        platform.NodeStatuses.Enqueue(Node("old-machine.tail.ts.net"));
        platform.NodeStatuses.Enqueue(Node("x-mcsv.tail.ts.net"));
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, _) = CreateSupervisor(platform, hostFactory);

        var status = await supervisor.EnableAsync(CancellationToken.None);

        Assert.Equal(1, platform.EnsureHostnameCount);
        Assert.True(status.HostRunning);
        Assert.True(status.FunnelRunning);
        Assert.Equal("https://x-mcsv.tail.ts.net/", status.PublicUrl);
        Assert.Equal("https://x-mcsv.tail.ts.net/", hostFactory.Origin?.ToString());
    }

    [Fact]
    public async Task VerifiedFixedUrl_RemainsVisibleDuringTemporaryNodeDisconnect()
    {
        var events = new List<string>();
        var platform = new FakePlatform(events,
        [
            ProductFunnelRouteDisposition.Absent,
            ProductFunnelRouteDisposition.Absent,
            ProductFunnelRouteDisposition.ExactTarget,
            ProductFunnelRouteDisposition.Absent,
        ]);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, _) = CreateSupervisor(platform, hostFactory);
        var running = await supervisor.EnableAsync(CancellationToken.None);
        platform.NodeStatuses.Enqueue(new ProductTailscaleNodeStatus(
            false,
            null,
            null,
            "tailscale.backend_not_running"));

        var disconnected = await supervisor.ReconnectAsync(CancellationToken.None);

        Assert.Equal("https://x-mcsv.tail.ts.net/", running.PublicUrl);
        Assert.True(disconnected.DesiredEnabled);
        Assert.False(disconnected.HostRunning);
        Assert.False(disconnected.FunnelRunning);
        Assert.Equal("unavailable", disconnected.State);
        Assert.Equal("tailscale.backend_not_running", disconnected.ErrorCode);
        Assert.Equal(running.PublicUrl, disconnected.PublicUrl);
    }

    [Fact]
    public async Task ReceiptMode_NoReceiptRequestsInteractiveProvisioningWithoutClaimingLiveRoute()
    {
        var events = new List<string>();
        var platform = new FakePlatform(events, []);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, _, _, _) = CreateReceiptSupervisor(platform, hostFactory);

        var status = await supervisor.EnableAsync(CancellationToken.None);

        Assert.True(status.DesiredEnabled);
        Assert.False(status.HostRunning);
        Assert.False(status.FunnelRunning);
        Assert.False(status.RouteConfigured);
        Assert.False(status.RouteStatusCached);
        Assert.Null(status.RouteLastVerifiedAtUtc);
        Assert.Equal("awaiting_interactive_route", status.State);
        Assert.Equal("tailscale.interactive_route_required", status.ErrorCode);
        Assert.Equal(0, platform.NodeStatusCount);
        Assert.Equal(0, platform.EnsureHostnameCount);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(0, hostFactory.StartCount);
    }

    [Fact]
    public async Task ReceiptMode_PrepareCommitAndServiceRecreationRestoreOnlyCachedConfiguration()
    {
        var now = new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var events = new List<string>();
        var platform = new FakePlatform(events, []);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, _, routeStore, layout) = CreateReceiptSupervisor(
            platform,
            hostFactory,
            timeProvider: time);

        var challenge = await supervisor.PrepareRouteAsync(
            "https://x-mcsv.tail123.ts.net/",
            CancellationToken.None);

        Assert.Equal("https://x-mcsv.tail123.ts.net/", challenge.PublicUrl);
        Assert.Equal(ProductRemoteWebSupervisor.LocalWebPort, challenge.LocalPort);
        Assert.Equal(now.AddMinutes(2), challenge.ExpiresAtUtc);
        Assert.Null(routeStore.Read());
        Assert.True(supervisor.Snapshot.HostRunning);
        Assert.False(supervisor.Snapshot.RouteConfigured);

        var committed = await supervisor.CommitRouteAsync(
            challenge.OperationId,
            now,
            CancellationToken.None);

        Assert.True(committed.HostRunning);
        Assert.False(committed.FunnelRunning);
        Assert.True(committed.RouteConfigured);
        Assert.True(committed.RouteStatusCached);
        Assert.Equal(now, committed.RouteLastVerifiedAtUtc);
        Assert.Equal("https://x-mcsv.tail123.ts.net/", committed.PublicUrl);
        Assert.Equal("configured", committed.State);
        Assert.Equal(now, routeStore.Read()?.VerifiedAtUtc);
        Assert.Equal(0, platform.NodeStatusCount);
        Assert.Equal(0, platform.StartCount);

        var restartedEvents = new List<string>();
        var restartedPlatform = new FakePlatform(restartedEvents, []);
        var restartedHostFactory = new FakeHostFactory(restartedEvents);
        var (restarted, _, _, _) = CreateReceiptSupervisor(
            restartedPlatform,
            restartedHostFactory,
            layout,
            time);

        var restored = await restarted.EnableAsync(CancellationToken.None);

        Assert.True(restored.HostRunning);
        Assert.False(restored.FunnelRunning);
        Assert.True(restored.RouteConfigured);
        Assert.True(restored.RouteStatusCached);
        Assert.Equal(now, restored.RouteLastVerifiedAtUtc);
        Assert.Equal("configured_cached", restored.State);
        Assert.Equal("https://x-mcsv.tail123.ts.net/", restored.PublicUrl);
        Assert.Equal(1, restartedHostFactory.StartCount);
        Assert.Equal(0, restartedPlatform.NodeStatusCount);
        Assert.Equal(0, restartedPlatform.StartCount);
    }

    [Fact]
    public async Task ReceiptMode_RemovalRetainsHostAndReceiptUntilMatchingVerifiedCommit()
    {
        var now = new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var events = new List<string>();
        var platform = new FakePlatform(events, []);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, intentStore, routeStore, _) = CreateReceiptSupervisor(
            platform,
            hostFactory,
            timeProvider: time);
        routeStore.WriteVerified("https://x-mcsv.tail123.ts.net/", now);
        var running = await supervisor.EnableAsync(CancellationToken.None);
        var challenge = await supervisor.PrepareRouteRemovalAsync(null, CancellationToken.None);

        Assert.True(running.HostRunning);
        Assert.True(supervisor.Snapshot.HostRunning);
        Assert.True(supervisor.Snapshot.RouteConfigured);
        Assert.NotNull(routeStore.Read());

        var mismatch = await Assert.ThrowsAsync<ProductRemoteRouteOperationException>(() =>
            supervisor.CommitRouteRemovalAsync(
                Guid.NewGuid(),
                now,
                CancellationToken.None));

        Assert.Equal("remote.route_challenge_invalid", mismatch.Code);
        Assert.True(supervisor.Snapshot.HostRunning);
        Assert.True(supervisor.Snapshot.RouteConfigured);
        Assert.NotNull(routeStore.Read());
        Assert.DoesNotContain("host.dispose", events);

        var disabled = await supervisor.CommitRouteRemovalAsync(
            challenge.OperationId,
            now,
            CancellationToken.None);

        Assert.False(disabled.DesiredEnabled);
        Assert.False(disabled.HostRunning);
        Assert.False(disabled.FunnelRunning);
        Assert.False(disabled.RouteConfigured);
        Assert.False(disabled.RouteStatusCached);
        Assert.Null(disabled.PublicUrl);
        Assert.Null(routeStore.Read());
        Assert.False(intentStore.ReadDesiredEnabled());
        AssertOrder(events, "host.revoke", "host.quiesce", "host.dispose");
    }

    [Fact]
    public async Task ReceiptMode_DirectDisableCannotDeleteRouteOrStopHostBeforeDesktopReset()
    {
        var now = new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero);
        var events = new List<string>();
        var platform = new FakePlatform(events, []);
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, intentStore, routeStore, _) = CreateReceiptSupervisor(
            platform,
            hostFactory,
            timeProvider: new FixedTimeProvider(now));
        routeStore.WriteVerified("https://x-mcsv.tail123.ts.net/", now);
        await supervisor.EnableAsync(CancellationToken.None);

        var blocked = await supervisor.DisableAsync(CancellationToken.None);

        Assert.True(blocked.DesiredEnabled);
        Assert.True(blocked.HostRunning);
        Assert.True(blocked.RouteConfigured);
        Assert.Equal("blocked", blocked.State);
        Assert.Equal("tailscale.interactive_route_removal_required", blocked.ErrorCode);
        Assert.NotNull(routeStore.Read());
        Assert.True(intentStore.ReadDesiredEnabled());
        Assert.DoesNotContain("host.dispose", events);
    }

    [Fact]
    public async Task ReceiptMode_PrepareIsIdempotentAndCannotOverwriteUnexpiredOperation()
    {
        const string publicUrl = "https://x-mcsv.tail123.ts.net/";
        var now = new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var events = new List<string>();
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, _, _, _) = CreateReceiptSupervisor(
            new FakePlatform(events, []),
            hostFactory,
            timeProvider: time);

        var first = await supervisor.PrepareRouteAsync(publicUrl, CancellationToken.None);
        var repeated = await supervisor.PrepareRouteAsync(publicUrl, CancellationToken.None);

        Assert.Equal(first, repeated);
        Assert.Equal(1, hostFactory.StartCount);
        var otherOrigin = await Assert.ThrowsAsync<ProductRemoteRouteOperationException>(() =>
            supervisor.PrepareRouteAsync(
                "https://x-mcsv.other-tail.ts.net/",
                CancellationToken.None));
        Assert.Equal("remote.route_operation_in_progress", otherOrigin.Code);
        var blocked = await Assert.ThrowsAsync<ProductRemoteRouteOperationException>(() =>
            supervisor.PrepareRouteRemovalAsync(null, CancellationToken.None));
        Assert.Equal("remote.route_operation_in_progress", blocked.Code);

        time.UtcNow = now.AddMinutes(3);
        var replacement = await supervisor.PrepareRouteAsync(publicUrl, CancellationToken.None);

        Assert.NotEqual(first.OperationId, replacement.OperationId);
        Assert.Equal(1, hostFactory.StartCount);
    }

    [Fact]
    public async Task ReceiptMode_CorruptReceiptRequiresCanonicalTrustedClientRecoveryBeforeRemoval()
    {
        const string publicUrl = "https://x-mcsv.tail123.ts.net/";
        var now = new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero);
        var events = new List<string>();
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, intentStore, routeStore, layout) = CreateReceiptSupervisor(
            new FakePlatform(events, []),
            hostFactory,
            timeProvider: new FixedTimeProvider(now));
        File.WriteAllText(
            Path.Combine(layout.Operations, ProductRemoteWebRouteStore.FileName),
            """
            {
              "schemaVersion": 1,
              "publicOrigin": "https://x-mcsv.tail123.ts.net/",
              "localPort": 25565,
              "localTarget": "http://127.0.0.1:25565",
              "verifiedAtUtc": "2026-09-05T01:02:03+00:00"
            }
            """);

        var invalid = await supervisor.EnableAsync(CancellationToken.None);
        Assert.Equal("remote.route_receipt_invalid", invalid.ErrorCode);
        Assert.False(invalid.HostRunning);

        var missing = await Assert.ThrowsAsync<ProductRemoteRouteOperationException>(() =>
            supervisor.PrepareRouteRemovalAsync(null, CancellationToken.None));
        Assert.Equal("remote.route_recovery_url_required", missing.Code);

        var challenge = await supervisor.PrepareRouteRemovalAsync(publicUrl, CancellationToken.None);
        var repeated = await supervisor.PrepareRouteRemovalAsync(publicUrl, CancellationToken.None);
        Assert.Equal(challenge, repeated);
        Assert.True(supervisor.Snapshot.HostRunning);
        Assert.Equal(ProductRemoteWebSupervisor.LocalWebPort, hostFactory.Port);

        var disabled = await supervisor.CommitRouteRemovalAsync(
            challenge.OperationId,
            now,
            CancellationToken.None);

        Assert.False(disabled.DesiredEnabled);
        Assert.False(disabled.HostRunning);
        Assert.Null(routeStore.Read());
        Assert.False(intentStore.ReadDesiredEnabled());
    }

    [Theory]
    [InlineData("https://x-mcsv.attacker.example/")]
    [InlineData("https://x-mcsv.tail123.ts.net:8443/")]
    public async Task ReceiptMode_CorruptReceiptRejectsUntrustedRecoveryOriginWithoutStartingHost(
        string recoveryPublicUrl)
    {
        var events = new List<string>();
        var hostFactory = new FakeHostFactory(events);
        var (supervisor, intentStore, _, layout) = CreateReceiptSupervisor(
            new FakePlatform(events, []),
            hostFactory);
        var receiptPath = Path.Combine(
            layout.Operations,
            ProductRemoteWebRouteStore.FileName);
        File.WriteAllText(
            receiptPath,
            """
            {
              "schemaVersion": 1,
              "publicOrigin": "https://x-mcsv.tail123.ts.net/",
              "localPort": 25565,
              "localTarget": "http://127.0.0.1:25565",
              "verifiedAtUtc": "2026-09-05T01:02:03+00:00"
            }
            """);

        var invalid = await supervisor.EnableAsync(CancellationToken.None);
        var rejected = await Assert.ThrowsAsync<ArgumentException>(() =>
            supervisor.PrepareRouteRemovalAsync(recoveryPublicUrl, CancellationToken.None));

        Assert.Equal("remote.route_receipt_invalid", invalid.ErrorCode);
        Assert.Equal("publicOrigin", rejected.ParamName);
        Assert.Equal(0, hostFactory.StartCount);
        Assert.True(File.Exists(receiptPath));
        Assert.True(intentStore.ReadDesiredEnabled());
    }

    [Fact]
    public async Task ReceiptMode_ValidReceiptRejectsRecoveryUrl()
    {
        const string publicUrl = "https://x-mcsv.tail123.ts.net/";
        var now = new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero);
        var events = new List<string>();
        var (supervisor, _, routeStore, _) = CreateReceiptSupervisor(
            new FakePlatform(events, []),
            new FakeHostFactory(events),
            timeProvider: new FixedTimeProvider(now));
        routeStore.WriteVerified(publicUrl, now);

        var rejected = await Assert.ThrowsAsync<ProductRemoteRouteOperationException>(() =>
            supervisor.PrepareRouteRemovalAsync(publicUrl, CancellationToken.None));

        Assert.Equal("remote.route_recovery_not_allowed", rejected.Code);
    }

    private static (ProductRemoteWebSupervisor Supervisor, ProductRemoteWebIntentStore IntentStore)
        CreateSupervisor(FakePlatform platform, FakeHostFactory hostFactory)
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        layout.EnsureCreated();
        var intentStore = new ProductRemoteWebIntentStore(layout);
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        var supervisor = new ProductRemoteWebSupervisor(
            new ProductServiceOptions
            {
                DataRoot = layout.Root,
                EnableRemoteWebInConsole = false,
            },
            state,
            intentStore,
            new ProductRemoteWebRouteStore(layout),
            hostFactory,
            platform,
            new FakeApplicationLifetime(),
            TimeProvider.System,
            NullLogger<ProductRemoteWebSupervisor>.Instance);
        return (supervisor, intentStore);
    }

    private static (
        ProductRemoteWebSupervisor Supervisor,
        ProductRemoteWebIntentStore IntentStore,
        ProductRemoteWebRouteStore RouteStore,
        ProductDataLayout Layout) CreateReceiptSupervisor(
            FakePlatform platform,
            FakeHostFactory hostFactory,
            ProductDataLayout? layout = null,
            TimeProvider? timeProvider = null)
    {
        layout ??= ProductServerRegistryTests.CreateLayout();
        layout.EnsureCreated();
        var intentStore = new ProductRemoteWebIntentStore(layout);
        var routeStore = new ProductRemoteWebRouteStore(layout);
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        var supervisor = new ProductRemoteWebSupervisor(
            new ProductServiceOptions
            {
                DataRoot = layout.Root,
                UseInteractiveTailscaleRouteReceipts = true,
            },
            state,
            intentStore,
            routeStore,
            hostFactory,
            platform,
            new FakeApplicationLifetime(),
            timeProvider ?? TimeProvider.System,
            NullLogger<ProductRemoteWebSupervisor>.Instance);
        return (supervisor, intentStore, routeStore, layout);
    }

    private static void AssertOrder(IReadOnlyList<string> events, params string[] expected)
    {
        var previous = -1;
        foreach (var item in expected)
        {
            var index = events.ToList().FindIndex(previous + 1, value => value == item);
            Assert.True(index > previous, $"Expected '{item}' after index {previous}. Events: {string.Join(", ", events)}");
            previous = index;
        }
    }

    private static ProductTailscaleNodeStatus Node(string dnsName)
        => new(
            true,
            dnsName,
            new Uri($"https://{dnsName}"),
            null);

    private sealed class FakePlatform(
        List<string> events,
        IEnumerable<ProductFunnelRouteDisposition> dispositions) : IProductTailscalePlatform
    {
        private readonly Queue<ProductFunnelRouteDisposition> _dispositions = new(dispositions);

        public int StartCount { get; private set; }
        public int NodeStatusCount { get; private set; }
        public int EnsureHostnameCount { get; private set; }
        public Queue<ProductTailscaleNodeStatus> NodeStatuses { get; } = [];
        public IReadOnlyList<string>? StartArguments { get; private set; }

        public Task<ProductTailscaleNodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken)
        {
            NodeStatusCount++;
            return Task.FromResult(NodeStatuses.Count > 0
                ? NodeStatuses.Dequeue()
                : Node("x-mcsv.tail.ts.net"));
        }

        public Task<ProductFunnelRouteStatus> GetFunnelStatusAsync(
            string dnsName,
            int localPort,
            CancellationToken cancellationToken)
        {
            events.Add("route.probe");
            var disposition = _dispositions.Count > 0
                ? _dispositions.Dequeue()
                : ProductFunnelRouteDisposition.Absent;
            return Task.FromResult(new ProductFunnelRouteStatus(
                disposition,
                disposition == ProductFunnelRouteDisposition.Conflict
                    ? "tailscale.funnel_route_conflict"
                    : null));
        }

        public Task<ProductTailscaleHostnameUpdateResult> EnsureMachineHostnameAsync(
            string expectedHostname,
            CancellationToken cancellationToken)
        {
            EnsureHostnameCount++;
            Assert.Equal("x-mcsv", expectedHostname);
            return Task.FromResult(new ProductTailscaleHostnameUpdateResult(true, false, null));
        }

        public Task<IProductOwnedFunnelProcess> StartFunnelAsync(
            int localPort,
            CancellationToken cancellationToken)
        {
            StartCount++;
            StartArguments = ["funnel", "--yes", "--https=443", ProductTailscalePlatform.CreateTarget(localPort)];
            events.Add("process.start");
            return Task.FromResult<IProductOwnedFunnelProcess>(new FakeProcess(events));
        }
    }

    private sealed class FakeHostFactory(List<string> events) : IProductRemoteWebHostFactory
    {
        public int StartCount { get; private set; }
        public int Port { get; private set; }
        public Uri? Origin { get; private set; }

        public Task<IProductRemoteWebHost> StartAsync(
            Uri publicOrigin,
            int localPort,
            CancellationToken applicationStopping,
            CancellationToken cancellationToken)
        {
            StartCount++;
            Port = localPort;
            Origin = publicOrigin;
            events.Add("host.start");
            return Task.FromResult<IProductRemoteWebHost>(new FakeHost(events));
        }
    }

    private sealed class FakeHost(List<string> events) : IProductRemoteWebHost
    {
        public void RevokeAllSessions() => events.Add("host.revoke");
        public void EnterFailClosedMode() => events.Add("host.quiesce");
        public ValueTask DisposeAsync()
        {
            events.Add("host.dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProcess(List<string> events) : IProductOwnedFunnelProcess
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited { get; private set; }
        public int? ExitCode => HasExited ? 0 : null;
        public string StandardOutput => "https://x-mcsv.tail.ts.net";
        public string StandardError => string.Empty;
        public Task Completion => _completion.Task;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add("process.stop");
            HasExited = true;
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            events.Add("process.dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
