using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerModpackUpdateCoordinatorTests
{
    [Fact]
    public async Task HealthyFirstLaunch_PreservesWorldBacksUpDataWithoutCoreAndAcknowledgesOnStop()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var terminalEvent = new TaskCompletionSource<ProductServerModpackUpdateStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.TerminalStatePersisted += (_, status) => terminalEvent.TrySetResult(status);
        var update = await fixture.StageAndCommitAsync();
        var awaiting = await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.AwaitingHealth);

        Assert.Equal("live-world", await fixture.ReadLiveAsync("world/level.dat"));
        Assert.Equal("live-player", await fixture.ReadLiveAsync("world/playerdata/player.dat"));
        Assert.Equal("new-mod", await fixture.ReadLiveAsync("mods/new.jar"));
        Assert.Equal("new-config", await fixture.ReadLiveAsync("config/pack.toml"));
        Assert.False(File.Exists(fixture.LivePath("mods/old.jar")));
        Assert.NotNull(awaiting.BackupArchivePath);
        Assert.True(File.Exists(awaiting.BackupArchivePath));
        using (var archive = ZipFile.OpenRead(awaiting.BackupArchivePath!))
        {
            var entries = archive.Entries.Select(static entry => entry.FullName).ToArray();
            Assert.Contains("mods/old.jar", entries);
            Assert.Contains("config/pack.toml", entries);
            Assert.Contains("world/level.dat", entries);
            Assert.Contains("world/playerdata/player.dat", entries);
            Assert.DoesNotContain("old-core.jar", entries);
        }

        var registration = fixture.Registry.GetAll().Single();
        Assert.Equal("v2", registration.ModpackVersionId);
        Assert.Equal("new-core.jar", registration.ServerJarPath);

        await fixture.Runtime.StartAsync(fixture.ServerId);
        Assert.Single(fixture.Factory.Processes).EmitOutput(
            "[Server thread/INFO]: Done (0.315s)! For help, type \"help\"");
        await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.HealthyAwaitingStop);

        await fixture.Runtime.StopAsync(fixture.ServerId);
        await fixture.WaitForStateAsync(update.UpdateId, ProductServerModpackUpdateState.Completed);
        var announced = await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(fixture.TransactionArtifacts());
        Assert.Equal("live-world", await fixture.ReadLiveAsync("world/level.dat"));
        Assert.Equal(update.UpdateId, announced.UpdateId);
        Assert.Equal(ProductServerModpackUpdateState.Completed, announced.State);
    }

    [Fact]
    public async Task CrashBeforeHealth_RollsBackFilesystemAndRegistrationFailClosed()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var update = await fixture.StageAndCommitAsync();
        await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.AwaitingHealth);

        await fixture.Runtime.StartAsync(fixture.ServerId);
        Assert.Single(fixture.Factory.Processes).Complete(17);
        var rolledBack = await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.RolledBack);

        Assert.Equal("modpack_update.health_failed", rolledBack.ErrorCode);
        var registration = fixture.Registry.GetAll().Single();
        Assert.Equal("v1", registration.ModpackVersionId);
        Assert.Equal("old-core.jar", registration.ServerJarPath);
        Assert.Equal("old-mod", await fixture.ReadLiveAsync("mods/old.jar"));
        Assert.Equal("old-config", await fixture.ReadLiveAsync("config/pack.toml"));
        Assert.Equal("live-world", await fixture.ReadLiveAsync("world/level.dat"));
        Assert.False(File.Exists(fixture.LivePath("new-core.jar")));
        Assert.Empty(fixture.TransactionArtifacts());

        // A completed rollback clears only the maintenance block. Desired intent remains false;
        // an explicit operator start can safely launch the restored version.
        var retry = await fixture.Runtime.StartAsync(fixture.ServerId);
        Assert.True(retry.Changed);
    }

    [Fact]
    public async Task RunningButNeverHealthy_StopsAndRollsBackAtDurableDeadline()
    {
        await using var fixture = await UpdateFixture.CreateAsync(
            healthValidationTimeout: TimeSpan.FromMilliseconds(250));
        var update = await fixture.StageAndCommitAsync();
        await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.AwaitingHealth);

        await fixture.Runtime.StartAsync(fixture.ServerId);
        var rolledBack = await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.RolledBack);

        Assert.Equal("modpack_update.health_timeout", rolledBack.ErrorCode);
        Assert.Equal(ProductServerState.Stopped, fixture.Runtime.GetStatus(fixture.ServerId).Server.State);
        Assert.Equal("v1", fixture.Registry.GetAll().Single().ModpackVersionId);
        Assert.Equal("old-mod", await fixture.ReadLiveAsync("mods/old.jar"));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task CoordinatorRestart_ResumesSamePersistedHealthDeadline()
    {
        await using var fixture = await UpdateFixture.CreateAsync(
            healthValidationTimeout: TimeSpan.FromSeconds(2));
        var update = await fixture.StageAndCommitAsync();
        await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.AwaitingHealth);
        await fixture.Runtime.StartAsync(fixture.ServerId);

        var before = await fixture.WaitForPersistedHealthDeadlineAsync(update.UpdateId);
        await Task.Delay(250);
        await fixture.ReplaceCoordinatorAfterProcessLossAsync();
        var after = await fixture.WaitForPersistedHealthDeadlineAsync(update.UpdateId);

        Assert.Equal(before, after);
        await fixture.Runtime.StartAsync(fixture.ServerId);
        var rolledBack = await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.RolledBack);
        Assert.Equal("modpack_update.health_timeout", rolledBack.ErrorCode);
        Assert.Equal("v1", fixture.Registry.GetAll().Single().ModpackVersionId);
    }

    [Fact]
    public async Task ManualStopBeforeHealth_RetainsPendingTransactionForNextRealLaunch()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var update = await fixture.StageAndCommitAsync();
        await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.AwaitingHealth);

        await fixture.Runtime.StartAsync(fixture.ServerId);
        await fixture.Runtime.SendCommandAsync(fixture.ServerId, "stop");
        await fixture.WaitForRuntimeStateAsync(ProductServerState.Stopped);
        var retained = fixture.Coordinator.GetStatus(update.UpdateId);
        Assert.Equal(ProductServerModpackUpdateState.AwaitingHealth, retained.State);
        Assert.Null(retained.ErrorCode);
        Assert.Equal("v2", fixture.Registry.GetAll().Single().ModpackVersionId);

        await fixture.Runtime.StartAsync(fixture.ServerId);
        fixture.Factory.Processes.Last().EmitOutput("Done (1s)! For help");
        await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.HealthyAwaitingStop);
        await fixture.Runtime.StopAsync(fixture.ServerId);
        await fixture.WaitForStateAsync(update.UpdateId, ProductServerModpackUpdateState.Completed);
    }

    [Fact]
    public async Task RestartRecovery_UsesDurableCoreJournalAndStillRollsBackOnFailedLaunch()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var update = await fixture.StageAndCommitAsync();
        await fixture.WaitForStateAsync(
            update.UpdateId,
            ProductServerModpackUpdateState.AwaitingHealth);

        await fixture.ReplaceCoordinatorAsync();
        var recovered = fixture.Coordinator.GetStatus(update.UpdateId);
        Assert.Equal(ProductServerModpackUpdateState.AwaitingHealth, recovered.State);
        Assert.Equal("v2", fixture.Registry.GetAll().Single().ModpackVersionId);

        await fixture.Runtime.StartAsync(fixture.ServerId);
        Assert.Single(fixture.Factory.Processes).Complete(1);
        await fixture.WaitForStateAsync(update.UpdateId, ProductServerModpackUpdateState.RolledBack);
        Assert.Equal("v1", fixture.Registry.GetAll().Single().ModpackVersionId);
        Assert.Equal("old-mod", await fixture.ReadLiveAsync("mods/old.jar"));
        Assert.Equal("live-world", await fixture.ReadLiveAsync("world/level.dat"));
    }

    [Fact]
    public async Task CandidateHashMismatch_FailsBeforeAnyLiveFilesystemMutation()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var staged = await fixture.StageAsync();
        await File.WriteAllTextAsync(
            Path.Combine(staged.StagingDirectory!, "candidate", "mods", "new.jar"),
            "tampered-after-manifest");

        await fixture.Coordinator.CommitAsync(
            staged.UpdateId,
            fixture.LastManifestSha256!);
        var failed = await fixture.WaitForStateAsync(
            staged.UpdateId,
            ProductServerModpackUpdateState.Failed);

        Assert.Equal("modpack_update.integrity_failed", failed.ErrorCode);
        Assert.Equal("old-mod", await fixture.ReadLiveAsync("mods/old.jar"));
        Assert.Equal("live-world", await fixture.ReadLiveAsync("world/level.dat"));
        Assert.Equal("v1", fixture.Registry.GetAll().Single().ModpackVersionId);
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task CandidateReparsePoint_IsRejectedWithoutReadingOrDeletingExternalTarget()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var staged = await fixture.StageAsync();
        var outside = Path.Combine(fixture.Layout.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.keep");
        await File.WriteAllTextAsync(sentinel, "outside must remain untouched");
        CreateDirectoryJunction(
            Path.Combine(staged.StagingDirectory!, "candidate", "linked"),
            outside);

        await fixture.Coordinator.CommitAsync(staged.UpdateId, fixture.LastManifestSha256!);
        var failed = await fixture.WaitForStateAsync(
            staged.UpdateId,
            ProductServerModpackUpdateState.Failed);

        Assert.Equal("modpack_update.integrity_failed", failed.ErrorCode);
        Assert.Equal("outside must remain untouched", await File.ReadAllTextAsync(sentinel));
        Assert.Equal("old-mod", await fixture.ReadLiveAsync("mods/old.jar"));
        Assert.Equal("v1", fixture.Registry.GetAll().Single().ModpackVersionId);
    }

    [Fact]
    public async Task TraversalManifest_IsRejectedAndStagingCanBeCancelled()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var staged = await fixture.Coordinator.BeginAsync(fixture.CreateBeginRequest());
        var manifest = new ProductServerModpackUpdateManifest(
            1,
            staged.UpdateId,
            [new ProductServerModpackUpdateManifestEntry(
                "../outside.jar",
                0,
                Convert.ToHexString(SHA256.HashData([]))) ]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await File.WriteAllBytesAsync(
            Path.Combine(staged.StagingDirectory!, ProductServerModpackUpdateCoordinator.ManifestFileName),
            bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.CommitAsync(
            staged.UpdateId,
            Convert.ToHexString(SHA256.HashData(bytes))));
        var cancelled = await fixture.Coordinator.CancelAsync(staged.UpdateId);

        Assert.Equal(ProductServerModpackUpdateState.Cancelled, cancelled.State);
        Assert.False(Directory.Exists(staged.StagingDirectory));
        Assert.Equal("old-mod", await fixture.ReadLiveAsync("mods/old.jar"));
    }

    [Fact]
    public async Task CancellationRacingScheduledWork_DurablyLeavesNoPartialLiveCommit()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var staged = await fixture.StageAsync();
        await fixture.Coordinator.CommitAsync(staged.UpdateId, fixture.LastManifestSha256!);

        await fixture.Coordinator.CancelAsync(staged.UpdateId);
        var terminal = await fixture.WaitForTerminalAsync(staged.UpdateId);

        Assert.Contains(
            terminal.State,
            new[]
            {
                ProductServerModpackUpdateState.Cancelled,
                ProductServerModpackUpdateState.RolledBack,
            });
        Assert.Equal("v1", fixture.Registry.GetAll().Single().ModpackVersionId);
        Assert.Equal("old-mod", await fixture.ReadLiveAsync("mods/old.jar"));
        Assert.Equal("live-world", await fixture.ReadLiveAsync("world/level.dat"));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RunningServer_IsRejectedBeforeStagingCapabilityIsCreated()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        await fixture.Runtime.StartAsync(fixture.ServerId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.BeginAsync(fixture.CreateBeginRequest()));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(fixture.Layout.Imports, "modpack-updates")));
    }

    [Fact]
    public async Task CurseForgeProvenance_IsAcceptedForServiceOwnedIterativeUpdate()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        await fixture.Registry.UpsertAsync(fixture.Registration with
        {
            ModpackProviderId = "curseforge",
            ModpackSource = ProductModpackSourceKind.CurseForge,
        });
        var request = fixture.CreateBeginRequest();
        request = request with
        {
            Target = request.Target with
            {
                ModpackProviderId = "curseforge",
                ModpackSource = ProductModpackSourceKind.CurseForge,
            },
        };

        var staged = await fixture.Coordinator.BeginAsync(request);

        Assert.Equal(ProductServerModpackUpdateState.Staging, staged.State);
        var cancelled = await fixture.Coordinator.CancelAsync(staged.UpdateId);
        Assert.Equal(ProductServerModpackUpdateState.Cancelled, cancelled.State);
    }

    [Fact]
    public async Task IpcMethods_AreApi13VersionedAndUnavailableServiceFailsClosed()
    {
        await using var fixture = await UpdateFixture.CreateAsync();
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        var processor = new ProductIpcMessageProcessor(
            state,
            fixture.Runtime,
            updates: null,
            imports: null,
            remoteWeb: null,
            remoteAccounts: null,
            remoteDevices: null,
            discordWebhook: null,
            notificationOutbox: null,
            backups: null,
            fixture.Coordinator);
        var request = Request(ProductIpcProtocol.ServerModpackUpdateBeginMethod) with
        {
            ModpackUpdateBegin = fixture.CreateBeginRequest(),
        };

        var oldApi = await processor.ProcessAsync(
            request with { ClientMaximumApiVersion = new ProductApiVersion(1, 2) },
            default);
        Assert.False(oldApi.Success);
        Assert.Equal("protocol.method_version_unsupported", oldApi.Error?.Code);

        var begun = await processor.ProcessAsync(request, default);
        Assert.True(begun.Success);
        Assert.Equal(ProductServerModpackUpdateState.Staging, begun.ModpackUpdate?.State);

        var unavailable = await new ProductIpcMessageProcessor(state, fixture.Runtime)
            .ProcessAsync(
                Request(ProductIpcProtocol.ServerModpackUpdateStatusMethod) with
                {
                    ModpackUpdateId = begun.ModpackUpdate!.UpdateId,
                },
                default);
        Assert.False(unavailable.Success);
        Assert.Equal("service.modpack_update_unavailable", unavailable.Error?.Code);
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
    };

    private sealed class UnhealthyProbe : IMinecraftStatusProbe
    {
        public Task<MinecraftStatusProbeResult> ProbeAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MinecraftStatusProbeResult(
                IsHealthy: false,
                Latency: TimeSpan.Zero,
                Error: "not ready"));
    }

    private sealed class UpdateFixture : IAsyncDisposable
    {
        private UpdateFixture(
            ProductDataLayout layout,
            ProductServerRegistry registry,
            ProductServerRuntime runtime,
            ProductServerTestProcessFactory factory,
            ProductServerRestartBlocker restartBlocker,
            ProductServerModpackUpdateCoordinator coordinator,
            ProductServerRegistration registration,
            TimeSpan healthValidationTimeout)
        {
            Layout = layout;
            Registry = registry;
            Runtime = runtime;
            Factory = factory;
            RestartBlocker = restartBlocker;
            Coordinator = coordinator;
            Registration = registration;
            HealthValidationTimeout = healthValidationTimeout;
        }

        public ProductDataLayout Layout { get; }
        public ProductServerRegistry Registry { get; }
        public ProductServerRuntime Runtime { get; }
        public ProductServerTestProcessFactory Factory { get; }
        public ProductServerRestartBlocker RestartBlocker { get; }
        public ProductServerModpackUpdateCoordinator Coordinator { get; private set; }
        public ProductServerRegistration Registration { get; }
        private TimeSpan HealthValidationTimeout { get; }
        public Guid ServerId => Registration.Id;
        public string? LastManifestSha256 { get; private set; }

        public static async Task<UpdateFixture> CreateAsync(
            TimeSpan? healthValidationTimeout = null)
        {
            var effectiveHealthTimeout = healthValidationTimeout ??
                                         ProductServerModpackUpdateCoordinator.DefaultHealthValidationTimeout;
            var layout = ProductServerRegistryTests.CreateLayout();
            var id = Guid.NewGuid();
            var registration = ProductServerRegistryTests.Registration(id) with
            {
                Name = "Pack",
                ServerDirectory = $"server-{id:N}",
                JavaRuntimePath = "java/bin/java.exe",
                ServerJarPath = "old-core.jar",
                CoreType = "Forge",
                MinecraftVersion = "1.20.1",
                StopCommand = "stop",
                ModpackProviderId = "builtin.modrinth",
                ModpackSource = ProductModpackSourceKind.Modrinth,
                ModpackProjectId = "project",
                ModpackVersionId = "v1",
                ModpackVersionName = "1.6.0",
            };
            var live = Path.Combine(layout.Servers, registration.ServerDirectory);
            var java = Path.Combine(
                layout.Runtimes,
                registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(Path.GetDirectoryName(java)!);
            await File.WriteAllTextAsync(java, "java");
            await WriteAsync(live, "old-core.jar", "old-core");
            await WriteAsync(live, "mods/old.jar", "old-mod");
            await WriteAsync(live, "config/pack.toml", "old-config");
            await WriteAsync(live, "libraries/old-lib.jar", "old-library");
            await WriteAsync(live, "world/level.dat", "live-world");
            await WriteAsync(live, "world/playerdata/player.dat", "live-player");
            await WriteAsync(live, "ops.json", "live-ops");
            await WriteAsync(live, "server.properties", "level-name=world\nserver-port=25565\n");
            await WriteAsync(live, "eula.txt", "eula=true");

            var registry = new ProductServerRegistry(layout);
            await registry.LoadAsync();
            await registry.UpsertAsync(registration);
            var factory = new ProductServerTestProcessFactory();
            var restartBlocker = new ProductServerRestartBlocker();
            var manager = new ServerProcessManager(
                new ServerProcessManagerOptions
                {
                    ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                    GracefulStopTimeout = TimeSpan.FromSeconds(1),
                    ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                    MonitorDrainTimeout = TimeSpan.FromSeconds(1),
                    ShouldAutoRestartAsync = (serverId, _) => Task.FromResult(
                        registry.TryGet(serverId, out var server) &&
                        server.AutoRestart &&
                        !restartBlocker.IsBlocked(serverId)),
                },
                factory);
            var runtime = new ProductServerRuntime(
                registry,
                layout,
                manager,
                new ProductDesiredRunIntentStore(layout),
                restartBlocker);
            var coordinator = CreateCoordinator(
                layout,
                registry,
                runtime,
                restartBlocker,
                effectiveHealthTimeout);
            await coordinator.InitializeAsync();
            return new UpdateFixture(
                layout,
                registry,
                runtime,
                factory,
                restartBlocker,
                coordinator,
                registration,
                effectiveHealthTimeout);
        }

        public ProductServerModpackUpdateBeginRequest CreateBeginRequest() => new(
            ServerId,
            "v1",
            new ProductServerModpackUpdateDefinition
            {
                LaunchKind = ProductServerLaunchKind.ExecutableJar,
                ServerJarPath = "new-core.jar",
                CoreType = "NeoForge",
                MinecraftVersion = "1.21.1",
                ServerArguments = ["nogui", "--updated"],
                ModpackProviderId = "builtin.modrinth",
                ModpackSource = ProductModpackSourceKind.Modrinth,
                ModpackProjectId = "project",
                ModpackVersionId = "v2",
                ModpackVersionName = "1.7.0",
            });

        public async Task<ProductServerModpackUpdateStatus> StageAsync()
        {
            var staged = await Coordinator.BeginAsync(CreateBeginRequest());
            var candidate = Path.Combine(staged.StagingDirectory!, "candidate");
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["new-core.jar"] = "new-core",
                ["mods/new.jar"] = "new-mod",
                ["config/pack.toml"] = "new-config",
                ["libraries/new-lib.jar"] = "new-library",
            };
            var entries = new List<ProductServerModpackUpdateManifestEntry>();
            foreach (var file in files)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(file.Value);
                await WriteAsync(candidate, file.Key, file.Value);
                entries.Add(new ProductServerModpackUpdateManifestEntry(
                    file.Key,
                    bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes))));
            }

            var manifest = new ProductServerModpackUpdateManifest(1, staged.UpdateId, entries);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await File.WriteAllBytesAsync(
                Path.Combine(staged.StagingDirectory!, ProductServerModpackUpdateCoordinator.ManifestFileName),
                manifestBytes);
            LastManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
            return staged;
        }

        public async Task<ProductServerModpackUpdateStatus> StageAndCommitAsync()
        {
            var staged = await StageAsync();
            return await Coordinator.CommitAsync(staged.UpdateId, LastManifestSha256!);
        }

        public async Task<ProductServerModpackUpdateStatus> WaitForStateAsync(
            Guid updateId,
            ProductServerModpackUpdateState expected)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            ProductServerModpackUpdateStatus? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                last = Coordinator.GetStatus(updateId);
                if (last.State == expected)
                {
                    return last;
                }

                if (last.IsTerminal)
                {
                    break;
                }

                await Task.Delay(25);
            }

            throw new Xunit.Sdk.XunitException(
                $"Expected update state {expected}, actual {last?.State}; " +
                $"error={last?.ErrorCode}:{last?.ErrorMessage}");
        }

        public async Task<ProductServerModpackUpdateStatus> WaitForTerminalAsync(Guid updateId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            ProductServerModpackUpdateStatus? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                last = Coordinator.GetStatus(updateId);
                if (last.IsTerminal)
                {
                    return last;
                }

                await Task.Delay(25);
            }

            throw new Xunit.Sdk.XunitException(
                $"Expected terminal update state, actual {last?.State}; " +
                $"error={last?.ErrorCode}:{last?.ErrorMessage}");
        }

        public async Task WaitForRuntimeStateAsync(ProductServerState expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Runtime.GetStatus(ServerId).Server.State != expected)
            {
                await Task.Delay(25, deadline.Token);
            }
        }

        public async Task ReplaceCoordinatorAsync()
        {
            await Coordinator.DisposeAsync();
            Coordinator = CreateCoordinator(
                Layout,
                Registry,
                Runtime,
                RestartBlocker,
                HealthValidationTimeout);
            await Coordinator.InitializeAsync();
        }

        public async Task ReplaceCoordinatorAfterProcessLossAsync()
        {
            await Coordinator.DisposeAsync();
            Factory.Processes.Last().Complete(1);
            await WaitForRuntimeStateAsync(ProductServerState.Crashed);
            Coordinator = CreateCoordinator(
                Layout,
                Registry,
                Runtime,
                RestartBlocker,
                HealthValidationTimeout);
            await Coordinator.InitializeAsync();
        }

        public async Task<DateTimeOffset> WaitForPersistedHealthDeadlineAsync(Guid updateId)
        {
            var path = Path.Combine(
                Layout.Operations,
                "modpack-updates",
                $"{updateId:N}.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                    if (document.RootElement.TryGetProperty("healthDeadlineAtUtc", out var deadline) &&
                        deadline.ValueKind == JsonValueKind.String &&
                        deadline.TryGetDateTimeOffset(out var value))
                    {
                        return value;
                    }
                }
                catch (Exception error) when (error is IOException or JsonException)
                {
                }

                await Task.Delay(25, timeout.Token);
            }
        }

        public string LivePath(string relativePath)
            => Path.Combine(
                Layout.Servers,
                Registration.ServerDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        public Task<string> ReadLiveAsync(string relativePath)
            => File.ReadAllTextAsync(LivePath(relativePath));

        public string[] TransactionArtifacts()
            => Directory.EnumerateFileSystemEntries(
                    Layout.Servers,
                    ".mcsv-modpack-update-*",
                    SearchOption.TopDirectoryOnly)
                .ToArray();

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Layout.Root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static ProductServerModpackUpdateCoordinator CreateCoordinator(
            ProductDataLayout layout,
            ProductServerRegistry registry,
            ProductServerRuntime runtime,
            ProductServerRestartBlocker restartBlocker,
            TimeSpan healthValidationTimeout)
            => new(
                layout,
                registry,
                runtime,
                restartBlocker,
                new BackupService(),
                new UnhealthyProbe(),
                new ModpackUpdateTransactionService(),
                new ModpackUpdateBackupPlanner(),
                TimeProvider.System,
                healthValidationTimeout);

        private static async Task WriteAsync(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }
    }
}
