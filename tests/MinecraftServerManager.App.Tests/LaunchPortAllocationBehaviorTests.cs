using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class LaunchPortAllocationBehaviorTests
{
    [Fact]
    public async Task Launch_FreeDefaultPort_Reuses25565AndRewritesOldServerPropertiesPort()
    {
        using var temporary = new TemporaryDirectory();
        var serverDirectory = temporary.CreateServerDirectory("server-a", configuredPort: 25566);
        var instanceId = Guid.NewGuid();
        await using var viewModel = CreateViewModel(temporary, tcpPorts: [], udpPorts: []);

        try
        {
            var assignment = await viewModel.AssignAvailablePortAsync(
                serverDirectory,
                instanceId,
                requestedPort: 25565,
                reserveForLaunch: true);

            Assert.Equal(25565, assignment.Port);
            Assert.Equal(25565, await ReadConfiguredPortAsync(serverDirectory));
            Assert.DoesNotContain(
                "server-port=25566",
                await File.ReadAllTextAsync(Path.Combine(serverDirectory, "server.properties")),
                StringComparison.Ordinal);
        }
        finally
        {
            viewModel.ReleasePendingLaunchPort(instanceId);
        }
    }

    [Fact]
    public async Task Launch_DefaultTcpPortIsListening_Allocates25566()
    {
        using var temporary = new TemporaryDirectory();
        var serverDirectory = temporary.CreateServerDirectory("server-a", configuredPort: 25565);
        var instanceId = Guid.NewGuid();
        await using var viewModel = CreateViewModel(temporary, tcpPorts: [25565], udpPorts: []);

        try
        {
            var assignment = await viewModel.AssignAvailablePortAsync(
                serverDirectory,
                instanceId,
                requestedPort: 25565,
                reserveForLaunch: true);

            Assert.Equal(25566, assignment.Port);
            Assert.Equal(25566, await ReadConfiguredPortAsync(serverDirectory));
        }
        finally
        {
            viewModel.ReleasePendingLaunchPort(instanceId);
        }
    }

    [Fact]
    public async Task Launch_DefaultPortHasOnlyUdpListener_StillAllocatesTcpPort25565()
    {
        using var temporary = new TemporaryDirectory();
        var serverDirectory = temporary.CreateServerDirectory("server-a", configuredPort: 25566);
        var instanceId = Guid.NewGuid();
        await using var viewModel = CreateViewModel(temporary, tcpPorts: [], udpPorts: [25565]);

        try
        {
            var assignment = await viewModel.AssignAvailablePortAsync(
                serverDirectory,
                instanceId,
                requestedPort: 25565,
                reserveForLaunch: true);

            Assert.Equal(25565, assignment.Port);
            Assert.Equal(25565, await ReadConfiguredPortAsync(serverDirectory));
        }
        finally
        {
            viewModel.ReleasePendingLaunchPort(instanceId);
        }
    }

    [Fact]
    public async Task ConcurrentLaunchReservations_AreDistinct_AndReleasedLowerPortIsReused()
    {
        using var temporary = new TemporaryDirectory();
        var firstDirectory = temporary.CreateServerDirectory("server-a", configuredPort: 25566);
        var secondDirectory = temporary.CreateServerDirectory("server-b", configuredPort: 25566);
        var thirdDirectory = temporary.CreateServerDirectory("server-c", configuredPort: 25566);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        await using var viewModel = CreateViewModel(temporary, tcpPorts: [], udpPorts: []);

        try
        {
            var launches = new[]
            {
                new PendingLaunch(
                    firstId,
                    viewModel.AssignAvailablePortAsync(
                        firstDirectory,
                        firstId,
                        requestedPort: 25565,
                        reserveForLaunch: true)),
                new PendingLaunch(
                    secondId,
                    viewModel.AssignAvailablePortAsync(
                        secondDirectory,
                        secondId,
                        requestedPort: 25565,
                        reserveForLaunch: true))
            };

            await Task.WhenAll(launches.Select(launch => launch.Assignment));
            Assert.Equal(
                [25565, 25566],
                launches.Select(launch => launch.Assignment.Result.Port).Order().ToArray());

            var lowerPortLaunch = launches.Single(launch => launch.Assignment.Result.Port == 25565);
            viewModel.ReleasePendingLaunchPort(lowerPortLaunch.InstanceId);

            var thirdAssignment = await viewModel.AssignAvailablePortAsync(
                thirdDirectory,
                thirdId,
                requestedPort: 25565,
                reserveForLaunch: true);

            Assert.Equal(25565, thirdAssignment.Port);
            Assert.Equal(25565, await ReadConfiguredPortAsync(thirdDirectory));
        }
        finally
        {
            viewModel.ReleasePendingLaunchPort(firstId);
            viewModel.ReleasePendingLaunchPort(secondId);
            viewModel.ReleasePendingLaunchPort(thirdId);
        }
    }

    [Fact]
    public async Task VelocityLaunch_RewritesPortArgumentWithoutCreatingServerProperties()
    {
        using var temporary = new TemporaryDirectory();
        var serverDirectory = temporary.CreateEmptyServerDirectory("velocity");
        var instance = new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = "Velocity",
            DirectoryPath = serverDirectory,
            CoreType = CoreType.Velocity,
            Port = 25566,
            ServerArguments = ["--show-config", "--port=25566"]
        };
        await using var viewModel = CreateViewModel(temporary, tcpPorts: [], udpPorts: []);

        try
        {
            var assignment = await viewModel.AssignAvailablePortAsync(
                serverDirectory,
                instance.Id,
                requestedPort: 25565,
                reserveForLaunch: true,
                launchConfiguration: instance);

            Assert.Equal(25565, assignment.Port);
            Assert.Equal(["--show-config", "--port", "25565"], instance.ServerArguments);
            Assert.False(File.Exists(Path.Combine(serverDirectory, "server.properties")));
        }
        finally
        {
            viewModel.ReleasePendingLaunchPort(instance.Id);
        }
    }

    private static MainWindowViewModel CreateViewModel(
        TemporaryDirectory temporary,
        int[] tcpPorts,
        int[] udpPorts)
    {
        var snapshot = new PortOccupancySnapshot(tcpPorts.ToHashSet(), udpPorts.ToHashSet());
        return new MainWindowViewModel(
            new ApplicationPaths(temporary.ApplicationRoot),
            () => snapshot);
    }

    private static async Task<int?> ReadConfiguredPortAsync(string serverDirectory)
    {
        var service = new ServerPropertiesPortService();
        return await service.ReadServerPortAsync(Path.Combine(serverDirectory, "server.properties"));
    }

    private sealed record PendingLaunch(
        Guid InstanceId,
        Task<MainWindowViewModel.PortAssignment> Assignment);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"msm-launch-port-{Guid.NewGuid():N}");
            ApplicationRoot = System.IO.Path.Combine(Path, "app");
            Directory.CreateDirectory(ApplicationRoot);
        }

        public string Path { get; }

        public string ApplicationRoot { get; }

        public string CreateServerDirectory(string name, int configuredPort)
        {
            var directory = CreateEmptyServerDirectory(name);
            File.WriteAllText(
                System.IO.Path.Combine(directory, "server.properties"),
                $"motd=Port allocation test{Environment.NewLine}server-port={configuredPort}{Environment.NewLine}online-mode=true{Environment.NewLine}");
            return directory;
        }

        public string CreateEmptyServerDirectory(string name)
        {
            var directory = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Preserve the original test result when Windows still owns an asynchronous handle.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original test result.
            }
        }
    }
}
