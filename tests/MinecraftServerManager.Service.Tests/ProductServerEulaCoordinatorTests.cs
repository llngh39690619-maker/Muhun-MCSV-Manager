using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerEulaCoordinatorTests
{
    [Fact]
    public async Task ExistingPaperServiceInstance_RequiresConfirmationThenStartsFromAuthoritativeDirectory()
    {
        var fixture = await EulaRuntimeFixture.CreateAsync(autoRestart: false);
        await using var runtime = fixture.Runtime;
        var serviceState = new ProductServiceState(TimeProvider.System);
        serviceState.Initialize(Guid.NewGuid());
        serviceState.MarkReady();
        var processor = new ProductIpcMessageProcessor(serviceState, runtime);

        var rejected = await processor.ProcessAsync(
            Request(fixture.Registration.Id),
            CancellationToken.None);

        Assert.False(rejected.Success);
        Assert.Equal("server.eula_acceptance_required", rejected.Error?.Code);
        Assert.Empty(fixture.Factory.Processes);
        Assert.False(File.Exists(fixture.EulaPath));
        Assert.False(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));

        var started = await processor.ProcessAsync(
            Request(fixture.Registration.Id) with { AcceptMinecraftEula = true },
            CancellationToken.None);

        Assert.True(started.Success);
        Assert.True(started.Mutation?.Changed);
        Assert.Single(fixture.Factory.Processes);
        Assert.Contains(
            "eula=true",
            await File.ReadAllTextAsync(fixture.EulaPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));
    }

    private static ProductIpcRequest Request(Guid serverId) => new(
        ProductIpcProtocol.CurrentSchemaVersion,
        Guid.NewGuid(),
        ProductIpcProtocol.ServerStartMethod,
        ProductApiProtocol.MinimumSupportedVersion,
        ProductApiProtocol.CurrentVersion)
    {
        ServerId = serverId,
    };

    [Fact]
    public async Task AutomaticRestart_DoesNotRetryWhenEulaWasRevoked()
    {
        var fixture = await EulaRuntimeFixture.CreateAsync(autoRestart: true);
        await using var runtime = fixture.Runtime;
        await runtime.StartAsync(fixture.Registration.Id, acceptMinecraftEula: true);
        var process = Assert.Single(fixture.Factory.Processes);

        await File.WriteAllTextAsync(fixture.EulaPath, "eula=false\n");
        process.Complete(1);

        Assert.True(SpinWait.SpinUntil(
            () => runtime.ReadConsole(fixture.Registration.Id, 0, 50).Entries.Any(entry =>
                entry.Text.Contains("Automatic restart failed", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3)));
        Assert.Single(fixture.Factory.Processes);
        Assert.Contains(
            "eula=false",
            await File.ReadAllTextAsync(fixture.EulaPath),
            StringComparison.OrdinalIgnoreCase);

        await Task.Delay(100);
        Assert.Single(fixture.Factory.Processes);
    }

    [Fact]
    public async Task RestartWithoutConfirmation_PreflightsBeforeStoppingRunningServer()
    {
        var fixture = await EulaRuntimeFixture.CreateAsync(autoRestart: false);
        await using var runtime = fixture.Runtime;
        await runtime.StartAsync(fixture.Registration.Id, acceptMinecraftEula: true);
        var process = Assert.Single(fixture.Factory.Processes);
        await File.WriteAllTextAsync(fixture.EulaPath, "eula=false\n");

        await Assert.ThrowsAsync<MinecraftEulaAcceptanceRequiredException>(() =>
            runtime.RestartAsync(fixture.Registration.Id));

        Assert.False(process.HasExited);
        Assert.Empty(process.Commands);
        Assert.Single(fixture.Factory.Processes);
        Assert.Equal(
            ProductServerState.Running,
            runtime.GetStatus(fixture.Registration.Id).Server.State);
    }

    private sealed record EulaRuntimeFixture(
        ProductServerRuntime Runtime,
        ProductServerTestProcessFactory Factory,
        ProductServerRegistration Registration,
        ProductDesiredRunIntentStore DesiredRunIntent,
        string EulaPath)
    {
        public static async Task<EulaRuntimeFixture> CreateAsync(bool autoRestart)
        {
            var layout = ProductServerRegistryTests.CreateLayout();
            var registration = ProductServerRegistryTests.Registration() with
            {
                ServerDirectory = "existing Paper server with spaces",
                AutoRestart = autoRestart,
            };
            var serverDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
            var javaPath = Path.Combine(
                layout.Runtimes,
                registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(serverDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
            await File.WriteAllBytesAsync(Path.Combine(serverDirectory, "server.jar"), []);
            await File.WriteAllBytesAsync(javaPath, []);

            var registry = new ProductServerRegistry(layout);
            await registry.LoadAsync();
            await registry.UpsertAsync(registration);
            var factory = new ProductServerTestProcessFactory();
            var desiredRunIntent = new ProductDesiredRunIntentStore(layout);
            await desiredRunIntent.LoadAsync();
            var eulaCoordinator = new ProductServerEulaCoordinator(
                layout,
                new MinecraftEulaAcceptanceService());
            var manager = new ServerProcessManager(
                new ServerProcessManagerOptions
                {
                    ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                    GracefulStopTimeout = TimeSpan.FromSeconds(1),
                    ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                    MonitorDrainTimeout = TimeSpan.FromSeconds(1),
                    AutoRestartDelay = TimeSpan.Zero,
                    PrepareStartWithContextAsync = eulaCoordinator.PrepareStartAsync,
                },
                factory);
            var runtime = new ProductServerRuntime(
                registry,
                layout,
                manager,
                desiredRunIntent,
                eulaCoordinator: eulaCoordinator);
            return new EulaRuntimeFixture(
                runtime,
                factory,
                registration,
                desiredRunIntent,
                Path.Combine(serverDirectory, "eula.txt"));
        }
    }
}
