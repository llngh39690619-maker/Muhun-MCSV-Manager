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
        Assert.Equal("https://box.tail.ts.net/", enabled.PublicUrl);
        Assert.False(disabled.DesiredEnabled);
        Assert.False(disabled.HostRunning);
        Assert.False(disabled.FunnelRunning);
        Assert.Equal("disabled", disabled.State);
        Assert.False(intentStore.ReadDesiredEnabled());
        Assert.Equal(ProductRemoteWebSupervisor.LocalWebPort, hostFactory.Port);
        Assert.Equal("https://box.tail.ts.net/", hostFactory.Origin?.ToString());
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
            hostFactory,
            platform,
            new FakeApplicationLifetime(),
            TimeProvider.System,
            NullLogger<ProductRemoteWebSupervisor>.Instance);
        return (supervisor, intentStore);
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

    private sealed class FakePlatform(
        List<string> events,
        IEnumerable<ProductFunnelRouteDisposition> dispositions) : IProductTailscalePlatform
    {
        private readonly Queue<ProductFunnelRouteDisposition> _dispositions = new(dispositions);

        public int StartCount { get; private set; }
        public int NodeStatusCount { get; private set; }
        public IReadOnlyList<string>? StartArguments { get; private set; }

        public Task<ProductTailscaleNodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken)
        {
            NodeStatusCount++;
            return Task.FromResult(new ProductTailscaleNodeStatus(
                true,
                "box.tail.ts.net",
                new Uri("https://box.tail.ts.net"),
                null));
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
        public string StandardOutput => "https://box.tail.ts.net";
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
}
