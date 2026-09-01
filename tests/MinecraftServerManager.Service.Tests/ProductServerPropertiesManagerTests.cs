using System.Text;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerPropertiesManagerTests
{
    [Fact]
    public async Task MissingFile_ReadDoesNotCreateAndFirstSaveUsesMissingRevision()
    {
        await using var fixture = await PropertiesFixture.CreateAsync();

        var loaded = await fixture.Manager.ReadAsync(fixture.Registration.Id);

        Assert.False(loaded.Exists);
        Assert.Equal(string.Empty, loaded.Text);
        Assert.Equal(ProductServerPropertiesContract.MissingRevision, loaded.RevisionSha256);
        Assert.False(File.Exists(fixture.PropertiesPath));

        var saved = await fixture.Manager.SaveAsync(
            fixture.Registration.Id,
            new ProductServerPropertiesUpdateRequest(
                "motd=first save\nserver-port=25566\n",
                loaded.RevisionSha256));

        Assert.True(saved.Exists);
        Assert.Equal("motd=first save\nserver-port=25566\n", saved.Text);
        Assert.True(File.Exists(fixture.PropertiesPath));
        Assert.True(fixture.Registry.TryGet(fixture.Registration.Id, out var registration));
        Assert.Equal(25566, registration.Port);
    }

    [Fact]
    public async Task Save_PreservesSourceEncoding_ReReadsCommittedText_AndSynchronizesValidPort()
    {
        await using var fixture = await PropertiesFixture.CreateAsync();
        var original = "motd=caf\u00e9\r\nserver-port=25565\r\n";
        await File.WriteAllBytesAsync(fixture.PropertiesPath, Encoding.Latin1.GetBytes(original));
        var loaded = await fixture.Manager.ReadAsync(fixture.Registration.Id);
        var updatedText = "motd=caf\u00e9\r\nserver-port=25570\r\n";

        var saved = await fixture.Manager.SaveAsync(
            fixture.Registration.Id,
            new ProductServerPropertiesUpdateRequest(updatedText, loaded.RevisionSha256));

        Assert.True(saved.Exists);
        Assert.Equal(updatedText, saved.Text);
        Assert.NotEqual(loaded.RevisionSha256, saved.RevisionSha256);
        Assert.Equal(Encoding.Latin1.GetBytes(updatedText), await File.ReadAllBytesAsync(fixture.PropertiesPath));
        Assert.Equal(Encoding.Latin1.GetBytes(original), await File.ReadAllBytesAsync(fixture.PropertiesPath + ".bak"));
        Assert.True(fixture.Registry.TryGet(fixture.Registration.Id, out var registration));
        Assert.Equal(25570, registration.Port);
    }

    [Theory]
    [InlineData("motd=no-port\n")]
    [InlineData("server-port=not-a-number\n")]
    [InlineData("server-port=70000\n")]
    public async Task Save_InvalidOrMissingPort_DoesNotChangeRegistry(string text)
    {
        await using var fixture = await PropertiesFixture.CreateAsync();
        var loaded = await fixture.Manager.ReadAsync(fixture.Registration.Id);

        var saved = await fixture.Manager.SaveAsync(
            fixture.Registration.Id,
            new ProductServerPropertiesUpdateRequest(text, loaded.RevisionSha256));

        Assert.Equal(text, saved.Text);
        Assert.True(fixture.Registry.TryGet(fixture.Registration.Id, out var registration));
        Assert.Equal(25565, registration.Port);
    }

    [Fact]
    public async Task Save_VelocityTextDoesNotTreatServerPortAsLaunchPort()
    {
        await using var fixture = await PropertiesFixture.CreateAsync();
        await fixture.Registry.UpsertAsync(fixture.Registration with
        {
            CoreType = "Velocity",
            ServerArguments = ["--port", "25565"],
        });
        var loaded = await fixture.Manager.ReadAsync(fixture.Registration.Id);

        var saved = await fixture.Manager.SaveAsync(
            fixture.Registration.Id,
            new ProductServerPropertiesUpdateRequest(
                "server-port=25570\n",
                loaded.RevisionSha256));

        Assert.Equal("server-port=25570\n", saved.Text);
        Assert.True(fixture.Registry.TryGet(fixture.Registration.Id, out var registration));
        Assert.Equal(25565, registration.Port);
        Assert.Equal(["--port", "25565"], registration.ServerArguments);
    }

    [Fact]
    public async Task Save_RejectsStaleRevisionWithoutReplacingNewerFile()
    {
        await using var fixture = await PropertiesFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.PropertiesPath, "server-port=25565\n");
        var loaded = await fixture.Manager.ReadAsync(fixture.Registration.Id);
        await File.WriteAllTextAsync(fixture.PropertiesPath, "server-port=25566\n");

        await Assert.ThrowsAsync<ProductServerPropertiesConflictException>(() =>
            fixture.Manager.SaveAsync(
                fixture.Registration.Id,
                new ProductServerPropertiesUpdateRequest(
                    "server-port=25570\n",
                    loaded.RevisionSha256)));

        Assert.Equal("server-port=25566\n", await File.ReadAllTextAsync(fixture.PropertiesPath));
        Assert.False(File.Exists(fixture.PropertiesPath + ".bak"));
    }

    [Fact]
    public async Task Save_RegistryPortFailure_RestoresOriginalDocumentAndRevision()
    {
        await using var fixture = await PropertiesFixture.CreateAsync(
            updateLaunchConfigurationAsync: static (_, _, _, _, _) =>
                Task.FromException<ProductServerRegistration>(
                    new IOException("Simulated registry persistence failure.")));
        var original = "motd=caf\u00e9 before\r\nserver-port=25565\r\n";
        await File.WriteAllBytesAsync(fixture.PropertiesPath, Encoding.Latin1.GetBytes(original));
        var loaded = await fixture.Manager.ReadAsync(fixture.Registration.Id);

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Manager.SaveAsync(
                fixture.Registration.Id,
                new ProductServerPropertiesUpdateRequest(
                    "motd=caf\u00e9 after\r\nserver-port=25570\r\n",
                    loaded.RevisionSha256)));

        Assert.Equal(Encoding.Latin1.GetBytes(original), await File.ReadAllBytesAsync(fixture.PropertiesPath));
        var restored = await fixture.Manager.ReadAsync(fixture.Registration.Id);
        Assert.Equal(loaded.RevisionSha256, restored.RevisionSha256);
        Assert.Equal(original, restored.Text);
        Assert.True(fixture.Registry.TryGet(fixture.Registration.Id, out var registration));
        Assert.Equal(25565, registration.Port);
    }

    [Fact]
    public async Task Save_RegistryPortCancellation_RemovesNewDocumentCreatedByFailedOperation()
    {
        await using var fixture = await PropertiesFixture.CreateAsync(
            updateLaunchConfigurationAsync: static (_, _, _, _, _) =>
                Task.FromCanceled<ProductServerRegistration>(new CancellationToken(canceled: true)));
        var loaded = await fixture.Manager.ReadAsync(fixture.Registration.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Manager.SaveAsync(
                fixture.Registration.Id,
                new ProductServerPropertiesUpdateRequest(
                    "server-port=25570\n",
                    loaded.RevisionSha256)));

        Assert.False(File.Exists(fixture.PropertiesPath));
        var restored = await fixture.Manager.ReadAsync(fixture.Registration.Id);
        Assert.False(restored.Exists);
        Assert.Equal(ProductServerPropertiesContract.MissingRevision, restored.RevisionSha256);
        Assert.True(fixture.Registry.TryGet(fixture.Registration.Id, out var registration));
        Assert.Equal(25565, registration.Port);
    }

    [Fact]
    public async Task Read_RejectsOversizedOrNonRegularPropertiesTarget()
    {
        await using var oversized = await PropertiesFixture.CreateAsync();
        await File.WriteAllBytesAsync(
            oversized.PropertiesPath,
            new byte[ProductServerPropertiesContract.MaximumSourceFileBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            oversized.Manager.ReadAsync(oversized.Registration.Id));

        await using var directory = await PropertiesFixture.CreateAsync();
        Directory.CreateDirectory(directory.PropertiesPath);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            directory.Manager.ReadAsync(directory.Registration.Id));
    }

    [Fact]
    public async Task Save_WaitsForInFlightStartAndThenRejectsActiveSession()
    {
        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await PropertiesFixture.CreateAsync(new ServerProcessManagerOptions
        {
            ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
            PrepareStartWithContextAsync = async (_, _, cancellationToken) =>
            {
                preparationEntered.TrySetResult();
                await releasePreparation.Task.WaitAsync(cancellationToken);
            },
        });
        await File.WriteAllTextAsync(fixture.PropertiesPath, "server-port=25565\n");
        var loaded = await fixture.Manager.ReadAsync(fixture.Registration.Id);
        var instance = new ServerInstance
        {
            Id = fixture.Registration.Id,
            Name = fixture.Registration.Name,
            DirectoryPath = fixture.ServerDirectory,
            JavaExecutablePath = fixture.JavaPath,
            ServerJarPath = "server.jar",
            CoreType = CoreType.Paper,
        };
        var start = fixture.Processes.StartAsync(instance);
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var save = fixture.Manager.SaveAsync(
            fixture.Registration.Id,
            new ProductServerPropertiesUpdateRequest(
                "server-port=25570\n",
                loaded.RevisionSha256));
        await Task.Delay(100);
        Assert.False(save.IsCompleted);

        releasePreparation.TrySetResult();
        _ = await start;
        await Assert.ThrowsAsync<InvalidOperationException>(() => save);
        Assert.Equal("server-port=25565\n", await File.ReadAllTextAsync(fixture.PropertiesPath));
    }

    [Fact]
    public async Task Read_WaitsForInFlightStartPreparation_ThenReadsWhileSessionIsActive()
    {
        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await PropertiesFixture.CreateAsync(new ServerProcessManagerOptions
        {
            ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
            PrepareStartWithContextAsync = async (_, _, cancellationToken) =>
            {
                preparationEntered.TrySetResult();
                await releasePreparation.Task.WaitAsync(cancellationToken);
            },
        });
        await File.WriteAllTextAsync(fixture.PropertiesPath, "server-port=25565\n");
        var instance = new ServerInstance
        {
            Id = fixture.Registration.Id,
            Name = fixture.Registration.Name,
            DirectoryPath = fixture.ServerDirectory,
            JavaExecutablePath = fixture.JavaPath,
            ServerJarPath = "server.jar",
            CoreType = CoreType.Paper,
        };
        var start = fixture.Processes.StartAsync(instance);
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var read = fixture.Manager.ReadAsync(fixture.Registration.Id);
        await Task.Delay(100);
        Assert.False(read.IsCompleted);

        releasePreparation.TrySetResult();
        _ = await start;
        var loaded = await read;
        Assert.True(loaded.Exists);
        Assert.Equal("server-port=25565\n", loaded.Text);
    }

    private sealed class PropertiesFixture : IAsyncDisposable
    {
        private PropertiesFixture(
            ProductDataLayout layout,
            ProductServerRegistry registry,
            ProductServerRegistration registration,
            ServerProcessManager processes,
            Func<Guid, int, CoreType, bool, CancellationToken, Task<ProductServerRegistration>>?
                updateLaunchConfigurationAsync)
        {
            Layout = layout;
            Registry = registry;
            Registration = registration;
            Processes = processes;
            ServerDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
            JavaPath = Path.Combine(
                layout.Runtimes,
                registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
            PropertiesPath = Path.Combine(ServerDirectory, "server.properties");
            Manager = updateLaunchConfigurationAsync is null
                ? new ProductServerPropertiesManager(
                    layout,
                    registry,
                    new ServerPropertiesPortService(),
                    processes)
                : new ProductServerPropertiesManager(
                    layout,
                    registry,
                    new ServerPropertiesPortService(),
                    processes,
                    updateLaunchConfigurationAsync);
        }

        public ProductDataLayout Layout { get; }
        public ProductServerRegistry Registry { get; }
        public ProductServerRegistration Registration { get; }
        public ServerProcessManager Processes { get; }
        public ProductServerPropertiesManager Manager { get; }
        public string ServerDirectory { get; }
        public string JavaPath { get; }
        public string PropertiesPath { get; }

        public static async Task<PropertiesFixture> CreateAsync(
            ServerProcessManagerOptions? options = null,
            Func<Guid, int, CoreType, bool, CancellationToken, Task<ProductServerRegistration>>?
                updateLaunchConfigurationAsync = null)
        {
            var layout = ProductServerRegistryTests.CreateLayout();
            var registry = new ProductServerRegistry(layout);
            await registry.LoadAsync();
            var registration = ProductServerRegistryTests.Registration();
            var serverDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
            var javaPath = Path.Combine(
                layout.Runtimes,
                registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(serverDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
            await File.WriteAllBytesAsync(Path.Combine(serverDirectory, "server.jar"), []);
            await File.WriteAllBytesAsync(javaPath, []);
            await registry.UpsertAsync(registration);
            var processes = new ServerProcessManager(
                options ?? new ServerProcessManagerOptions
                {
                    ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                },
                new ProductServerTestProcessFactory());
            return new PropertiesFixture(
                layout,
                registry,
                registration,
                processes,
                updateLaunchConfigurationAsync);
        }

        public async ValueTask DisposeAsync()
        {
            await Processes.DisposeAsync();
            try
            {
                Directory.Delete(Layout.Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
