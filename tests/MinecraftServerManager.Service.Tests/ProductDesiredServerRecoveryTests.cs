using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Data;
using MinecraftServerManager.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductDesiredServerRecoveryTests
{
    [Fact]
    public async Task PlannedShutdownPreservesIntentAndRecreatedServiceRestoresServer()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration();
        EnsureLaunchFiles(layout, registration);
        var first = await CreateRuntimeAsync(layout, [registration]);
        await first.Runtime.StartAsync(registration.Id);

        await first.Runtime.ShutdownAsync();

        Assert.True(first.Intent.IsDesired(registration.Id));
        Assert.Contains("stop", Assert.Single(first.Factory.Processes).Commands);
        await first.Runtime.DisposeAsync();

        var propertiesPath = Path.Combine(
            layout.Servers,
            registration.ServerDirectory,
            "server.properties");
        await File.WriteAllTextAsync(propertiesPath, "server-port=29999\n");

        var recreated = await CreateRuntimeAsync(layout, [registration]);
        var database = new ProductDatabase(Path.Combine(layout.Data, "recovery-audit.db"));
        await database.InitializeAsync();
        var audit = new ProductSecurityAuditStore(database);
        var serviceState = ReadyState();
        var recovery = new ProductDesiredServerRecoveryHostedService(
            recreated.Intent,
            recreated.Runtime,
            audit,
            serviceState,
            TimeProvider.System,
            NullLogger<ProductDesiredServerRecoveryHostedService>.Instance);

        var startedAt = DateTime.UtcNow;
        await recovery.StartAsync(CancellationToken.None);
        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => recreated.Factory.Processes.Count == 1);

        Assert.Equal(ProductServerState.Running, recreated.Runtime.GetStatus(registration.Id).Server.State);
        Assert.Equal(25565, recreated.Runtime.GetStatus(registration.Id).Server.Port);
        Assert.Equal(25565, await new ServerPropertiesPortService().ReadServerPortAsync(propertiesPath));
        Assert.True(recreated.Intent.IsDesired(registration.Id));
        var entries = await audit.ReadRecentAsync(10);
        Assert.Contains(entries, entry =>
            entry.ServerId == registration.Id &&
            entry.ActionCode == "server.restore" &&
            entry.OutcomeCode == "succeeded" &&
            entry.ReasonCode == "desired_restore_succeeded");

        await recovery.StopAsync(CancellationToken.None);
        await recreated.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task OneFailedRestoreIsAuditedAndDoesNotBlockRemainingServer()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var failedId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var successfulId = Guid.Parse("f0000000-0000-0000-0000-000000000002");
        var failed = ProductServerRegistryTests.Registration(failedId) with
        {
            Name = "Fails",
            ServerDirectory = "fails",
            JavaRuntimePath = "java-fails/bin/java.exe",
            Port = 25565,
        };
        var successful = ProductServerRegistryTests.Registration(successfulId) with
        {
            Name = "Succeeds",
            ServerDirectory = "succeeds",
            JavaRuntimePath = "java-succeeds/bin/java.exe",
            Port = 25566,
        };
        EnsureLaunchFiles(layout, failed);
        EnsureLaunchFiles(layout, successful);
        var fixture = await CreateRuntimeAsync(layout, [failed, successful]);
        await fixture.Intent.SetDesiredAsync(failed.Id, true);
        await fixture.Intent.SetDesiredAsync(successful.Id, true);
        fixture.Factory.StartResults.Enqueue(false);
        fixture.Factory.StartResults.Enqueue(true);
        var database = new ProductDatabase(Path.Combine(layout.Data, "recovery-audit.db"));
        await database.InitializeAsync();
        var audit = new ProductSecurityAuditStore(database);
        var recovery = new ProductDesiredServerRecoveryHostedService(
            fixture.Intent,
            fixture.Runtime,
            audit,
            ReadyState(),
            TimeProvider.System,
            NullLogger<ProductDesiredServerRecoveryHostedService>.Instance);

        await recovery.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fixture.Factory.Processes.Count == 2);
        await WaitUntilAsync(async () => (await audit.ReadRecentAsync(20)).Count >= 4);

        Assert.Equal(ProductServerState.Running, fixture.Runtime.GetStatus(successful.Id).Server.State);
        var entries = await audit.ReadRecentAsync(20);
        Assert.Contains(entries, entry =>
            entry.ServerId == failed.Id &&
            entry.OutcomeCode == "failed" &&
            entry.ReasonCode == "desired_restore_failed");
        Assert.Contains(entries, entry =>
            entry.ServerId == successful.Id &&
            entry.OutcomeCode == "succeeded" &&
            entry.ReasonCode == "desired_restore_succeeded");

        await recovery.StopAsync(CancellationToken.None);
        await fixture.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task CorruptIntentFailsClosedAndCreatesDurableAuditRecord()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration();
        EnsureLaunchFiles(layout, registration);
        var fixture = await CreateRuntimeAsync(layout, [registration]);
        await File.WriteAllTextAsync(
            Path.Combine(layout.Operations, ProductDesiredRunIntentStore.FileName),
            "{\"schemaVersion\":999,\"serverIds\":[]}");
        var freshIntent = new ProductDesiredRunIntentStore(layout);
        var manager = CreateManager(fixture.Factory);
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        var runtime = new ProductServerRuntime(registry, layout, manager, freshIntent);
        var database = new ProductDatabase(Path.Combine(layout.Data, "recovery-audit.db"));
        await database.InitializeAsync();
        var audit = new ProductSecurityAuditStore(database);
        var recovery = new ProductDesiredServerRecoveryHostedService(
            freshIntent,
            runtime,
            audit,
            ReadyState(),
            TimeProvider.System,
            NullLogger<ProductDesiredServerRecoveryHostedService>.Instance);

        await recovery.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await audit.ReadRecentAsync(10)).Count >= 1);

        Assert.Empty(fixture.Factory.Processes);
        var entry = Assert.Single(await audit.ReadRecentAsync(10));
        Assert.Null(entry.ServerId);
        Assert.Equal("desired_intent_invalid", entry.ReasonCode);
        Assert.Equal("failed", entry.OutcomeCode);

        await recovery.StopAsync(CancellationToken.None);
        await runtime.DisposeAsync();
        await fixture.Runtime.DisposeAsync();
    }

    private static async Task<RuntimeFixture> CreateRuntimeAsync(
        ProductDataLayout layout,
        IReadOnlyList<ProductServerRegistration> registrations)
    {
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        foreach (var registration in registrations)
        {
            await registry.UpsertAsync(registration);
        }

        var factory = new ProductServerTestProcessFactory();
        var intent = new ProductDesiredRunIntentStore(layout);
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            () => new PortOccupancySnapshot(new HashSet<int>(), new HashSet<int>()));
        var manager = CreateManager(factory, coordinator);
        var runtime = new ProductServerRuntime(registry, layout, manager, intent);
        return new RuntimeFixture(runtime, factory, intent);
    }

    private static ServerProcessManager CreateManager(
        ProductServerTestProcessFactory factory,
        ProductServerPortCoordinator? coordinator = null)
    {
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(100),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(100),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(100),
                PrepareStartAsync = coordinator is null ? null : coordinator.PrepareStartAsync,
                PreparedStartAborted = coordinator is null ? null : coordinator.PreparedStartAborted,
            },
            factory);
        if (coordinator is not null)
        {
            manager.StateChanged += coordinator.ObserveStateChanged;
        }

        return manager;
    }

    private static void EnsureLaunchFiles(
        ProductDataLayout layout,
        ProductServerRegistration registration)
    {
        var serverDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
        var javaPath = Path.Combine(
            layout.Runtimes,
            registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(serverDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        File.WriteAllBytes(Path.Combine(serverDirectory, registration.ServerJarPath), []);
        File.WriteAllBytes(javaPath, []);
    }

    private static ProductServiceState ReadyState()
    {
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        return state;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed record RuntimeFixture(
        ProductServerRuntime Runtime,
        ProductServerTestProcessFactory Factory,
        ProductDesiredRunIntentStore Intent);
}
