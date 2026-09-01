using System.Diagnostics;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerRuntimeTests
{
    [Fact]
    public async Task Runtime_OwnsStartStatusCommandCursorRestartAndStopThroughCoreManager()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;

        var started = await runtime.StartAsync(fixture.Registration.Id);
        Assert.True(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));
        var first = Assert.Single(fixture.Factory.Processes);
        first.EmitOutput("[Server thread/INFO]: ready");
        first.EmitError("[12:00:00] [Server thread/WARN]: warning");
        await runtime.SendCommandAsync(fixture.Registration.Id, "list");

        Assert.True(started.Changed);
        Assert.Equal(ProductServerState.Running, runtime.GetStatus(fixture.Registration.Id).Server.State);
        Assert.Contains("list", first.Commands);
        var console = runtime.ReadConsole(fixture.Registration.Id, 0, 50);
        Assert.Equal(2, console.Entries.Count);
        Assert.Equal(ProductConsoleSeverity.Warning, console.Entries[1].Severity);

        var restarted = await runtime.RestartAsync(fixture.Registration.Id);
        Assert.True(restarted.Changed);
        Assert.True(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));
        Assert.Equal(2, fixture.Factory.Processes.Count);
        Assert.Contains("stop", first.Commands);

        var stopped = await runtime.StopAsync(fixture.Registration.Id);
        Assert.True(stopped.Changed);
        Assert.Equal(ProductServerState.Stopped, stopped.Status.Server.State);
        Assert.False(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));
    }

    [Fact]
    public async Task Runtime_RejectsRegistrationMutationWhileProcessIsRunning()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        await runtime.StartAsync(fixture.Registration.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.UpsertAsync(
            fixture.Registration with { Name = "Changed while running" }));
    }

    [Fact]
    public async Task StatusProjectsServiceResolvedJavaAndOnlyActiveListenerState()
    {
        var fixture = await RuntimeFixture.CreateAsync(includeRuntimeStatusReaders: true);
        await using var runtime = fixture.Runtime;

        var stopped = runtime.GetStatus(fixture.Registration.Id);

        Assert.NotNull(stopped.Java);
        Assert.True(stopped.Java.Available);
        Assert.Equal(21, stopped.Java.MajorVersion);
        Assert.Null(stopped.PortListening);

        await runtime.StartAsync(fixture.Registration.Id);
        var running = runtime.GetStatus(fixture.Registration.Id);

        Assert.Equal(ProductServerState.Running, running.Server.State);
        Assert.True(running.PortListening);
        Assert.Equal("Eclipse Adoptium", running.Java?.Vendor);
    }

    [Fact]
    public async Task Runtime_ShutdownStopsEveryOwnedProcessAndRejectsNewStarts()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        await runtime.StartAsync(fixture.Registration.Id);

        await runtime.ShutdownAsync();

        Assert.Contains("stop", Assert.Single(fixture.Factory.Processes).Commands);
        Assert.True(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StartAsync(fixture.Registration.Id));
    }

    [Fact]
    public async Task FailedStart_DoesNotLeaveDesiredRunIntent()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        fixture.Factory.StartResults.Enqueue(false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StartAsync(fixture.Registration.Id));

        await fixture.DesiredRunIntent.LoadAsync();
        Assert.False(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));
    }

    [Fact]
    public async Task RemoveExplicitlyClearsStaleDesiredIntent()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        await fixture.DesiredRunIntent.SetDesiredAsync(fixture.Registration.Id, true);

        var removed = await runtime.RemoveAsync(fixture.Registration.Id);

        Assert.True(removed);
        Assert.False(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));
    }

    [Fact]
    public async Task PermanentDelete_StopsRunningProcessDeletesOwnedTreeThenRemovesRegistry()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        var serverDirectory = Path.Combine(
            fixture.Layout.Servers,
            fixture.Registration.ServerDirectory);
        await File.WriteAllTextAsync(Path.Combine(serverDirectory, "world.dat"), "world");
        await runtime.StartAsync(fixture.Registration.Id);

        var directory = runtime.GetDirectoryInfo(fixture.Registration.Id);
        var result = await runtime.DeletePermanentlyAsync(fixture.Registration.Id);

        Assert.Equal(serverDirectory, directory.DirectoryPath);
        Assert.True(directory.Exists);
        Assert.True(result.Deleted);
        Assert.Equal(TimeSpan.Zero, result.CompletedAtUtc.Offset);
        Assert.Contains("stop", Assert.Single(fixture.Factory.Processes).Commands);
        Assert.False(Directory.Exists(serverDirectory));
        Assert.False(fixture.DesiredRunIntent.IsDesired(fixture.Registration.Id));
        Assert.Throws<KeyNotFoundException>(
            () => runtime.GetRegistration(fixture.Registration.Id));
    }

    [Fact]
    public async Task PermanentDelete_RejectsDirectoryIdentityOwnedByAnotherRegistration()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        var duplicate = fixture.Registration with
        {
            Id = Guid.NewGuid(),
            Name = "Duplicate path guard",
        };
        await fixture.Registry.UpsertAsync(duplicate);
        var serverDirectory = Path.Combine(
            fixture.Layout.Servers,
            fixture.Registration.ServerDirectory);
        await File.WriteAllTextAsync(Path.Combine(serverDirectory, "world.dat"), "must-survive");

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(
            () => runtime.DeletePermanentlyAsync(fixture.Registration.Id));

        Assert.True(Directory.Exists(serverDirectory));
        Assert.Equal("must-survive", await File.ReadAllTextAsync(
            Path.Combine(serverDirectory, "world.dat")));
        Assert.Equal(fixture.Registration.Id, runtime.GetRegistration(fixture.Registration.Id).Id);
        Assert.Equal(duplicate.Id, runtime.GetRegistration(duplicate.Id).Id);
    }

    [Fact]
    public async Task PermanentDelete_RemovesNestedJunctionWithoutFollowingOutsideServersRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        var serverDirectory = Path.Combine(
            fixture.Layout.Servers,
            fixture.Registration.ServerDirectory);
        var outside = Path.Combine(fixture.Layout.Root, "outside-world");
        Directory.CreateDirectory(outside);
        var outsideWorld = Path.Combine(outside, "level.dat");
        await File.WriteAllTextAsync(outsideWorld, "must-survive");
        CreateDirectoryJunction(Path.Combine(serverDirectory, "linked-world"), outside);

        await runtime.DeletePermanentlyAsync(fixture.Registration.Id);

        Assert.False(Directory.Exists(serverDirectory));
        Assert.True(Directory.Exists(outside));
        Assert.Equal("must-survive", await File.ReadAllTextAsync(outsideWorld));
    }

    [Fact]
    public async Task IpcRuntimeMethods_AreVersionedAndReturnBoundedPayloads()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        var processor = new ProductIpcMessageProcessor(state, runtime);

        var list = await processor.ProcessAsync(Request(ProductIpcProtocol.ServerListMethod), default);
        var start = await processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerStartMethod) with { ServerId = fixture.Registration.Id },
            default);
        Assert.True(list.Success);
        Assert.Single(list.ServerPage!.Servers);
        Assert.True(start.Success);
        Assert.Equal(ProductServerState.Running, start.Mutation?.Status.Server.State);

        Assert.Single(fixture.Factory.Processes).EmitOutput("hello");
        var console = await processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerConsoleMethod) with
            {
                ServerId = fixture.Registration.Id,
                ConsoleCursor = 0,
                ConsoleLimit = 50,
            },
            default);
        Assert.Equal("hello", Assert.Single(console.Console!.Entries).Text);

        var oldClient = await processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerListMethod) with
            {
                ClientMaximumApiVersion = new ProductApiVersion(1, 0),
            },
            default);
        Assert.False(oldClient.Success);
        Assert.Equal("protocol.method_version_unsupported", oldClient.Error?.Code);
    }

    [Fact]
    public async Task IpcPlayerList_ProjectsBoundedPresenceWithoutPollingTheProcess()
    {
        var fixture = await RuntimeFixture.CreateAsync();
        await using var runtime = fixture.Runtime;
        using var tracker = new ProductPlayerPresenceTracker(
            fixture.Manager,
            fixture.Registry,
            TimeProvider.System);
        await tracker.StartAsync(default);
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        var processor = new ProductIpcMessageProcessor(
            state,
            runtime,
            updates: null,
            imports: null,
            remoteWeb: null,
            remoteAccounts: null,
            remoteDevices: null,
            discordWebhook: null,
            notificationOutbox: null,
            backups: null,
            players: tracker);

        await runtime.StartAsync(fixture.Registration.Id);
        Assert.Single(fixture.Factory.Processes)
            .EmitOutput("[12:34:56] [Server thread/INFO]: PlayerOne joined the game");
        Assert.True(SpinWait.SpinUntil(
            () => tracker.GetPlayers(fixture.Registration.Id).Count == 1,
            TimeSpan.FromSeconds(2)));
        var response = await processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerPlayersMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);

        Assert.True(response.Success);
        Assert.Equal(fixture.Registration.Id, response.Players?.ServerId);
        Assert.Equal("PlayerOne", Assert.Single(response.Players!.Players).Name);
        await tracker.StopAsync(default);
    }

    private static ProductIpcRequest Request(string method) => new(
        ProductIpcProtocol.CurrentSchemaVersion,
        Guid.NewGuid(),
        method,
        ProductApiProtocol.MinimumSupportedVersion,
        ProductApiProtocol.CurrentVersion);

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
        }) ?? throw new InvalidOperationException("Could not create test junction.");
        process.WaitForExit();
        if (process.ExitCode != 0 ||
            !File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Could not create test reparse point.");
        }
    }

    private sealed record RuntimeFixture(
        ProductServerRuntime Runtime,
        ProductServerTestProcessFactory Factory,
        ProductServerRegistration Registration,
        ProductDesiredRunIntentStore DesiredRunIntent,
        ProductDataLayout Layout,
        ProductServerRegistry Registry,
        ServerProcessManager Manager)
    {
        public static async Task<RuntimeFixture> CreateAsync(bool includeRuntimeStatusReaders = false)
        {
            var layout = ProductServerRegistryTests.CreateLayout();
            var registration = ProductServerRegistryTests.Registration();
            var serverDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
            var javaPath = Path.Combine(
                layout.Runtimes,
                registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(serverDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
            File.WriteAllBytes(Path.Combine(serverDirectory, "server.jar"), []);
            File.WriteAllBytes(javaPath, []);
            if (includeRuntimeStatusReaders)
            {
                var runtimeHome = Directory.GetParent(Path.GetDirectoryName(javaPath)!)!.FullName;
                File.WriteAllText(
                    Path.Combine(runtimeHome, "release"),
                    "JAVA_VERSION=\"21.0.8+9\"\nIMPLEMENTOR=\"Eclipse Adoptium\"\nOS_ARCH=\"amd64\"\n");
            }

            var registry = new ProductServerRegistry(layout);
            await registry.LoadAsync();
            await registry.UpsertAsync(registration);
            var factory = new ProductServerTestProcessFactory();
            var desiredRunIntent = new ProductDesiredRunIntentStore(layout);
            var manager = new ServerProcessManager(
                new ServerProcessManagerOptions
                {
                    ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                    GracefulStopTimeout = TimeSpan.FromSeconds(1),
                    ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                    MonitorDrainTimeout = TimeSpan.FromSeconds(1),
                },
                factory);
            var administration = includeRuntimeStatusReaders
                ? new ProductServerAdministrationReader(layout, registry, TimeProvider.System)
                : null;
            var listener = includeRuntimeStatusReaders
                ? new ProductServerListenerStateReader(
                    () => new PortOccupancySnapshot(
                        new HashSet<int> { registration.Port },
                        new HashSet<int>()),
                    TimeProvider.System)
                : null;
            return new RuntimeFixture(
                new ProductServerRuntime(
                    registry,
                    layout,
                    manager,
                    desiredRunIntent,
                    administrationReader: administration,
                    listenerStateReader: listener),
                factory,
                registration,
                desiredRunIntent,
                layout,
                registry,
                manager);
        }
    }
}
