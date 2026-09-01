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
            MemoryAllocationMode = ProductServerMemoryAllocationMode.Automatic,
            SeparateDiagnosticOutput = false,
            EnableHangWatchdog = true,
            WatchdogCheckIntervalSeconds = 45,
            WatchdogProbeTimeoutSeconds = 9,
            WatchdogFailureThreshold = 4,
            WatchdogStartupGraceSeconds = 240,
            EnableAutomaticRecoveryPoints = true,
            RecoveryPointIntervalMinutes = 60,
            RecoveryPointRetentionCount = 5,
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
                    changed.AutoRestart)
                {
                    MemoryAllocationMode = changed.MemoryAllocationMode,
                    SeparateDiagnosticOutput = changed.SeparateDiagnosticOutput,
                    EnableHangWatchdog = changed.EnableHangWatchdog,
                    WatchdogCheckIntervalSeconds = changed.WatchdogCheckIntervalSeconds,
                    WatchdogProbeTimeoutSeconds = changed.WatchdogProbeTimeoutSeconds,
                    WatchdogFailureThreshold = changed.WatchdogFailureThreshold,
                    WatchdogStartupGraceSeconds = changed.WatchdogStartupGraceSeconds,
                    EnableAutomaticRecoveryPoints = changed.EnableAutomaticRecoveryPoints,
                    RecoveryPointIntervalMinutes = changed.RecoveryPointIntervalMinutes,
                    RecoveryPointRetentionCount = changed.RecoveryPointRetentionCount,
                },
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
        Assert.Equal(changed.MemoryAllocationMode, stored.MemoryAllocationMode);
        Assert.Equal(changed.SeparateDiagnosticOutput, stored.SeparateDiagnosticOutput);
        Assert.Equal(changed.EnableHangWatchdog, stored.EnableHangWatchdog);
        Assert.Equal(changed.WatchdogCheckIntervalSeconds, stored.WatchdogCheckIntervalSeconds);
        Assert.Equal(changed.WatchdogProbeTimeoutSeconds, stored.WatchdogProbeTimeoutSeconds);
        Assert.Equal(changed.WatchdogFailureThreshold, stored.WatchdogFailureThreshold);
        Assert.Equal(changed.WatchdogStartupGraceSeconds, stored.WatchdogStartupGraceSeconds);
        Assert.Equal(
            changed.EnableAutomaticRecoveryPoints,
            stored.EnableAutomaticRecoveryPoints);
        Assert.Equal(changed.RecoveryPointIntervalMinutes, stored.RecoveryPointIntervalMinutes);
        Assert.Equal(changed.RecoveryPointRetentionCount, stored.RecoveryPointRetentionCount);
        Assert.Equal(fixture.Registration.ServerDirectory, stored.ServerDirectory);
        Assert.Equal(fixture.Registration.JavaRuntimePath, stored.JavaRuntimePath);
        Assert.Equal(fixture.Registration.ServerJarPath, stored.ServerJarPath);
        Assert.Equal(fixture.Registration.JvmArguments, stored.JvmArguments);
        Assert.Equal(fixture.Registration.ModpackVersionId, stored.ModpackVersionId);
    }

    [Fact]
    public async Task Api17LegacyUpdate_PreservesNewSettings_AndCannotSubmitApi18Snapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var authoritative = fixture.Registration with
        {
            MemoryAllocationMode = ProductServerMemoryAllocationMode.UseManagerDefault,
            SeparateDiagnosticOutput = false,
            EnableHangWatchdog = true,
            WatchdogCheckIntervalSeconds = 45,
            WatchdogProbeTimeoutSeconds = 9,
            WatchdogFailureThreshold = 4,
            WatchdogStartupGraceSeconds = 240,
            EnableAutomaticRecoveryPoints = true,
            RecoveryPointIntervalMinutes = 60,
            RecoveryPointRetentionCount = 5,
        };
        await fixture.Runtime.UpsertAsync(authoritative);
        var legacyRequest = Request(ProductIpcProtocol.ServerSettingsUpdateMethod) with
        {
            ServerId = authoritative.Id,
            ClientMaximumApiVersion = ProductApiProtocol.ServerPropertiesEditorVersion,
            ServerSettings = new ProductServerSettingsUpdateRequest(
                "Legacy desktop rename",
                authoritative.MinimumMemoryMb,
                authoritative.MaximumMemoryMb,
                authoritative.Port,
                authoritative.AutoRestart),
        };

        var legacyResult = await fixture.Processor.ProcessAsync(legacyRequest, default);
        var rejected = await fixture.Processor.ProcessAsync(
            legacyRequest with
            {
                ServerSettings = legacyRequest.ServerSettings! with
                {
                    MemoryAllocationMode = ProductServerMemoryAllocationMode.Manual,
                    SeparateDiagnosticOutput = true,
                    EnableHangWatchdog = false,
                    WatchdogCheckIntervalSeconds = 30,
                    WatchdogProbeTimeoutSeconds = 8,
                    WatchdogFailureThreshold = 3,
                    WatchdogStartupGraceSeconds = 180,
                    EnableAutomaticRecoveryPoints = false,
                    RecoveryPointIntervalMinutes = 30,
                    RecoveryPointRetentionCount = 3,
                },
            },
            default);

        Assert.True(legacyResult.Success);
        Assert.Equal("protocol.field_version_unsupported", rejected.Error!.Code);
        var stored = fixture.Runtime.GetRegistration(authoritative.Id);
        Assert.Equal("Legacy desktop rename", stored.Name);
        Assert.Equal(authoritative.MemoryAllocationMode, stored.MemoryAllocationMode);
        Assert.Equal(authoritative.SeparateDiagnosticOutput, stored.SeparateDiagnosticOutput);
        Assert.Equal(authoritative.EnableHangWatchdog, stored.EnableHangWatchdog);
        Assert.Equal(authoritative.WatchdogCheckIntervalSeconds, stored.WatchdogCheckIntervalSeconds);
        Assert.Equal(authoritative.WatchdogProbeTimeoutSeconds, stored.WatchdogProbeTimeoutSeconds);
        Assert.Equal(authoritative.WatchdogFailureThreshold, stored.WatchdogFailureThreshold);
        Assert.Equal(authoritative.WatchdogStartupGraceSeconds, stored.WatchdogStartupGraceSeconds);
        Assert.Equal(
            authoritative.EnableAutomaticRecoveryPoints,
            stored.EnableAutomaticRecoveryPoints);
        Assert.Equal(authoritative.RecoveryPointIntervalMinutes, stored.RecoveryPointIntervalMinutes);
        Assert.Equal(authoritative.RecoveryPointRetentionCount, stored.RecoveryPointRetentionCount);
    }

    [Theory]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.MemoryAllocationMode))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.SeparateDiagnosticOutput))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.EnableHangWatchdog))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.WatchdogCheckIntervalSeconds))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.WatchdogProbeTimeoutSeconds))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.WatchdogFailureThreshold))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.WatchdogStartupGraceSeconds))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.EnableAutomaticRecoveryPoints))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.RecoveryPointIntervalMinutes))]
    [InlineData(nameof(ProductServerSettingsUpdateRequest.RecoveryPointRetentionCount))]
    public async Task Api17_EachIndividualApi18Field_IsRejectedByFieldVersionGate(string field)
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = await fixture.Processor.ProcessAsync(
            Request(ProductIpcProtocol.ServerSettingsUpdateMethod) with
            {
                ServerId = fixture.Registration.Id,
                ClientMaximumApiVersion = ProductApiProtocol.ServerPropertiesEditorVersion,
                ServerSettings = WithSingleServiceInstanceSetting(field),
            },
            default);

        Assert.False(response.Success);
        Assert.Equal("protocol.field_version_unsupported", response.Error!.Code);
    }

    [Theory]
    [InlineData(MinecraftServerManager.Core.Models.ServerState.Stopped, true)]
    [InlineData(MinecraftServerManager.Core.Models.ServerState.Starting, false)]
    [InlineData(MinecraftServerManager.Core.Models.ServerState.Running, false)]
    [InlineData(MinecraftServerManager.Core.Models.ServerState.Stopping, false)]
    [InlineData(MinecraftServerManager.Core.Models.ServerState.Crashed, false)]
    [InlineData(MinecraftServerManager.Core.Models.ServerState.Faulted, false)]
    public void SettingsUpdateStatePolicy_AllowsOnlyExactStopped(
        MinecraftServerManager.Core.Models.ServerState state,
        bool allowed)
    {
        if (allowed)
        {
            ProductServerRuntime.EnsureSettingsUpdateState(state);
            return;
        }

        Assert.Throws<InvalidOperationException>(
            () => ProductServerRuntime.EnsureSettingsUpdateState(state));
    }

    [Fact]
    public async Task DirectUpdate_RejectsCrashedAndFaultedSlotsWithoutPersisting()
    {
        await using var crashed = await Fixture.CreateAsync();
        await crashed.Runtime.StartAsync(crashed.Registration.Id);
        Assert.Single(crashed.ProcessFactory.Processes).Complete(17);
        await WaitUntilAsync(
            () => crashed.Runtime.GetStatus(crashed.Registration.Id).Server.State ==
                  ProductServerState.Crashed);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => crashed.Runtime.UpdateSettingsAsync(
                crashed.Registration.Id,
                CompleteServiceSettings("must-not-save-crashed", crashed.Registration)));
        Assert.Equal(
            crashed.Registration.Name,
            crashed.Runtime.GetRegistration(crashed.Registration.Id).Name);

        await using var faulted = await Fixture.CreateAsync();
        faulted.ProcessFactory.StartResults.Enqueue(false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => faulted.Runtime.StartAsync(faulted.Registration.Id));
        Assert.Equal(
            ProductServerState.Faulted,
            faulted.Runtime.GetStatus(faulted.Registration.Id).Server.State);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => faulted.Runtime.UpdateSettingsAsync(
                faulted.Registration.Id,
                CompleteServiceSettings("must-not-save-faulted", faulted.Registration)));
        Assert.Equal(
            faulted.Registration.Name,
            faulted.Runtime.GetRegistration(faulted.Registration.Id).Name);
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

    private static ProductServerSettingsUpdateRequest WithSingleServiceInstanceSetting(string field)
    {
        var settings = new ProductServerSettingsUpdateRequest("valid", 1024, 2048, 25565, false);
        return field switch
        {
            nameof(ProductServerSettingsUpdateRequest.MemoryAllocationMode) => settings with
            {
                MemoryAllocationMode = ProductServerMemoryAllocationMode.Manual,
            },
            nameof(ProductServerSettingsUpdateRequest.SeparateDiagnosticOutput) => settings with
            {
                SeparateDiagnosticOutput = true,
            },
            nameof(ProductServerSettingsUpdateRequest.EnableHangWatchdog) => settings with
            {
                EnableHangWatchdog = true,
            },
            nameof(ProductServerSettingsUpdateRequest.WatchdogCheckIntervalSeconds) => settings with
            {
                WatchdogCheckIntervalSeconds = 30,
            },
            nameof(ProductServerSettingsUpdateRequest.WatchdogProbeTimeoutSeconds) => settings with
            {
                WatchdogProbeTimeoutSeconds = 8,
            },
            nameof(ProductServerSettingsUpdateRequest.WatchdogFailureThreshold) => settings with
            {
                WatchdogFailureThreshold = 3,
            },
            nameof(ProductServerSettingsUpdateRequest.WatchdogStartupGraceSeconds) => settings with
            {
                WatchdogStartupGraceSeconds = 180,
            },
            nameof(ProductServerSettingsUpdateRequest.EnableAutomaticRecoveryPoints) => settings with
            {
                EnableAutomaticRecoveryPoints = true,
            },
            nameof(ProductServerSettingsUpdateRequest.RecoveryPointIntervalMinutes) => settings with
            {
                RecoveryPointIntervalMinutes = 30,
            },
            nameof(ProductServerSettingsUpdateRequest.RecoveryPointRetentionCount) => settings with
            {
                RecoveryPointRetentionCount = 3,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    private static ProductServerSettingsUpdateRequest LegacySettings(
        string name,
        ProductServerRegistration registration)
        => new(
            name,
            registration.MinimumMemoryMb,
            registration.MaximumMemoryMb,
            registration.Port,
            registration.AutoRestart);

    private static ProductServerSettingsUpdateRequest CompleteServiceSettings(
        string name,
        ProductServerRegistration registration)
        => LegacySettings(name, registration) with
        {
            MemoryAllocationMode = registration.MemoryAllocationMode,
            SeparateDiagnosticOutput = registration.SeparateDiagnosticOutput,
            EnableHangWatchdog = registration.EnableHangWatchdog,
            WatchdogCheckIntervalSeconds = registration.WatchdogCheckIntervalSeconds,
            WatchdogProbeTimeoutSeconds = registration.WatchdogProbeTimeoutSeconds,
            WatchdogFailureThreshold = registration.WatchdogFailureThreshold,
            WatchdogStartupGraceSeconds = registration.WatchdogStartupGraceSeconds,
            EnableAutomaticRecoveryPoints = registration.EnableAutomaticRecoveryPoints,
            RecoveryPointIntervalMinutes = registration.RecoveryPointIntervalMinutes,
            RecoveryPointRetentionCount = registration.RecoveryPointRetentionCount,
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("Condition was not reached before the test timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            ProductDataLayout layout,
            ProductServerRegistration registration,
            ProductServerRuntime runtime,
            ProductIpcMessageProcessor processor,
            ProductServerTestProcessFactory processFactory)
        {
            Layout = layout;
            Registration = registration;
            Runtime = runtime;
            Processor = processor;
            ProcessFactory = processFactory;
            ServerDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
        }

        public ProductDataLayout Layout { get; }
        public ProductServerRegistration Registration { get; }
        public ProductServerRuntime Runtime { get; }
        public ProductIpcMessageProcessor Processor { get; }
        public ProductServerTestProcessFactory ProcessFactory { get; }
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
            var processFactory = new ProductServerTestProcessFactory();
            var processes = new ServerProcessManager(
                new ServerProcessManagerOptions
                {
                    ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                    GracefulStopTimeout = TimeSpan.FromMilliseconds(250),
                    ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(250),
                    MonitorDrainTimeout = TimeSpan.FromMilliseconds(250),
                },
                processFactory);
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
            return new Fixture(layout, registration, runtime, processor, processFactory);
        }

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
}
