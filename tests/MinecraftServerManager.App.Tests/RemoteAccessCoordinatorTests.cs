using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class RemoteAccessCoordinatorTests
{
    [Fact]
    public async Task Start_DoesNotTreatAnotherAccountsGmailAsGlobalStartupBlocker()
    {
        var port = ReserveLoopbackPort();
        var tailscale = new HttpsCertificateEnablementRequiredTailscaleService();
        var store = new EphemeralRemoteSecurityStore();
        store.RegisterAccount("different@gmail.com", "account1", "12345678");
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            tailscale,
            store);

        var state = await coordinator.StartAsync(CreateSettings(port));

        Assert.False(state.IsRunning);
        Assert.Contains("HTTPS", state.Error, StringComparison.Ordinal);
        Assert.Equal(1, tailscale.StatusCallCount);
        Assert.Equal(0, tailscale.EnableCallCount);
        Assert.False(await IsPortReachableAsync(port));
    }

    [Fact]
    public async Task Start_WhenTailnetHttpsCertificatesAreDisabled_FailsBeforeBindingListener()
    {
        var port = ReserveLoopbackPort();
        var tailscale = new HttpsCertificateEnablementRequiredTailscaleService();
        await using var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), tailscale);

        var state = await coordinator.StartAsync(CreateSettings(port));

        Assert.False(state.IsRunning);
        Assert.True(state.IsTailscaleConnected);
        Assert.True(state.RequiresTailscaleHttpsCertificateEnablement);
        Assert.False(state.AutoRetryRecommended);
        Assert.Contains("HTTPS", state.Error, StringComparison.Ordinal);
        Assert.Equal(1, tailscale.StatusCallCount);
        Assert.Equal(0, tailscale.EnableCallCount);
        Assert.False(await IsPortReachableAsync(port));
    }

    [Fact]
    public async Task Start_WhenTailscaleBackendIsTemporarilyUnavailable_RecommendsAutoRetry()
    {
        var port = ReserveLoopbackPort();
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            new BackendUnavailableTailscaleService());

        var state = await coordinator.StartAsync(CreateSettings(port));

        Assert.False(state.IsRunning);
        Assert.True(state.IsTailscaleInstalled);
        Assert.False(state.IsTailscaleConnected);
        Assert.True(state.AutoRetryRecommended);
        Assert.False(state.RequiresTailscaleHttpsCertificateEnablement);
        Assert.False(await IsPortReachableAsync(port));
    }

    [Fact]
    public async Task Start_BindsLoopbackListenerBeforeServeIsEnabled()
    {
        var port = ReserveLoopbackPort();
        var tailscale = new ProbingTailscaleServeService();
        await using var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), tailscale);

        var state = await coordinator.StartAsync(CreateSettings(port));

        Assert.True(state.IsRunning);
        Assert.Equal(ProbingTailscaleServeService.PublicUrl, state.PublicUrl);
        Assert.Collection(
            tailscale.Events,
            entry => AssertProbe(entry, "status", port, listenerReachable: false),
            entry => AssertProbe(entry, "status", port, listenerReachable: true),
            entry => AssertProbe(entry, "enable", port, listenerReachable: true));
        Assert.True(coordinator.IsRemoteHostActive);
    }

    [Fact]
    public async Task Stop_KeepsListenerBoundUntilServeRemovalIsConfirmed()
    {
        var port = ReserveLoopbackPort();
        var tailscale = new ProbingTailscaleServeService();
        await using var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), tailscale);
        Assert.True((await coordinator.StartAsync(CreateSettings(port))).IsRunning);
        tailscale.Events.Clear();
        tailscale.HostUnavailableProbe = () => HostIsUnavailable(coordinator);

        var state = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(state.IsRunning);
        Assert.Null(state.Error);
        var disable = Assert.Single(tailscale.Events);
        AssertProbe(disable, "disable", port, listenerReachable: true);
        Assert.True(disable.HostUnavailable);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: false));
        Assert.False(coordinator.IsRemoteHostActive);
    }

    [Theory]
    [InlineData(DisableOutcome.Failure)]
    [InlineData(DisableOutcome.Conflict)]
    public async Task Stop_WhenServeRemovalIsNotConfirmed_RetainsGuardListenerAndAllowsSafeRetry(
        DisableOutcome firstOutcome)
    {
        var port = ReserveLoopbackPort();
        var tailscale = new ProbingTailscaleServeService();
        await using var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), tailscale);
        Assert.True((await coordinator.StartAsync(CreateSettings(port))).IsRunning);
        tailscale.Events.Clear();
        tailscale.DisableOutcomes.Enqueue(firstOutcome);
        tailscale.DisableOutcomes.Enqueue(DisableOutcome.Success);
        tailscale.HostUnavailableProbe = () => HostIsUnavailable(coordinator);

        var failedStop = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(failedStop.IsRunning);
        Assert.NotNull(failedStop.Error);
        var failedDisable = Assert.Single(tailscale.Events);
        AssertProbe(failedDisable, "disable", port, listenerReachable: true);
        Assert.True(failedDisable.HostUnavailable);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: true));
        Assert.False(coordinator.IsRemoteHostActive);
        using (var client = CreateQuickTunnelClient(
                   port,
                   ProbingTailscaleServeService.PublicUrl))
        using (var response = await client.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        tailscale.Events.Clear();
        var successfulRetry = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(successfulRetry.IsRunning);
        Assert.Null(successfulRetry.Error);
        var retryDisable = Assert.Single(tailscale.Events);
        AssertProbe(retryDisable, "disable", port, listenerReachable: true);
        Assert.True(retryDisable.HostUnavailable);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: false));
    }

    [Fact]
    public async Task Reconfigure_RemovesOldRouteBeforeReleasingOldListenerAndBindsNewListenerBeforeEnable()
    {
        var oldPort = ReserveLoopbackPort();
        var newPort = ReserveLoopbackPort();
        while (newPort == oldPort)
        {
            newPort = ReserveLoopbackPort();
        }

        var tailscale = new ProbingTailscaleServeService();
        await using var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), tailscale);
        Assert.True((await coordinator.StartAsync(CreateSettings(oldPort))).IsRunning);
        tailscale.Events.Clear();
        tailscale.HostUnavailableProbe = () => HostIsUnavailable(coordinator);

        var state = await coordinator.StartAsync(CreateSettings(newPort));

        Assert.True(state.IsRunning);
        Assert.Collection(
            tailscale.Events,
            entry =>
            {
                AssertProbe(entry, "disable", oldPort, listenerReachable: true);
                Assert.True(entry.HostUnavailable);
            },
            entry => AssertProbe(entry, "status", newPort, listenerReachable: false),
            entry => AssertProbe(entry, "status", newPort, listenerReachable: true),
            entry => AssertProbe(entry, "enable", newPort, listenerReachable: true));
        Assert.True(await WaitForPortStateAsync(oldPort, expectedReachable: false));
        Assert.True(await WaitForPortStateAsync(newPort, expectedReachable: true));
        Assert.True(coordinator.IsRemoteHostActive);
    }

    [Fact]
    public async Task Funnel_StartsOnlyOwnedPublicRouteAndAcceptsLocalAccountLogin()
    {
        var port = ReserveLoopbackPort();
        var privateServe = new ProbingTailscaleServeService();
        var funnelUrl = new Uri("https://manager-node.example.ts.net/");
        var funnel = new ProbingTailscaleServeService(funnelUrl);
        var store = new EphemeralRemoteSecurityStore();
        store.RegisterAccount(
            null,
            "account1",
            "12345678",
            RemoteWebPermission.All);
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            privateServe,
            store,
            funnel: funnel);

        var started = await coordinator.StartAsync(CreateFunnelSettings(port));

        Assert.True(started.IsRunning, started.Error);
        Assert.Equal(RemoteAccessMode.TailscaleFunnel, started.AccessMode);
        Assert.Equal(funnelUrl, started.PublicUrl);
        Assert.Empty(privateServe.Events);
        Assert.Collection(
            funnel.Events,
            entry => AssertProbe(entry, "status", port, listenerReachable: false),
            entry => AssertProbe(entry, "status", port, listenerReachable: true),
            entry => AssertProbe(entry, "enable", port, listenerReachable: true));

        using (var client = CreateQuickTunnelClient(port, funnelUrl))
        {
            Assert.False(string.IsNullOrWhiteSpace(
                await LoginQuickTunnelAsync(client, funnelUrl)));
        }

        funnel.Events.Clear();
        funnel.HostUnavailableProbe = () => HostIsUnavailable(coordinator);
        var stopped = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(stopped.IsRunning);
        Assert.Null(stopped.Error);
        Assert.False(stopped.AutoRetryRecommended);
        Assert.Equal(RemoteAccessMode.TailscaleFunnel, stopped.AccessMode);
        Assert.Empty(privateServe.Events);
        var disable = Assert.Single(funnel.Events);
        AssertProbe(disable, "disable", port, listenerReachable: true);
        Assert.True(disable.HostUnavailable);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: false));
    }

    [Fact]
    public async Task Funnel_TransientChildExitImmediatelyGuardsListenerAndCanRecover()
    {
        var port = ReserveLoopbackPort();
        var funnelUrl = new Uri("https://manager-node.example.ts.net/");
        var privateServe = new ProbingTailscaleServeService();
        var funnel = new ProbingTailscaleServeService(funnelUrl);
        var store = new EphemeralRemoteSecurityStore();
        store.RegisterAccount(null, "account1", "12345678", RemoteWebPermission.All);
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            privateServe,
            store,
            funnel: funnel);
        var settings = CreateFunnelSettings(port);
        Assert.True((await coordinator.StartAsync(settings)).IsRunning);

        funnel.ExitUnexpectedly(autoRetryRecommended: true);

        var faulted = coordinator.State;
        Assert.False(faulted.IsRunning);
        Assert.True(faulted.AutoRetryRecommended);
        Assert.Equal(RemoteAccessMode.TailscaleFunnel, faulted.AccessMode);
        Assert.Null(faulted.PublicUrl);
        Assert.False(coordinator.IsRemoteHostActive);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: true));
        using (var client = CreateQuickTunnelClient(port, funnelUrl))
        using (var response = await client.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        funnel.DisableOutcomes.Enqueue(DisableOutcome.BackendUnavailable);
        var stillOffline = await coordinator.StartAsync(settings);

        Assert.False(stillOffline.IsRunning);
        Assert.True(stillOffline.AutoRetryRecommended);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: true));

        var recovered = await coordinator.StartAsync(settings);

        Assert.True(recovered.IsRunning, recovered.Error);
        Assert.False(recovered.AutoRetryRecommended);
        Assert.Equal(funnelUrl, recovered.PublicUrl);
        Assert.True(coordinator.IsRemoteHostActive);
    }

    [Fact]
    public async Task Funnel_IgnoresPrivateServeExitAndNonTransientExitDoesNotAutoRetry()
    {
        var port = ReserveLoopbackPort();
        var funnelUrl = new Uri("https://manager-node.example.ts.net/");
        var privateServe = new ProbingTailscaleServeService();
        var funnel = new ProbingTailscaleServeService(funnelUrl);
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            privateServe,
            funnel: funnel);
        Assert.True((await coordinator.StartAsync(CreateFunnelSettings(port))).IsRunning);

        privateServe.ExitUnexpectedly(autoRetryRecommended: true);

        Assert.True(coordinator.State.IsRunning);
        Assert.True(coordinator.IsRemoteHostActive);

        funnel.ExitUnexpectedly(
            autoRetryRecommended: false,
            error: "foreground process was terminated by an administrator");

        Assert.False(coordinator.State.IsRunning);
        Assert.False(coordinator.State.AutoRetryRecommended);
        Assert.False(coordinator.IsRemoteHostActive);
    }

    [Fact]
    public async Task Funnel_StopFailureRetainsDenyAllGuardUntilRouteRemovalCanBeRetried()
    {
        var port = ReserveLoopbackPort();
        var funnelUrl = new Uri("https://manager-node.example.ts.net/");
        var funnel = new ProbingTailscaleServeService(funnelUrl);
        var store = new EphemeralRemoteSecurityStore();
        store.RegisterAccount(null, "account1", "12345678", RemoteWebPermission.All);
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            securityStore: store,
            funnel: funnel);
        Assert.True((await coordinator.StartAsync(CreateFunnelSettings(port))).IsRunning);
        funnel.Events.Clear();
        funnel.DisableOutcomes.Enqueue(DisableOutcome.Failure);

        var failedStop = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(failedStop.IsRunning);
        Assert.NotNull(failedStop.Error);
        Assert.False(coordinator.IsRemoteHostActive);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: true));
        using (var client = CreateQuickTunnelClient(port, funnelUrl))
        using (var response = await client.GetAsync("api/v1/auth/status"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        funnel.Events.Clear();
        var successfulRetry = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.Null(successfulRetry.Error);
        Assert.False(successfulRetry.IsRunning);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: false));
    }

    [Fact]
    public async Task Funnel_FirstUseApprovalFailureClosesListenerAndDoesNotAutoRetry()
    {
        var port = ReserveLoopbackPort();
        var privateServe = new ProbingTailscaleServeService();
        var funnel = new ProbingTailscaleServeService(
            new Uri("https://manager-node.example.ts.net/"))
        {
            EnableError = "Funnel 尚未獲 Tailnet 管理員核准。"
        };
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            privateServe,
            funnel: funnel);

        var state = await coordinator.StartAsync(CreateFunnelSettings(port));

        Assert.False(state.IsRunning);
        Assert.Equal(RemoteAccessMode.TailscaleFunnel, state.AccessMode);
        Assert.False(state.AutoRetryRecommended);
        Assert.Contains("核准", state.Error, StringComparison.Ordinal);
        Assert.Empty(privateServe.Events);
        Assert.Collection(
            funnel.Events,
            entry => AssertProbe(entry, "status", port, listenerReachable: false),
            entry => AssertProbe(entry, "status", port, listenerReachable: true),
            entry => AssertProbe(entry, "enable", port, listenerReachable: true),
            entry => AssertProbe(entry, "disable", port, listenerReachable: true));
        Assert.False(coordinator.IsRemoteHostActive);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: false));
    }

    [Fact]
    public async Task Funnel_WebConsoleUsesRuntimeStateInsteadOfRetainedCloudflareSnapshot()
    {
        var port = ReserveLoopbackPort();
        var quickTunnel = new FakeWebTunnelService();
        var funnelUrl = new Uri("https://manager-node.example.ts.net/");
        var funnel = new ProbingTailscaleServeService(funnelUrl);
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ => quickTunnel,
            funnel: funnel);
        Assert.True((await coordinator.StartAsync(
            CreateQuickTunnelSettings(port))).IsRunning);
        Assert.True((await coordinator.StartAsync(
            CreateFunnelSettings(port))).IsRunning);
        Assert.Equal(
            WebTunnelLifecycleState.Stopped,
            coordinator.WebTunnelSnapshot?.State);

        using var console = new RemoteWebConsoleViewModel(
            coordinator,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            Dispatcher.CurrentDispatcher);

        Assert.True(console.IsRunning);
        Assert.Equal("已連線", console.StateText);
        Assert.Equal(funnelUrl.AbsoluteUri, console.PublicUrl);
        Assert.Empty(console.Logs);
        Assert.Equal("—", console.ProcessIdText);
    }

    [Fact]
    public async Task QuickTunnel_StopPreservesFinalConsoleSnapshotAndClearsOwnedResources()
    {
        var port = ReserveLoopbackPort();
        var tunnel = new FakeWebTunnelService();
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ => tunnel);

        var started = await coordinator.StartAsync(CreateQuickTunnelSettings(port));

        Assert.True(started.IsRunning);
        Assert.Equal(RemoteAccessMode.CloudflareQuickTunnel, started.AccessMode);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: true));
        Assert.Equal(WebTunnelLifecycleState.Running, coordinator.WebTunnelSnapshot?.State);

        var stopped = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(stopped.IsRunning);
        Assert.Null(stopped.Error);
        Assert.Equal(RemoteAccessMode.CloudflareQuickTunnel, stopped.AccessMode);
        Assert.Equal(1, tunnel.StopCount);
        Assert.Equal(1, tunnel.DisposeCount);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: false));
        var retained = Assert.IsType<WebTunnelSnapshot>(coordinator.WebTunnelSnapshot);
        Assert.Equal(WebTunnelLifecycleState.Stopped, retained.State);
        Assert.Contains(retained.RecentLogs, entry => entry.Message == "fake-tunnel-log");

        var repeated = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.Null(repeated.Error);
        Assert.Equal(RemoteAccessMode.CloudflareQuickTunnel, repeated.AccessMode);
        Assert.Equal(1, tunnel.StopCount);
        Assert.Equal(1, tunnel.DisposeCount);
        Assert.Equal(WebTunnelLifecycleState.Stopped, coordinator.WebTunnelSnapshot?.State);
    }

    [Fact]
    public async Task QuickTunnel_FailedStopAndDisposeRetainsOwnershipForSafeRetry()
    {
        var port = ReserveLoopbackPort();
        var tunnel = new FakeWebTunnelService
        {
            StopFailuresRemaining = 1,
            DisposeFailuresRemaining = 1
        };
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ => tunnel);
        Assert.True((await coordinator.StartAsync(CreateQuickTunnelSettings(port))).IsRunning);

        var failed = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(failed.IsRunning);
        Assert.NotNull(failed.Error);
        Assert.Equal(RemoteAccessMode.CloudflareQuickTunnel, failed.AccessMode);
        Assert.Equal(1, tunnel.StopCount);
        Assert.Equal(1, tunnel.DisposeCount);
        Assert.Equal(WebTunnelLifecycleState.Running, coordinator.WebTunnelSnapshot?.State);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: true));

        var retried = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(retried.IsRunning);
        Assert.Null(retried.Error);
        Assert.Equal(RemoteAccessMode.CloudflareQuickTunnel, retried.AccessMode);
        Assert.Equal(2, tunnel.StopCount);
        Assert.Equal(2, tunnel.DisposeCount);
        Assert.Equal(WebTunnelLifecycleState.Stopped, coordinator.WebTunnelSnapshot?.State);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: false));
    }

    [Theory]
    [InlineData((int)WebTunnelLifecycleState.Faulted, false)]
    [InlineData((int)WebTunnelLifecycleState.Running, true)]
    public async Task QuickTunnel_InitialTransientFailure_RecommendsAutoRetry(
        int startStateValue,
        bool omitPublicUrl)
    {
        var startState = (WebTunnelLifecycleState)startStateValue;
        var port = ReserveLoopbackPort();
        var tunnel = new FakeWebTunnelService
        {
            StartResultState = startState,
            OmitStartPublicUrl = omitPublicUrl,
            StartError = "temporary Cloudflare network failure"
        };
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ => tunnel);

        var state = await coordinator.StartAsync(CreateQuickTunnelSettings(port));

        Assert.False(state.IsRunning);
        Assert.Null(state.PublicUrl);
        Assert.True(state.AutoRetryRecommended);
        Assert.Equal(RemoteAccessMode.CloudflareQuickTunnel, state.AccessMode);
        Assert.Contains("temporary", state.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, tunnel.StopCount);
        Assert.Equal(1, tunnel.DisposeCount);
        Assert.False(await IsPortReachableAsync(port));
    }

    [Fact]
    public async Task QuickTunnel_MissingPathAndFactoryConfigurationFailure_DoNotRetry()
    {
        var port = ReserveLoopbackPort();
        var factoryCalls = 0;
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException("invalid cloudflared configuration");
            });
        var missingPath = CreateQuickTunnelSettings(port);
        missingPath.CloudflaredExecutablePath = string.Empty;
        var invalidConfiguration = CreateQuickTunnelSettings(port);
        invalidConfiguration.LocalPort = 80;

        var missing = await coordinator.StartAsync(missingPath);
        var invalid = await coordinator.StartAsync(CreateQuickTunnelSettings(port));
        var invalidPort = await coordinator.StartAsync(invalidConfiguration);

        Assert.False(missing.AutoRetryRecommended);
        Assert.False(invalid.AutoRetryRecommended);
        Assert.False(invalidPort.AutoRetryRecommended);
        Assert.Equal(1, factoryCalls);
        Assert.Contains("invalid", invalid.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1024", invalidPort.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickTunnel_RuntimeFault_ClearsPublishedAccessAndRevokesSessionsButKeepsGuardHost()
    {
        var port = ReserveLoopbackPort();
        var store = new EphemeralRemoteSecurityStore();
        store.RegisterAccount(null, "account1", "12345678", RemoteWebPermission.All);
        var tunnel = new FakeWebTunnelService();
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            securityStore: store,
            quickTunnelFactory: _ => tunnel);
        var started = await coordinator.StartAsync(CreateQuickTunnelSettings(port));
        Assert.True(started.IsRunning);
        using var client = CreateQuickTunnelClient(port, started.PublicUrl!);
        var sessionCookie = await LoginQuickTunnelAsync(client, started.PublicUrl!);

        tunnel.RaiseFaulted("simulated network loss");

        var faulted = coordinator.State;
        Assert.False(faulted.IsRunning);
        Assert.Null(faulted.PublicUrl);
        Assert.True(faulted.AutoRetryRecommended);
        Assert.Equal(RemoteAccessMode.CloudflareQuickTunnel, faulted.AccessMode);
        Assert.Contains("network loss", faulted.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(coordinator.IsRemoteHostActive);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: true));

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/status");
        statusRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        using var statusResponse = await client.SendAsync(statusRequest);
        var afterFault = await statusResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.False(afterFault?.Authenticated);
    }

    [Fact]
    public async Task QuickTunnel_StaleTunnelFault_DoesNotReplaceCurrentRunningState()
    {
        var firstPort = ReserveLoopbackPort();
        var secondPort = ReserveLoopbackPort();
        while (secondPort == firstPort)
        {
            secondPort = ReserveLoopbackPort();
        }

        var stale = new FakeWebTunnelService
        {
            RetainStateObserverAfterDispose = true
        };
        var current = new FakeWebTunnelService
        {
            PublicUrl = new Uri("https://current-cloud-5678.trycloudflare.com/")
        };
        var tunnels = new Queue<IWebTunnelService>([stale, current]);
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ => tunnels.Dequeue());
        Assert.True((await coordinator.StartAsync(CreateQuickTunnelSettings(firstPort))).IsRunning);
        var currentState = await coordinator.StartAsync(CreateQuickTunnelSettings(secondPort));
        Assert.True(currentState.IsRunning);

        stale.RaiseFaulted("late stale event");

        Assert.Same(currentState, coordinator.State);
        Assert.True(coordinator.State.IsRunning);
        Assert.Equal(current.PublicUrl, coordinator.State.PublicUrl);
        Assert.False(coordinator.State.AutoRetryRecommended);
        Assert.True(coordinator.IsRemoteHostActive);
    }

    [Fact]
    public async Task QuickTunnel_ExplicitStop_NeverPublishesRetryRecommendation()
    {
        var port = ReserveLoopbackPort();
        var tunnel = new FakeWebTunnelService();
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ => tunnel);
        Assert.True((await coordinator.StartAsync(CreateQuickTunnelSettings(port))).IsRunning);
        var stopStates = new List<RemoteAccessRuntimeState>();
        coordinator.StateChanged += (_, state) => stopStates.Add(state);

        var stopped = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(stopped.IsRunning);
        Assert.False(stopped.AutoRetryRecommended);
        Assert.NotEmpty(stopStates);
        Assert.All(stopStates, state => Assert.False(state.AutoRetryRecommended));
    }

    [Fact]
    public async Task QuickTunnel_FaultDuringFinalCommit_NeverPublishesRunningAndRetries()
    {
        var port = ReserveLoopbackPort();
        var tunnel = new FakeWebTunnelService
        {
            FaultOnNextSnapshotRead = true
        };
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ => tunnel);
        var observed = new List<RemoteAccessRuntimeState>();
        coordinator.StateChanged += (_, state) => observed.Add(state);

        var result = await coordinator.StartAsync(CreateQuickTunnelSettings(port));

        Assert.False(result.IsRunning);
        Assert.True(result.AutoRetryRecommended);
        Assert.DoesNotContain(observed, state => state.IsRunning);
        Assert.False(coordinator.IsRemoteHostActive);
        Assert.False(await IsPortReachableAsync(port));
        Assert.Equal(1, tunnel.StopCount);
        Assert.Equal(1, tunnel.DisposeCount);
    }

    [Fact]
    public async Task StateObserverException_DoesNotBreakQuickTunnelLifecycleOrLaterObservers()
    {
        var port = ReserveLoopbackPort();
        var tunnel = new FakeWebTunnelService();
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            quickTunnelFactory: _ => tunnel);
        var laterObserverCalls = 0;
        coordinator.StateChanged += (_, _) => throw new InvalidOperationException("observer failed");
        coordinator.StateChanged += (_, _) => laterObserverCalls++;

        var started = await coordinator.StartAsync(CreateQuickTunnelSettings(port));
        tunnel.RaiseFaulted("runtime failure after observer error");

        Assert.True(started.IsRunning);
        Assert.True(laterObserverCalls >= 3);
        Assert.False(coordinator.State.IsRunning);
        Assert.True(coordinator.State.AutoRetryRecommended);
        Assert.False(coordinator.IsRemoteHostActive);
    }

    [Fact]
    public async Task PermissionChange_ImmediatelyRevokesExistingQuickTunnelSession()
    {
        var port = ReserveLoopbackPort();
        var store = new EphemeralRemoteSecurityStore();
        store.RegisterAccount(null, "account1", "12345678", RemoteWebPermission.All);
        var tunnel = new FakeWebTunnelService();
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            securityStore: store,
            quickTunnelFactory: _ => tunnel);
        var settings = CreateQuickTunnelSettings(port);
        settings.AllowedLogin = string.Empty;
        var started = await coordinator.StartAsync(settings);
        Assert.True(started.IsRunning);

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Host = started.PublicUrl!.Authority;
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using var bootstrapResponse = await client.GetAsync("api/v1/auth/status");
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(bootstrap?.CsrfToken);
        var bootstrapCookie = GetCookie(bootstrapResponse, "__Host-MCSV-Auth-CSRF");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        loginRequest.Headers.TryAddWithoutValidation("Origin", started.PublicUrl.GetLeftPart(UriPartial.Authority));
        loginRequest.Headers.TryAddWithoutValidation(RemoteControlOptions.CsrfHeaderName, bootstrap.CsrfToken);
        loginRequest.Headers.TryAddWithoutValidation("Cookie", bootstrapCookie);
        loginRequest.Content = JsonContent.Create(new RemoteCredentialLoginRequestDto("account1", "12345678"));
        using var loginResponse = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var sessionCookie = GetCookie(loginResponse, RemoteControlOptions.DefaultSessionCookieName);

        coordinator.UpdateApprovedAccountPermissions(RemoteWebPermission.StartServer);

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/status");
        statusRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        using var statusResponse = await client.SendAsync(statusRequest);
        var afterChange = await statusResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.False(afterChange?.Authenticated);
        Assert.Equal(RemoteWebPermission.StartServer, store.ApprovedAccount?.Permissions);
    }

    [Fact]
    public async Task NamedTunnel_UsesStoredTokenAndPublishesConfiguredFixedOrigin()
    {
        var port = ReserveLoopbackPort();
        var token = "eyJ" + new string('N', 96) + "=";
        var store = new EphemeralRemoteSecurityStore();
        store.SaveCloudflareNamedTunnelToken(token);
        store.SaveCloudflaredInstallationReceipt(CreateTestCloudflaredReceipt());
        var tunnel = new FakeWebTunnelService
        {
            PublicUrl = new Uri("https://mc.example.com/")
        };
        string? capturedPath = null;
        Uri? capturedOrigin = null;
        string? capturedToken = null;
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            securityStore: store,
            namedTunnelFactory: (path, origin, suppliedToken) =>
            {
                capturedPath = path;
                capturedOrigin = origin;
                capturedToken = suppliedToken;
                return tunnel;
            },
            namedTunnelExecutableVerifier: TrustTestNamedTunnelExecutableAsync);

        var started = await coordinator.StartAsync(CreateNamedTunnelSettings(port));

        Assert.True(started.IsRunning);
        Assert.Equal(RemoteAccessMode.CloudflareNamedTunnel, started.AccessMode);
        Assert.Equal(new Uri("https://mc.example.com/"), started.PublicUrl);
        Assert.Equal(@"C:\Tools\cloudflared.exe", capturedPath);
        Assert.Equal(new Uri("https://mc.example.com/"), capturedOrigin);
        Assert.Equal(token, capturedToken);
        Assert.True(coordinator.IsRemoteHostActive);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: true));

        var stopped = await coordinator.StopAsync(disableOwnedServe: true);

        Assert.False(stopped.IsRunning);
        Assert.Equal(RemoteAccessMode.CloudflareNamedTunnel, stopped.AccessMode);
        Assert.Equal(1, tunnel.StopCount);
        Assert.Equal(1, tunnel.DisposeCount);
        Assert.True(await WaitForPortStateAsync(port, expectedReachable: false));
    }

    [Fact]
    public async Task NamedTunnel_WithoutStoredTokenFailsBeforeCreatingConnectorAndDoesNotRetry()
    {
        var factoryCalls = 0;
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            securityStore: new EphemeralRemoteSecurityStore(),
            namedTunnelFactory: (_, _, _) =>
            {
                factoryCalls++;
                return new FakeWebTunnelService
                {
                    PublicUrl = new Uri("https://mc.example.com/")
                };
            });

        var state = await coordinator.StartAsync(
            CreateNamedTunnelSettings(ReserveLoopbackPort()));

        Assert.False(state.IsRunning);
        Assert.Equal(RemoteAccessMode.CloudflareNamedTunnel, state.AccessMode);
        Assert.False(state.AutoRetryRecommended);
        Assert.Null(state.PublicUrl);
        Assert.Contains("Token", state.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factoryCalls);
        Assert.False(coordinator.IsRemoteHostActive);
    }

    [Fact]
    public async Task NamedTunnel_WithoutManagedInstallationReceiptFailsBeforeCreatingConnector()
    {
        var token = "eyJ" + new string('R', 96) + "=";
        var store = new EphemeralRemoteSecurityStore();
        store.SaveCloudflareNamedTunnelToken(token);
        var factoryCalls = 0;
        var verifierCalls = 0;
        await using var coordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            securityStore: store,
            namedTunnelFactory: (_, _, _) =>
            {
                factoryCalls++;
                return new FakeWebTunnelService
                {
                    PublicUrl = new Uri("https://mc.example.com/")
                };
            },
            namedTunnelExecutableVerifier: (_, path, _, _) =>
            {
                verifierCalls++;
                return Task.FromResult(
                    new CloudflaredExecutableVerificationLease(path));
            });

        var state = await coordinator.StartAsync(
            CreateNamedTunnelSettings(ReserveLoopbackPort()));

        Assert.False(state.IsRunning);
        Assert.False(state.AutoRetryRecommended);
        Assert.Contains("安裝收據", state.Error, StringComparison.Ordinal);
        Assert.Equal(0, verifierCalls);
        Assert.Equal(0, factoryCalls);
        Assert.False(coordinator.IsRemoteHostActive);
    }

    [Fact]
    public async Task NamedTunnel_UnexpectedOriginFailsClosedAndRuntimeFaultRequestsReconnect()
    {
        var token = "eyJ" + new string('F', 96) + "=";
        var mismatchPort = ReserveLoopbackPort();
        var mismatchStore = new EphemeralRemoteSecurityStore();
        mismatchStore.SaveCloudflareNamedTunnelToken(token);
        mismatchStore.SaveCloudflaredInstallationReceipt(CreateTestCloudflaredReceipt());
        var mismatchTunnel = new FakeWebTunnelService
        {
            PublicUrl = new Uri("https://other.example.com/")
        };
        await using (var mismatchCoordinator = new RemoteAccessCoordinator(
                         new StubRemoteBackend(),
                         securityStore: mismatchStore,
                         namedTunnelFactory: (_, _, _) => mismatchTunnel,
                         namedTunnelExecutableVerifier: TrustTestNamedTunnelExecutableAsync))
        {
            var rejected = await mismatchCoordinator.StartAsync(
                CreateNamedTunnelSettings(mismatchPort));

            Assert.False(rejected.IsRunning);
            Assert.False(rejected.AutoRetryRecommended);
            Assert.Contains("不一致", rejected.Error, StringComparison.Ordinal);
            Assert.Equal(1, mismatchTunnel.StopCount);
            Assert.Equal(1, mismatchTunnel.DisposeCount);
            Assert.False(mismatchCoordinator.IsRemoteHostActive);
            Assert.False(await IsPortReachableAsync(mismatchPort));
        }

        var faultPort = ReserveLoopbackPort();
        var faultStore = new EphemeralRemoteSecurityStore();
        faultStore.SaveCloudflareNamedTunnelToken(token);
        faultStore.SaveCloudflaredInstallationReceipt(CreateTestCloudflaredReceipt());
        var faultTunnel = new FakeWebTunnelService
        {
            PublicUrl = new Uri("https://mc.example.com/")
        };
        await using var faultCoordinator = new RemoteAccessCoordinator(
            new StubRemoteBackend(),
            securityStore: faultStore,
            namedTunnelFactory: (_, _, _) => faultTunnel,
            namedTunnelExecutableVerifier: TrustTestNamedTunnelExecutableAsync);
        Assert.True((await faultCoordinator.StartAsync(
            CreateNamedTunnelSettings(faultPort))).IsRunning);

        faultTunnel.RaiseFaulted("temporary network change");

        var faulted = faultCoordinator.State;
        Assert.False(faulted.IsRunning);
        Assert.Null(faulted.PublicUrl);
        Assert.True(faulted.AutoRetryRecommended);
        Assert.Equal(RemoteAccessMode.CloudflareNamedTunnel, faulted.AccessMode);
        Assert.Contains("temporary network change", faulted.Error, StringComparison.Ordinal);
    }

    private static RemoteControlSettings CreateSettings(int port) => new()
    {
        Enabled = true,
        AllowedLogin = "owner@gmail.com",
        LocalPort = port
    };

    private static RemoteControlSettings CreateQuickTunnelSettings(int port) => new()
    {
        Enabled = true,
        AllowedLogin = "owner@gmail.com",
        LocalPort = port,
        AccessMode = RemoteAccessMode.CloudflareQuickTunnel,
        CloudflaredExecutablePath = @"C:\Tools\cloudflared.exe"
    };

    private static RemoteControlSettings CreateFunnelSettings(int port) => new()
    {
        Enabled = true,
        AllowedLogin = string.Empty,
        LocalPort = port,
        AccessMode = RemoteAccessMode.TailscaleFunnel
    };

    private static RemoteControlSettings CreateNamedTunnelSettings(int port) => new()
    {
        Enabled = true,
        LocalPort = port,
        AccessMode = RemoteAccessMode.CloudflareNamedTunnel,
        CloudflaredExecutablePath = @"C:\Tools\cloudflared.exe",
        CloudflareNamedPublicOrigin = " https://mc.example.com "
    };

    private static CloudflaredInstallationReceipt CreateTestCloudflaredReceipt()
        => CloudflaredInstallationReceipt.Create(
            new CloudflaredBootstrapResult(
                @"C:\Tools\cloudflared.exe",
                "2026.8.1",
                123,
                new string('a', 64)),
            DateTimeOffset.UtcNow);

    private static Task<CloudflaredExecutableVerificationLease> TrustTestNamedTunnelExecutableAsync(
        string applicationRoot,
        string executablePath,
        CloudflaredInstallationReceipt receipt,
        CancellationToken cancellationToken)
    {
        _ = applicationRoot;
        CloudflaredInstallationReceipt.ValidateAndThrow(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new CloudflaredExecutableVerificationLease(executablePath));
    }

    private static void AssertProbe(
        TailscaleProbeEvent entry,
        string operation,
        int port,
        bool listenerReachable)
    {
        Assert.Equal(operation, entry.Operation);
        Assert.Equal(port, entry.Port);
        Assert.Equal(listenerReachable, entry.ListenerReachable);
    }

    private static string GetCookie(HttpResponseMessage response, string cookieName)
    {
        var value = response.Headers.GetValues("Set-Cookie")
            .Single(header => header.StartsWith(cookieName + "=", StringComparison.Ordinal));
        return value.Split(';', 2)[0];
    }

    private static HttpClient CreateQuickTunnelClient(int port, Uri publicUrl)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Host = publicUrl.Authority;
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        return client;
    }

    private static async Task<string> LoginQuickTunnelAsync(HttpClient client, Uri publicUrl)
    {
        using var bootstrapResponse = await client.GetAsync("api/v1/auth/status");
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<RemoteAuthStatusDto>();
        Assert.NotNull(bootstrap?.CsrfToken);
        var bootstrapCookie = GetCookie(bootstrapResponse, "__Host-MCSV-Auth-CSRF");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        loginRequest.Headers.TryAddWithoutValidation(
            "Origin",
            publicUrl.GetLeftPart(UriPartial.Authority));
        loginRequest.Headers.TryAddWithoutValidation(
            RemoteControlOptions.CsrfHeaderName,
            bootstrap.CsrfToken);
        loginRequest.Headers.TryAddWithoutValidation("Cookie", bootstrapCookie);
        loginRequest.Content = JsonContent.Create(
            new RemoteCredentialLoginRequestDto("account1", "12345678"));
        using var loginResponse = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        return GetCookie(loginResponse, RemoteControlOptions.DefaultSessionCookieName);
    }

    private static bool HostIsUnavailable(RemoteAccessCoordinator coordinator)
        => !coordinator.IsRemoteHostActive;

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> WaitForPortStateAsync(int port, bool expectedReachable)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        do
        {
            if (await IsPortReachableAsync(port) == expectedReachable)
            {
                return true;
            }

            await Task.Delay(20);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static async Task<bool> IsPortReachableAsync(int port)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port)
                .WaitAsync(TimeSpan.FromSeconds(1));
            return client.Connected;
        }
        catch (Exception exception) when (
            exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    public enum DisableOutcome
    {
        Success,
        Failure,
        Conflict,
        BackendUnavailable
    }

    private sealed record TailscaleProbeEvent(
        string Operation,
        int Port,
        bool ListenerReachable,
        bool? HostUnavailable = null);

    private sealed class ProbingTailscaleServeService : ITailscaleServeService
    {
        public static Uri PublicUrl { get; } = new("https://manager-node.example.ts.net:8443/");

        private readonly Uri _publicUrl;
        private bool _enabled;

        public ProbingTailscaleServeService(Uri? publicUrl = null)
        {
            _publicUrl = publicUrl ?? PublicUrl;
        }

        public List<TailscaleProbeEvent> Events { get; } = [];

        public Queue<DisableOutcome> DisableOutcomes { get; } = new();

        public Func<bool>? HostUnavailableProbe { get; set; }

        public event EventHandler<TailscaleRouteProcessExitedEventArgs>? ForegroundProcessExited;

        public string? EnableError { get; init; }

        public void ExitUnexpectedly(
            bool autoRetryRecommended,
            string error = "Tailscale backend network connection was lost.")
        {
            _enabled = false;
            ForegroundProcessExited?.Invoke(
                this,
                new TailscaleRouteProcessExitedEventArgs(
                    4242,
                    1,
                    error,
                    autoRetryRecommended));
        }

        public async Task<TailscaleServeStatus> GetStatusAsync(
            int localPort,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(new TailscaleProbeEvent(
                "status",
                localPort,
                await IsPortReachableAsync(localPort)));
            return _enabled ? EnabledStatus() : CleanStatus();
        }

        public async Task<TailscaleServeOperationResult> EnableAsync(
            int localPort,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(new TailscaleProbeEvent(
                "enable",
                localPort,
                await IsPortReachableAsync(localPort)));
            if (EnableError is not null)
            {
                return new TailscaleServeOperationResult(
                    false,
                    false,
                    CleanStatus(),
                    EnableError);
            }

            _enabled = true;
            return new TailscaleServeOperationResult(
                true,
                true,
                EnabledStatus(),
                null);
        }

        public async Task<TailscaleServeOperationResult> DisableAsync(
            int localPort,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(new TailscaleProbeEvent(
                "disable",
                localPort,
                await IsPortReachableAsync(localPort),
                HostUnavailableProbe?.Invoke()));

            var outcome = DisableOutcomes.TryDequeue(out var configured)
                ? configured
                : DisableOutcome.Success;
            switch (outcome)
            {
                case DisableOutcome.Success:
                    _enabled = false;
                    return new TailscaleServeOperationResult(
                        true,
                        true,
                        CleanStatus(),
                        null);
                case DisableOutcome.Failure:
                    return new TailscaleServeOperationResult(
                        false,
                        false,
                        EnabledStatus(),
                        "Serve removal failed.");
                case DisableOutcome.Conflict:
                    return new TailscaleServeOperationResult(
                        true,
                        false,
                        ConflictStatus(),
                        null);
                case DisableOutcome.BackendUnavailable:
                    return new TailscaleServeOperationResult(
                        false,
                        false,
                        BackendUnavailableStatus(),
                        "Tailscale backend is temporarily unavailable.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private TailscaleServeStatus CleanStatus() => new(
            true,
            true,
            false,
            false,
            false,
            @"C:\Program Files\Tailscale\tailscale.exe",
            "Running",
            "manager-node.example.ts.net",
            _publicUrl,
            null,
            null);

        private TailscaleServeStatus EnabledStatus() => new(
            true,
            true,
            true,
            true,
            false,
            @"C:\Program Files\Tailscale\tailscale.exe",
            "Running",
            "manager-node.example.ts.net",
            _publicUrl,
            _publicUrl,
            null);

        private TailscaleServeStatus ConflictStatus() => new(
            true,
            true,
            true,
            false,
            true,
            @"C:\Program Files\Tailscale\tailscale.exe",
            "Running",
            "manager-node.example.ts.net",
            _publicUrl,
            _publicUrl,
            $"HTTPS {_publicUrl.Port} is still configured.");

        private TailscaleServeStatus BackendUnavailableStatus() => new(
            true,
            false,
            false,
            false,
            false,
            @"C:\Program Files\Tailscale\tailscale.exe",
            "Starting",
            "manager-node.example.ts.net",
            null,
            null,
            "Tailscale backend is temporarily unavailable.");
    }

    private sealed class HttpsCertificateEnablementRequiredTailscaleService
        : ITailscaleServeService
    {
        public event EventHandler<TailscaleRouteProcessExitedEventArgs>? ForegroundProcessExited
        {
            add { }
            remove { }
        }

        public int StatusCallCount { get; private set; }
        public int EnableCallCount { get; private set; }

        public Task<TailscaleServeStatus> GetStatusAsync(
            int localPort,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCallCount++;
            return Task.FromResult(new TailscaleServeStatus(
                true,
                true,
                false,
                false,
                false,
                @"C:\Program Files\Tailscale\tailscale.exe",
                "Running",
                "manager-node.example.ts.net",
                new Uri("https://manager-node.example.ts.net:8443/"),
                null,
                "此 Tailnet 尚未啟用 HTTPS 憑證。")
            {
                RequiresHttpsCertificateEnablement = true
            });
        }

        public Task<TailscaleServeOperationResult> EnableAsync(
            int localPort,
            CancellationToken cancellationToken = default)
        {
            EnableCallCount++;
            throw new InvalidOperationException("Preflight should reject before EnableAsync.");
        }

        public Task<TailscaleServeOperationResult> DisableAsync(
            int localPort,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No owned Serve process should exist.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BackendUnavailableTailscaleService : ITailscaleServeService
    {
        public event EventHandler<TailscaleRouteProcessExitedEventArgs>? ForegroundProcessExited
        {
            add { }
            remove { }
        }

        public Task<TailscaleServeStatus> GetStatusAsync(
            int localPort,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TailscaleServeStatus(
                true,
                false,
                false,
                false,
                false,
                @"C:\Program Files\Tailscale\tailscale.exe",
                "Starting",
                null,
                null,
                null,
                "Tailscale backend is still starting."));
        }

        public Task<TailscaleServeOperationResult> EnableAsync(
            int localPort,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A disconnected backend cannot enable Serve.");

        public Task<TailscaleServeOperationResult> DisableAsync(
            int localPort,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No owned Serve process should exist.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWebTunnelService : IWebTunnelService
    {
        private static readonly Uri DefaultPublicUrl = new(
            "https://gentle-cloud-1234.trycloudflare.com/");
        private readonly IReadOnlyList<WebTunnelLogEntry> _logs =
        [
            new(
                DateTimeOffset.UtcNow,
                WebTunnelLogChannel.Service,
                "fake-tunnel-log")
        ];
        private WebTunnelSnapshot _snapshot;
        private EventHandler<WebTunnelSnapshot>? _stateChanged;

        public FakeWebTunnelService()
        {
            _snapshot = CreateSnapshot(WebTunnelLifecycleState.Stopped);
        }

        public event EventHandler<WebTunnelSnapshot>? StateChanged
        {
            add => _stateChanged += value;
            remove
            {
                if (!RetainStateObserverAfterDispose)
                {
                    _stateChanged -= value;
                }
            }
        }

        public WebTunnelSnapshot Snapshot
        {
            get
            {
                if (FaultOnNextSnapshotRead)
                {
                    FaultOnNextSnapshotRead = false;
                    RaiseFaulted("tunnel faulted during final commit");
                }

                return _snapshot;
            }
        }
        public Uri PublicUrl { get; set; } = DefaultPublicUrl;
        public WebTunnelLifecycleState StartResultState { get; set; } =
            WebTunnelLifecycleState.Running;
        public bool OmitStartPublicUrl { get; set; }
        public string? StartError { get; set; }
        public bool FaultOnNextSnapshotRead { get; set; }
        public bool RetainStateObserverAfterDispose { get; set; }
        public int StopFailuresRemaining { get; set; }
        public int DisposeFailuresRemaining { get; set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<WebTunnelSnapshot> StartAsync(
            int localPort,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetSnapshot(CreateSnapshot(WebTunnelLifecycleState.Starting));
            SetSnapshot(CreateSnapshot(
                StartResultState,
                OmitStartPublicUrl || StartResultState != WebTunnelLifecycleState.Running
                    ? null
                    : PublicUrl,
                processId: StartResultState == WebTunnelLifecycleState.Running ? 42420 : null,
                startedAtUtc: StartResultState == WebTunnelLifecycleState.Running
                    ? DateTimeOffset.UtcNow
                    : null,
                error: StartError));
            return Task.FromResult(_snapshot);
        }

        public void RaiseFaulted(string error)
            => SetSnapshot(CreateSnapshot(
                WebTunnelLifecycleState.Faulted,
                error: error));

        public Task<WebTunnelSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            if (StopFailuresRemaining > 0)
            {
                StopFailuresRemaining--;
                throw new InvalidOperationException("simulated tunnel stop failure");
            }

            SetSnapshot(CreateSnapshot(WebTunnelLifecycleState.Stopped));
            return Task.FromResult(_snapshot);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                return ValueTask.FromException(
                    new InvalidOperationException("simulated tunnel dispose failure"));
            }

            SetSnapshot(CreateSnapshot(WebTunnelLifecycleState.Stopped));
            return ValueTask.CompletedTask;
        }

        private WebTunnelSnapshot CreateSnapshot(
            WebTunnelLifecycleState state,
            Uri? publicUrl = null,
            int? processId = null,
            DateTimeOffset? startedAtUtc = null,
            string? error = null)
            => new(
                state,
                publicUrl,
                processId,
                "fake-2026.8",
                startedAtUtc,
                startedAtUtc is null ? null : TimeSpan.Zero,
                error,
                _logs);

        private void SetSnapshot(WebTunnelSnapshot snapshot)
        {
            _snapshot = snapshot;
            _stateChanged?.Invoke(this, snapshot);
        }
    }

    private sealed class StubRemoteBackend : IRemoteControlBackend
    {
        public ValueTask<RemoteDashboardDto> GetDashboardAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteDashboardDto(
                DateTimeOffset.UtcNow,
                Array.Empty<RemoteServerSummaryDto>()));

        public ValueTask<RemoteServerDetailDto?> GetServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemoteServerDetailDto?>(null);

        public ValueTask<RemoteConsolePageDto?> GetConsoleAsync(
            string serverId,
            RemoteConsoleQuery query,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemoteConsolePageDto?>(null);

        public ValueTask<RemotePlayerListDto?> GetPlayersAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemotePlayerListDto?>(null);

        public ValueTask<RemoteOperationResultDto> StartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> StopServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> RestartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> SendConsoleCommandAsync(
            string serverId,
            string command,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> PerformPlayerActionAsync(
            string serverId,
            RemotePlayerActionRequestDto request,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> CreateBackupAsync(
            string serverId,
            CancellationToken cancellationToken)
            => Success();

        private static ValueTask<RemoteOperationResultDto> Success()
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "ok"));
    }
}
