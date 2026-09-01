using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerAdministrationIpcTests
{
    [Fact]
    public async Task Registration_ReadAndStoppedUpdate_RoundTripWithoutAbsolutePaths()
    {
        await using var fixture = await Fixture.CreateAsync();
        var read = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerRegistrationMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);

        Assert.True(read.Success);
        Assert.Equal(fixture.Registration.Id, read.Registration!.Id);
        Assert.Equal(fixture.Registration.Name, read.Registration.Name);
        Assert.Equal(fixture.Registration.JvmArguments, read.Registration.JvmArguments);
        Assert.False(Path.IsPathFullyQualified(read.Registration.ServerDirectory));
        Assert.False(Path.IsPathFullyQualified(read.Registration.JavaRuntimePath));

        var changed = fixture.Registration with
        {
            Name = "Edited by desktop",
            Port = 25570,
            MinimumMemoryMb = 1536,
            MaximumMemoryMb = 3072,
            AutoRestart = true,
        };
        var update = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerSettingsUpdateMethod) with
            {
                ServerId = changed.Id,
                ServerSettings = new ProductServerSettingsUpdateRequest(
                    changed.Name,
                    changed.MinimumMemoryMb,
                    changed.MaximumMemoryMb,
                    changed.Port,
                    changed.AutoRestart),
            },
            default);

        Assert.True(update.Success);
        Assert.Equal(changed.Name, update.Server!.Server.Name);
        var stored = fixture.Runtime.GetRegistration(changed.Id);
        Assert.Equal(changed.Name, stored.Name);
        Assert.Equal(changed.Port, stored.Port);
        Assert.Equal(changed.MinimumMemoryMb, stored.MinimumMemoryMb);
        Assert.Equal(changed.MaximumMemoryMb, stored.MaximumMemoryMb);
        Assert.Equal(changed.AutoRestart, stored.AutoRestart);
        Assert.Equal(fixture.Registration.ServerDirectory, stored.ServerDirectory);
        Assert.Equal(fixture.Registration.JavaRuntimePath, stored.JavaRuntimePath);
        Assert.Equal(fixture.Registration.ServerJarPath, stored.ServerJarPath);
        Assert.Equal(fixture.Registration.JvmArguments, stored.JvmArguments);
        Assert.Equal(fixture.Registration.ModpackVersionId, stored.ModpackVersionId);
    }

    [Fact]
    public async Task BackupIpc_IsPagedOpaqueAndRestoreRejectsUnknownId()
    {
        await using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.ServerDirectory, "world.dat"), "ipc-world");

        var create = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerBackupCreateMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);
        var list = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerBackupListMethod) with
            {
                ServerId = fixture.Registration.Id,
                ListOffset = 0,
                ListLimit = 50,
            },
            default);
        var unknown = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerBackupRestoreMethod) with
            {
                ServerId = fixture.Registration.Id,
                BackupId = new string('b', 64),
            },
            default);

        Assert.True(create.Success);
        Assert.Equal(64, create.BackupMutation!.Backup.BackupId.Length);
        Assert.True(list.Success);
        Assert.Single(list.BackupPage!.Backups);
        Assert.False(unknown.Success);
        Assert.Equal("server.not_found", unknown.Error!.Code);
        Assert.Equal("ipc-world", File.ReadAllText(Path.Combine(fixture.ServerDirectory, "world.dat")));
    }

    [Fact]
    public async Task RunningServer_FailsClosedForUpdateRemoveAndBackup()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Runtime.StartAsync(fixture.Registration.Id);

        var update = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerSettingsUpdateMethod) with
            {
                ServerId = fixture.Registration.Id,
                ServerSettings = new ProductServerSettingsUpdateRequest(
                    "must-not-save",
                    fixture.Registration.MinimumMemoryMb,
                    fixture.Registration.MaximumMemoryMb,
                    fixture.Registration.Port,
                    fixture.Registration.AutoRestart),
            },
            default);
        var remove = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerRemoveMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);
        var backup = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerBackupCreateMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);

        Assert.Equal("server.operation_rejected", update.Error!.Code);
        Assert.Equal("server.operation_rejected", remove.Error!.Code);
        Assert.Equal("server.operation_rejected", backup.Error!.Code);
        Assert.Equal(fixture.Registration.Name, fixture.Runtime.GetRegistration(fixture.Registration.Id).Name);
    }

    [Fact]
    public async Task StoppedRemove_UnregistersOnlyAndPreservesTheManagedTree()
    {
        await using var fixture = await Fixture.CreateAsync();
        var world = Path.Combine(fixture.ServerDirectory, "world.dat");
        File.WriteAllText(world, "must-survive-unregister");

        var response = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerRemoveMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);

        Assert.True(response.Success);
        Assert.Throws<KeyNotFoundException>(
            () => fixture.Runtime.GetRegistration(fixture.Registration.Id));
        Assert.True(Directory.Exists(fixture.ServerDirectory));
        Assert.Equal("must-survive-unregister", File.ReadAllText(world));
    }

    [Fact]
    public async Task LocalFileAdministration_ReturnsOwnedDirectoryAndDeletesTreeBeforeRegistry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var world = Path.Combine(fixture.ServerDirectory, "world.dat");
        File.WriteAllText(world, "delete-through-ipc");

        var directory = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerDirectoryMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);
        var administration = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerAdministrationMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);
        var deletion = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerDeleteMethod) with
            {
                ServerId = fixture.Registration.Id,
            },
            default);

        Assert.True(directory.Success);
        Assert.Equal(fixture.Registration.Id, directory.ServerDirectory!.ServerId);
        Assert.Equal(fixture.ServerDirectory, directory.ServerDirectory.DirectoryPath);
        Assert.True(directory.ServerDirectory.Exists);
        Assert.True(administration.Success);
        Assert.Equal(fixture.Registration.Id, administration.ServerAdministration!.ServerId);
        Assert.DoesNotContain(
            fixture.ServerDirectory,
            System.Text.Json.JsonSerializer.Serialize(administration.ServerAdministration),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(deletion.Success);
        Assert.True(deletion.ServerDeletion!.Deleted);
        Assert.False(Directory.Exists(fixture.ServerDirectory));
        Assert.Throws<KeyNotFoundException>(
            () => fixture.Runtime.GetRegistration(fixture.Registration.Id));
    }

    [Fact]
    public async Task ServerProperties_Api17ReadsAndUpdatesServiceOwnedDocument()
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = Path.Combine(fixture.ServerDirectory, "server.properties");
        await File.WriteAllTextAsync(path, "server-port=25565\n");

        var read = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerPropertiesReadMethod) with
            {
                ServerId = fixture.Registration.Id,
                ClientMinimumApiVersion = ProductApiProtocol.ServerPropertiesEditorVersion,
            },
            default);
        var update = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerPropertiesUpdateMethod) with
            {
                ServerId = fixture.Registration.Id,
                ClientMinimumApiVersion = ProductApiProtocol.ServerPropertiesEditorVersion,
                ServerPropertiesUpdate = new ProductServerPropertiesUpdateRequest(
                    "server-port=25570\n",
                    read.ServerProperties!.RevisionSha256),
            },
            default);

        Assert.True(read.Success);
        Assert.Equal("server-port=25565\n", read.ServerProperties!.Text);
        Assert.True(update.Success);
        Assert.Equal("server-port=25570\n", update.ServerProperties!.Text);
        Assert.Equal("server-port=25570\n", await File.ReadAllTextAsync(path));
        Assert.Equal(25570, fixture.Runtime.GetRegistration(fixture.Registration.Id).Port);
    }

    [Theory]
    [InlineData(ProductIpcProtocol.ServerPropertiesReadMethod)]
    [InlineData(ProductIpcProtocol.ServerPropertiesUpdateMethod)]
    public async Task ServerProperties_RequiresNegotiatedApiVersionOneSeven(string method)
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = await fixture.Processor.ProcessAsync(
            Request(method) with
            {
                ServerId = fixture.Registration.Id,
                ClientMaximumApiVersion = ProductApiProtocol.MinecraftEulaConsentVersion,
                ServerPropertiesUpdate = method == ProductIpcProtocol.ServerPropertiesUpdateMethod
                    ? new ProductServerPropertiesUpdateRequest(
                        string.Empty,
                        ProductServerPropertiesContract.MissingRevision)
                    : null,
            },
            default);

        Assert.False(response.Success);
        Assert.Equal("protocol.method_version_unsupported", response.Error!.Code);
    }

    [Theory]
    [InlineData(ProductIpcProtocol.ServerDirectoryMethod)]
    [InlineData(ProductIpcProtocol.ServerAdministrationMethod)]
    [InlineData(ProductIpcProtocol.ServerDeleteMethod)]
    public async Task LocalFileAdministration_RequiresNegotiatedApiVersionOneFive(string method)
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = await fixture.Processor.ProcessAsync(
            Request(method) with
            {
                ServerId = fixture.Registration.Id,
                ClientMaximumApiVersion = new ProductApiVersion(1, 4),
            },
            default);

        Assert.False(response.Success);
        Assert.Equal("protocol.method_version_unsupported", response.Error!.Code);
        Assert.True(Directory.Exists(fixture.ServerDirectory));
        Assert.Equal(
            fixture.Registration.Id,
            fixture.Runtime.GetRegistration(fixture.Registration.Id).Id);
    }

    [Theory]
    [InlineData(ProductIpcProtocol.ServerRegistrationMethod)]
    [InlineData(ProductIpcProtocol.ServerSettingsUpdateMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupListMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupCreateMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupRestoreMethod)]
    public async Task AdministrationMethods_RequireNegotiatedApiVersionOneThree(string method)
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = await fixture.Processor.ProcessAsync(
            Request(method) with
            {
                ServerId = fixture.Registration.Id,
                ClientMaximumApiVersion = new ProductApiVersion(1, 2),
                ServerSettings = method == ProductIpcProtocol.ServerSettingsUpdateMethod
                    ? new ProductServerSettingsUpdateRequest("valid", 1024, 2048, 25565, false)
                    : null,
                BackupId = method == ProductIpcProtocol.ServerBackupRestoreMethod
                    ? new string('a', 64)
                    : null,
            },
            default);

        Assert.False(response.Success);
        Assert.Equal("protocol.method_version_unsupported", response.Error!.Code);
    }

    private static ProductIpcRequest Request(string method) => new(
        ProductIpcProtocol.CurrentSchemaVersion,
        Guid.NewGuid(),
        method,
        ProductApiProtocol.MinimumSupportedVersion,
        ProductApiProtocol.CurrentVersion);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            ProductDataLayout layout,
            ProductServerRegistration registration,
            ProductServerRuntime runtime,
            ProductIpcMessageProcessor processor)
        {
            Layout = layout;
            Registration = registration;
            Runtime = runtime;
            Processor = processor;
            ServerDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
        }

        public ProductDataLayout Layout { get; }
        public ProductServerRegistration Registration { get; }
        public ProductServerRuntime Runtime { get; }
        public ProductIpcMessageProcessor Processor { get; }
        public string ServerDirectory { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var layout = ProductServerRegistryTests.CreateLayout();
            var registration = ProductServerRegistryTests.Registration();
            var serverDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
            var javaPath = Path.Combine(
                layout.Runtimes,
                registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(serverDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
            File.WriteAllText(Path.Combine(serverDirectory, "server.jar"), "core");
            File.WriteAllText(javaPath, "java");
            var registry = new ProductServerRegistry(layout);
            await registry.LoadAsync();
            await registry.UpsertAsync(registration);
            var processes = new ServerProcessManager(
                new ServerProcessManagerOptions
                {
                    ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                    GracefulStopTimeout = TimeSpan.FromMilliseconds(250),
                    ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(250),
                    MonitorDrainTimeout = TimeSpan.FromMilliseconds(250),
                },
                new ProductServerTestProcessFactory());
            var runtime = new ProductServerRuntime(
                registry,
                layout,
                processes,
                new ProductDesiredRunIntentStore(layout));
            var backups = new ProductServerBackupManager(layout, runtime, new BackupService());
            var administration = new ProductServerAdministrationReader(layout, registry, TimeProvider.System);
            var properties = new ProductServerPropertiesManager(
                layout,
                registry,
                new ServerPropertiesPortService(),
                processes);
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
                backups,
                administration: administration,
                properties: properties);
            return new Fixture(layout, registration, runtime, processor);
        }

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
}
