using System.Diagnostics;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerPortCoordinatorTests
{
    [Fact]
    public async Task SavedServicePort_IsPreferredAndSynchronizedToActualWorkingDirectoryBeforeLaunch()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var launch = CreateLaunch(layout, registration);
        await EnsureProcessLaunchFilesAsync(layout, registration, launch);
        var propertiesPath = Path.Combine(launch.DirectoryPath, "server.properties");
        await File.WriteAllTextAsync(propertiesPath, "motd=keep-me\nserver-port=25565\n");
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            EmptyOccupancy);
        var factory = new ProductServerTestProcessFactory();
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(100),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(100),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(100),
                PrepareStartAsync = coordinator.PrepareStartAsync,
                PreparedStartAborted = coordinator.PreparedStartAborted,
            },
            factory);
        manager.StateChanged += coordinator.ObserveStateChanged;
        var intent = new ProductDesiredRunIntentStore(layout);
        await using var runtime = new ProductServerRuntime(registry, layout, manager, intent);

        var saved = await runtime.UpdateSettingsAsync(
            registration.Id,
            new ProductServerSettingsUpdateRequest(
                registration.Name,
                registration.MinimumMemoryMb,
                registration.MaximumMemoryMb,
                Port: 25566,
                AutoRestart: registration.AutoRestart));
        var started = await runtime.StartAsync(registration.Id);

        Assert.Equal(25566, saved.Registration.Port);
        Assert.Equal(25566, started.Status.Server.Port);
        Assert.Equal(25566, registry.GetAll().Single().Port);
        Assert.Equal(25566, await new ServerPropertiesPortService().ReadServerPortAsync(propertiesPath));
        Assert.Contains("motd=keep-me", await File.ReadAllTextAsync(propertiesPath));
        Assert.Equal(launch.DirectoryPath, Assert.Single(factory.Processes).StartInfo!.WorkingDirectory);
    }

    [Fact]
    public async Task SavedServicePortConflict_SearchesUpwardWithoutFallingBackTo25565()
    {
        var registration = ProductServerRegistryTests.Registration() with { Port = 25566 };
        var fixture = await CreateFixtureAsync(
            registration,
            () => new PortOccupancySnapshot(new HashSet<int> { 25566 }, new HashSet<int>()));
        var propertiesPath = Path.Combine(fixture.Launch.DirectoryPath, "server.properties");
        await File.WriteAllTextAsync(propertiesPath, "server-port=25565\n");

        await fixture.Coordinator.PrepareStartAsync(fixture.Launch, CancellationToken.None);

        Assert.Equal(25567, fixture.Launch.Port);
        Assert.Equal(25567, fixture.Registry.GetAll().Single().Port);
        Assert.Equal(25567, await new ServerPropertiesPortService().ReadServerPortAsync(propertiesPath));
    }

    [Fact]
    public async Task PrepareStart_UsesLowestFreeTcpPort_IgnoresUdp_AndPersistsConfiguration()
    {
        var fixture = await CreateFixtureAsync(
            ProductServerRegistryTests.Registration(),
            () => new PortOccupancySnapshot(new HashSet<int> { 25565 }, new HashSet<int> { 25566 }));
        var propertiesPath = Path.Combine(fixture.Launch.DirectoryPath, "server.properties");
        await File.WriteAllTextAsync(propertiesPath, "motd=keep-me\nserver-port=29999\n");

        await fixture.Coordinator.PrepareStartAsync(fixture.Launch, CancellationToken.None);

        Assert.Equal(25566, fixture.Launch.Port);
        Assert.Equal(25566, await new ServerPropertiesPortService().ReadServerPortAsync(propertiesPath));
        Assert.Contains("motd=keep-me", await File.ReadAllTextAsync(propertiesPath));
        Assert.Equal(25566, fixture.Registry.GetAll().Single().Port);
        Assert.True(fixture.Coordinator.TryGetReservation(fixture.Launch.Id, out var port, out var session));
        Assert.Equal(25566, port);
        Assert.Null(session);
    }

    [Fact]
    public async Task ConcurrentPrepares_ReserveDistinctPortsBeforeProcessesBind()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var first = ProductServerRegistryTests.Registration() with
        {
            ServerDirectory = "first",
        };
        var second = ProductServerRegistryTests.Registration() with
        {
            ServerDirectory = "second",
        };
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(first);
        await registry.UpsertAsync(second);
        var firstLaunch = CreateLaunch(layout, first);
        var secondLaunch = CreateLaunch(layout, second);
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            EmptyOccupancy);

        await Task.WhenAll(
            coordinator.PrepareStartAsync(firstLaunch, CancellationToken.None),
            coordinator.PrepareStartAsync(secondLaunch, CancellationToken.None));

        Assert.Equal([25565, 25566], new[] { firstLaunch.Port, secondLaunch.Port }.Order());
    }

    [Fact]
    public async Task Velocity_PrepareNormalizesAndPersistsOnlyPortArguments()
    {
        var registration = ProductServerRegistryTests.Registration() with
        {
            CoreType = nameof(CoreType.Velocity),
            ServerArguments = ["--help", "-p=29999", "--port", "30000"],
        };
        var fixture = await CreateFixtureAsync(registration, EmptyOccupancy);

        await fixture.Coordinator.PrepareStartAsync(fixture.Launch, CancellationToken.None);

        Assert.Equal(["--help", "--port", "25565"], fixture.Launch.ServerArguments);
        Assert.Equal(fixture.Launch.ServerArguments, fixture.Registry.GetAll().Single().ServerArguments);
        Assert.False(File.Exists(Path.Combine(fixture.Launch.DirectoryPath, "server.properties")));
    }

    [Fact]
    public async Task Velocity_SettingsRace_PreservesLatestDurableArguments()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration() with
        {
            CoreType = nameof(CoreType.Velocity),
            ServerArguments = ["--old-setting", "--port", "29999"],
        };
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var launch = CreateLaunch(layout, registration);
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            () =>
            {
                registry.UpsertAsync(
                        registration with
                        {
                            ServerArguments = ["--new-setting", "--port", "30000"],
                        })
                    .GetAwaiter()
                    .GetResult();
                return EmptyOccupancy();
            });

        await coordinator.PrepareStartAsync(launch, CancellationToken.None);

        Assert.Equal(["--new-setting", "--port", "25565"], launch.ServerArguments);
        Assert.Equal(launch.ServerArguments, registry.GetAll().Single().ServerArguments);
        Assert.DoesNotContain("--old-setting", launch.ServerArguments);
    }

    [Theory]
    [InlineData(CoreType.Waterfall)]
    [InlineData(CoreType.BungeeCord)]
    public async Task YamlProxyCore_FailsClosedWithoutWritingServerProperties(CoreType coreType)
    {
        var registration = ProductServerRegistryTests.Registration() with
        {
            CoreType = coreType.ToString(),
        };
        var fixture = await CreateFixtureAsync(registration, EmptyOccupancy);

        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => fixture.Coordinator.PrepareStartAsync(fixture.Launch, CancellationToken.None));

        Assert.Contains("YAML", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Launch.DirectoryPath, "server.properties")));
        Assert.Equal(registration.Port, fixture.Registry.GetAll().Single().Port);
        Assert.False(fixture.Coordinator.TryGetReservation(registration.Id, out _, out _));
    }

    [Theory]
    [InlineData(CoreType.Unknown)]
    [InlineData(CoreType.CustomJar)]
    public async Task CoreWithoutVerifiedPortAdapter_FailsClosedWithoutMutation(CoreType coreType)
    {
        var registration = ProductServerRegistryTests.Registration() with
        {
            CoreType = coreType.ToString(),
        };
        var fixture = await CreateFixtureAsync(registration, EmptyOccupancy);

        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => fixture.Coordinator.PrepareStartAsync(fixture.Launch, CancellationToken.None));

        Assert.Contains("adapter", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Launch.DirectoryPath, "server.properties")));
        Assert.Equal(registration.Port, fixture.Registry.GetAll().Single().Port);
        Assert.False(fixture.Coordinator.TryGetReservation(registration.Id, out _, out _));
    }

    [Fact]
    public async Task PrepareStart_RejectsJunctionedLaunchDirectoryBeforeOccupancyOrFileAccess()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var outside = Path.Combine(
            Path.GetTempPath(),
            "muhun-port-reparse-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var outsideProperties = Path.Combine(outside, "server.properties");
        await File.WriteAllTextAsync(outsideProperties, "server-port=29999\n");
        var linkPath = Path.Combine(layout.Servers, registration.ServerDirectory);
        CreateDirectoryJunction(linkPath, outside);
        var occupancyCaptured = false;
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            () =>
            {
                occupancyCaptured = true;
                return EmptyOccupancy();
            });
        var launch = new ServerInstance
        {
            Id = registration.Id,
            DirectoryPath = linkPath,
            CoreType = CoreType.Paper,
        };

        try
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => coordinator.PrepareStartAsync(launch, CancellationToken.None));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(occupancyCaptured);
            Assert.Equal("server-port=29999\n", await File.ReadAllTextAsync(outsideProperties));
            Assert.False(coordinator.TryGetReservation(registration.Id, out _, out _));
        }
        finally
        {
            Directory.Delete(linkPath);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task AutoRestart_JunctionSwapIsRejectedBeforeExternalLockOrPropertiesAreCreated()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration() with { AutoRestart = true };
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var launch = CreateLaunch(layout, registration);
        await EnsureProcessLaunchFilesAsync(layout, registration, launch);
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            EmptyOccupancy);
        var leaseProvider = new ProductServerDirectoryLeaseProvider(layout);
        var restartDelayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestartDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ProductServerTestProcessFactory();
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(100),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(100),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(100),
                AcquireDirectoryLease = leaseProvider.Acquire,
                PrepareStartAsync = coordinator.PrepareStartAsync,
                PreparedStartAborted = coordinator.PreparedStartAborted,
                GetAutoRestartDelayAsync = async (_, _, cancellationToken) =>
                {
                    restartDelayEntered.TrySetResult();
                    await releaseRestartDelay.Task.WaitAsync(cancellationToken);
                    return TimeSpan.Zero;
                },
            },
            factory);
        manager.StateChanged += coordinator.ObserveStateChanged;
        var intent = new ProductDesiredRunIntentStore(layout);
        await using var runtime = new ProductServerRuntime(registry, layout, manager, intent);
        await runtime.StartAsync(registration.Id);

        factory.Processes[0].Complete(17);
        await restartDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var originalDirectory = launch.DirectoryPath;
        var retainedDirectory = Path.Combine(layout.Servers, "retained-original");
        Directory.Move(originalDirectory, retainedDirectory);
        var outside = Path.Combine(
            Path.GetTempPath(),
            "muhun-port-restart-junction-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        CreateDirectoryJunction(originalDirectory, outside);

        try
        {
            releaseRestartDelay.TrySetResult();
            await WaitUntilAsync(() =>
                manager.TryGetSnapshot(registration.Id, out var snapshot) &&
                snapshot.State == ServerState.Faulted);

            Assert.Single(factory.Processes);
            Assert.False(File.Exists(Path.Combine(outside, ".minecraft-server-manager.lock")));
            Assert.False(File.Exists(Path.Combine(outside, "server.properties")));
        }
        finally
        {
            Directory.Delete(originalDirectory);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task StoppingKeepsReservation_TerminalReleasesIt_AndLateTerminalCannotReleaseNewLaunch()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registrations = Enumerable.Range(0, 3)
            .Select(index => ProductServerRegistryTests.Registration() with
            {
                ServerDirectory = $"server-{index}",
            })
            .ToArray();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        foreach (var registration in registrations)
        {
            await registry.UpsertAsync(registration);
        }

        var launches = registrations.Select(item => CreateLaunch(layout, item)).ToArray();
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            EmptyOccupancy);
        await coordinator.PrepareStartAsync(launches[0], CancellationToken.None);
        var oldSession = Guid.NewGuid();
        coordinator.ObserveStateChanged(
            null,
            new ServerStateChangedEventArgs(
                launches[0].Id,
                oldSession,
                ServerState.Stopped,
                ServerState.Starting));
        coordinator.ObserveStateChanged(
            null,
            new ServerStateChangedEventArgs(
                launches[0].Id,
                oldSession,
                ServerState.Running,
                ServerState.Stopping));

        await coordinator.PrepareStartAsync(launches[1], CancellationToken.None);
        Assert.Equal(25566, launches[1].Port);

        coordinator.PreparedStartAborted(launches[1].Id);
        coordinator.ObserveStateChanged(
            null,
            new ServerStateChangedEventArgs(
                launches[0].Id,
                oldSession,
                ServerState.Stopping,
                ServerState.Stopped));
        launches[0] = CreateLaunch(layout, registrations[0]);
        await coordinator.PrepareStartAsync(launches[0], CancellationToken.None);
        Assert.Equal(25565, launches[0].Port);

        coordinator.ObserveStateChanged(
            null,
            new ServerStateChangedEventArgs(
                launches[0].Id,
                oldSession,
                ServerState.Stopping,
                ServerState.Stopped));
        Assert.True(coordinator.TryGetReservation(launches[0].Id, out _, out var newSession));
        Assert.Null(newSession);

        await coordinator.PrepareStartAsync(launches[2], CancellationToken.None);
        Assert.Equal(25566, launches[2].Port);
    }

    [Fact]
    public async Task FileUpdateFailureAfterSelectionReleasesUnboundReservation()
    {
        var registration = ProductServerRegistryTests.Registration();
        var fixture = await CreateFixtureAsync(registration, EmptyOccupancy);
        Directory.CreateDirectory(Path.Combine(fixture.Launch.DirectoryPath, "server.properties"));

        await Assert.ThrowsAnyAsync<IOException>(
            () => fixture.Coordinator.PrepareStartAsync(
                fixture.Launch,
                CancellationToken.None));

        Assert.False(fixture.Coordinator.TryGetReservation(registration.Id, out _, out _));
    }

    [Fact]
    public async Task CancellationAfterSelectionReleasesUnboundReservation()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = await CreateFixtureAsync(
            ProductServerRegistryTests.Registration(),
            () =>
            {
                cancellation.Cancel();
                return EmptyOccupancy();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Coordinator.PrepareStartAsync(fixture.Launch, cancellation.Token));

        Assert.False(fixture.Coordinator.TryGetReservation(fixture.Launch.Id, out _, out _));
    }

    [Fact]
    public async Task BuildStartInfoFailure_InvokesPreparedAbortAndReleasesReservation()
    {
        var fixture = await CreateFixtureAsync(
            ProductServerRegistryTests.Registration(),
            EmptyOccupancy);
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                PrepareStartAsync = fixture.Coordinator.PrepareStartAsync,
                PreparedStartAborted = fixture.Coordinator.PreparedStartAborted,
            },
            new ProductServerTestProcessFactory());
        manager.StateChanged += fixture.Coordinator.ObserveStateChanged;

        await Assert.ThrowsAnyAsync<Exception>(
            () => manager.StartAsync(fixture.Launch));

        Assert.False(fixture.Coordinator.TryGetReservation(fixture.Launch.Id, out _, out _));
    }

    [Fact]
    public async Task ProcessStartFalse_ReleasesSessionBoundReservation()
    {
        var registration = ProductServerRegistryTests.Registration();
        var fixture = await CreateFixtureAsync(registration, EmptyOccupancy);
        await EnsureProcessLaunchFilesAsync(fixture.Layout, registration, fixture.Launch);
        var factory = new ProductServerTestProcessFactory();
        factory.StartResults.Enqueue(false);
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(100),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(100),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(100),
                PrepareStartAsync = fixture.Coordinator.PrepareStartAsync,
                PreparedStartAborted = fixture.Coordinator.PreparedStartAborted,
            },
            factory);
        manager.StateChanged += fixture.Coordinator.ObserveStateChanged;
        var intent = new ProductDesiredRunIntentStore(fixture.Layout);
        await using var runtime = new ProductServerRuntime(
            fixture.Registry,
            fixture.Layout,
            manager,
            intent);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StartAsync(registration.Id));

        Assert.False(fixture.Coordinator.TryGetReservation(registration.Id, out _, out _));
    }

    [Fact]
    public async Task ProcessManagerHooks_PreserveLastAssignedPreferredPortOnRestart()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var launch = CreateLaunch(layout, registration);
        var javaPath = Path.Combine(
            layout.Runtimes,
            registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        await File.WriteAllBytesAsync(javaPath, []);
        await File.WriteAllBytesAsync(Path.Combine(launch.DirectoryPath, registration.ServerJarPath), []);
        var occupiedTcpPorts = new HashSet<int> { 25565 };
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            () => new PortOccupancySnapshot(occupiedTcpPorts.ToHashSet(), new HashSet<int>()));
        var factory = new ProductServerTestProcessFactory();
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(100),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(100),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(100),
                PrepareStartAsync = coordinator.PrepareStartAsync,
                PreparedStartAborted = coordinator.PreparedStartAborted,
            },
            factory);
        manager.StateChanged += coordinator.ObserveStateChanged;
        var intent = new ProductDesiredRunIntentStore(layout);
        await using var runtime = new ProductServerRuntime(registry, layout, manager, intent);

        var started = await runtime.StartAsync(registration.Id);
        Assert.Equal(25566, started.Status.Server.Port);
        occupiedTcpPorts.Clear();

        var restarted = await runtime.RestartAsync(registration.Id);

        Assert.Equal(25566, restarted.Status.Server.Port);
        Assert.Equal(2, factory.Processes.Count);
        Assert.Equal(25566, registry.GetAll().Single().Port);
    }

    [Fact]
    public async Task ProcessManagerAutoRestart_PreservesLastAssignedPreferredPort()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration() with { AutoRestart = true };
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var launch = CreateLaunch(layout, registration);
        var javaPath = Path.Combine(
            layout.Runtimes,
            registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        await File.WriteAllBytesAsync(javaPath, []);
        await File.WriteAllBytesAsync(Path.Combine(launch.DirectoryPath, registration.ServerJarPath), []);
        var occupiedTcpPorts = new HashSet<int> { 25565 };
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            () => new PortOccupancySnapshot(occupiedTcpPorts.ToHashSet(), new HashSet<int>()));
        var factory = new ProductServerTestProcessFactory();
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(100),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(100),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(100),
                AutoRestartDelay = TimeSpan.FromMilliseconds(5),
                PrepareStartAsync = coordinator.PrepareStartAsync,
                PreparedStartAborted = coordinator.PreparedStartAborted,
            },
            factory);
        manager.StateChanged += coordinator.ObserveStateChanged;
        var intent = new ProductDesiredRunIntentStore(layout);
        await using var runtime = new ProductServerRuntime(registry, layout, manager, intent);

        var first = await runtime.StartAsync(registration.Id);
        Assert.Equal(25566, first.Status.Server.Port);
        occupiedTcpPorts.Clear();
        factory.Processes[0].Complete(17);

        await WaitUntilAsync(() => factory.Processes.Count == 2);
        Assert.Equal(25566, registry.GetAll().Single().Port);
        Assert.Equal(
            25566,
            await new ServerPropertiesPortService().ReadServerPortAsync(
                Path.Combine(launch.DirectoryPath, "server.properties")));
    }

    [Fact]
    public async Task DisableAutoRestartDuringPreparationWinsBeforeSessionCommit()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration() with { AutoRestart = true };
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var launch = CreateLaunch(layout, registration);
        await EnsureProcessLaunchFilesAsync(layout, registration, launch);
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            EmptyOccupancy);
        var restartPreparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestartPreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disabledPolicyObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ProductServerTestProcessFactory();
        var leaseProvider = new ProductServerDirectoryLeaseProvider(layout);
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(100),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(100),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(100),
                AutoRestartDelay = TimeSpan.Zero,
                AcquireDirectoryLease = leaseProvider.Acquire,
                PrepareStartAsync = coordinator.PrepareStartAsync,
                PreparedStartAborted = coordinator.PreparedStartAborted,
                ShouldAutoRestartAsync = (serverId, _) =>
                {
                    var enabled = registry.TryGet(serverId, out var current) && current.AutoRestart;
                    if (!enabled)
                    {
                        disabledPolicyObserved.TrySetResult();
                    }

                    return Task.FromResult(enabled);
                },
                PrepareAutoRestartAsync = async (_, cancellationToken) =>
                {
                    restartPreparationEntered.TrySetResult();
                    await releaseRestartPreparation.Task.WaitAsync(cancellationToken);
                },
            },
            factory);
        manager.StateChanged += coordinator.ObserveStateChanged;
        var intent = new ProductDesiredRunIntentStore(layout);
        await using var runtime = new ProductServerRuntime(registry, layout, manager, intent);
        await runtime.StartAsync(registration.Id);
        factory.Processes[0].Complete(17);
        await restartPreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await runtime.UpdateSettingsAsync(
            registration.Id,
            new ProductServerSettingsUpdateRequest(
                registration.Name,
                registration.MinimumMemoryMb,
                registration.MaximumMemoryMb,
                registration.Port,
                AutoRestart: false));
        releaseRestartPreparation.TrySetResult();
        await disabledPolicyObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => !coordinator.TryGetReservation(registration.Id, out _, out _));

        Assert.Single(factory.Processes);
        Assert.False(registry.GetAll().Single().AutoRestart);
    }

    [Fact]
    public async Task NonVelocityMutationDuringCrashDelayRefreshesCompleteRestartLaunchSnapshot()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration() with
        {
            AutoRestart = true,
            ServerArguments = ["--old-setting"],
        };
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var launch = CreateLaunch(layout, registration);
        await EnsureProcessLaunchFilesAsync(layout, registration, launch);
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            EmptyOccupancy);
        var restartPreparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestartPreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ProductServerTestProcessFactory();
        var leaseProvider = new ProductServerDirectoryLeaseProvider(layout);
        var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(100),
                ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(100),
                MonitorDrainTimeout = TimeSpan.FromMilliseconds(100),
                AutoRestartDelay = TimeSpan.Zero,
                AcquireDirectoryLease = leaseProvider.Acquire,
                PrepareStartAsync = coordinator.PrepareStartAsync,
                PreparedStartAborted = coordinator.PreparedStartAborted,
                RefreshAutoRestartSnapshotAsync = (snapshot, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Assert.True(registry.TryGet(snapshot.Id, out var current));
                    ProductServerRuntime.ApplyRegistrationLaunchSnapshot(snapshot, current, layout);
                    return Task.CompletedTask;
                },
                PrepareAutoRestartAsync = async (_, cancellationToken) =>
                {
                    restartPreparationEntered.TrySetResult();
                    await releaseRestartPreparation.Task.WaitAsync(cancellationToken);
                },
            },
            factory);
        manager.StateChanged += coordinator.ObserveStateChanged;
        var intent = new ProductDesiredRunIntentStore(layout);
        await using var runtime = new ProductServerRuntime(registry, layout, manager, intent);
        await runtime.StartAsync(registration.Id);
        factory.Processes[0].Complete(17);
        await restartPreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var newJar = "updated-server.jar";
        await File.WriteAllBytesAsync(Path.Combine(launch.DirectoryPath, newJar), []);
        await runtime.UpsertAsync(registration with
        {
            MinimumMemoryMb = 3072,
            MaximumMemoryMb = 6144,
            ServerJarPath = newJar,
            ServerArguments = ["--new-setting"],
        });
        releaseRestartPreparation.TrySetResult();
        await WaitUntilAsync(() => factory.Processes.Count == 2);

        var actualArguments = factory.Processes[1].StartInfo!.ArgumentList.ToArray();
        Assert.Contains("-Xms3072M", actualArguments);
        Assert.Contains("-Xmx6144M", actualArguments);
        Assert.Contains(newJar, actualArguments);
        Assert.Contains("--new-setting", actualArguments);
        Assert.DoesNotContain("--old-setting", actualArguments);
    }

    private static async Task<CoordinatorFixture> CreateFixtureAsync(
        ProductServerRegistration registration,
        Func<PortOccupancySnapshot> captureOccupancy)
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var launch = CreateLaunch(layout, registration);
        var coordinator = new ProductServerPortCoordinator(
            registry,
            layout,
            new ServerPropertiesPortService(),
            captureOccupancy);
        return new CoordinatorFixture(layout, registry, coordinator, launch);
    }

    private static ServerInstance CreateLaunch(
        ProductDataLayout layout,
        ProductServerRegistration registration)
    {
        var directory = Path.Combine(layout.Servers, registration.ServerDirectory);
        Directory.CreateDirectory(directory);
        return new ServerInstance
        {
            Id = registration.Id,
            Name = registration.Name,
            DirectoryPath = directory,
            CoreType = Enum.Parse<CoreType>(registration.CoreType),
            ServerArguments = registration.ServerArguments.ToList(),
            Port = registration.Port,
        };
    }

    private static PortOccupancySnapshot EmptyOccupancy()
        => new(new HashSet<int>(), new HashSet<int>());

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task EnsureProcessLaunchFilesAsync(
        ProductDataLayout layout,
        ProductServerRegistration registration,
        ServerInstance launch)
    {
        var javaPath = Path.Combine(
            layout.Runtimes,
            registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        await File.WriteAllBytesAsync(javaPath, []);
        await File.WriteAllBytesAsync(Path.Combine(launch.DirectoryPath, registration.ServerJarPath), []);
    }

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

    private sealed record CoordinatorFixture(
        ProductDataLayout Layout,
        ProductServerRegistry Registry,
        ProductServerPortCoordinator Coordinator,
        ServerInstance Launch);
}
