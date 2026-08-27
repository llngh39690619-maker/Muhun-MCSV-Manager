using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerImportServiceTests
{
    [Fact]
    public async Task Commit_PreservesWorldBytesAndRegistersOnlyServiceOwnedPaths()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var sourceWorld = Path.Combine(fixture.Layout.Root, "source-world.dat");
        var worldBytes = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(sourceWorld, worldBytes);
        var begin = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(
            fixture.Definition,
            "preview9:world-preservation"));
        var manifestHash = await WritePayloadAsync(
            begin,
            new Dictionary<string, byte[]>
            {
                ["server/server.jar"] = [1, 2, 3],
                ["server/world/region/r.0.0.mca"] = await File.ReadAllBytesAsync(sourceWorld),
                ["runtime/bin/java.exe"] = [4, 5, 6],
            });

        var queued = await fixture.Service.CommitAsync(begin.ImportId, manifestHash);
        var completed = await WaitForTerminalAsync(fixture.Service, queued.ImportId);

        Assert.True(
            completed.State == ProductServerImportState.Completed,
            $"{completed.ErrorCode}: {completed.ErrorMessage}");
        Assert.Equal(worldBytes, await File.ReadAllBytesAsync(sourceWorld));
        var stored = Assert.Single(fixture.Registry.GetAll());
        Assert.Equal(fixture.Definition.ServerId, stored.Id);
        Assert.Equal(fixture.Definition.ServerId.ToString("N"), stored.ServerDirectory);
        Assert.StartsWith(fixture.Definition.ServerId.ToString("N") + "/", stored.JavaRuntimePath);
        Assert.Equal(
            worldBytes,
            await File.ReadAllBytesAsync(Path.Combine(
                fixture.Layout.Servers,
                stored.ServerDirectory,
                "world",
                "region",
                "r.0.0.mca")));
    }

    [Fact]
    public async Task Commit_RejectsTraversalBeforeServiceOpensPayload()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var begin = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(fixture.Definition));
        var hash = await WriteManifestAsync(
            begin,
            [
                new ProductServerImportManifestEntry("server/../escape.dat", 0, EmptyHash),
                new ProductServerImportManifestEntry("runtime/bin/java.exe", 0, EmptyHash),
            ]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Service.CommitAsync(begin.ImportId, hash));
        Assert.False(File.Exists(Path.Combine(fixture.Layout.Root, "escape.dat")));
    }

    [Fact]
    public async Task Copy_HashMismatchFailsWithoutRegisteringOrPromoting()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var begin = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(fixture.Definition));
        var staging = begin.StagingDirectory!;
        WritePayloadFile(staging, "server/server.jar", [1]);
        WritePayloadFile(staging, "runtime/bin/java.exe", [2]);
        var hash = await WriteManifestAsync(
            begin,
            [
                new ProductServerImportManifestEntry("server/server.jar", 1, EmptyHash),
                new ProductServerImportManifestEntry("runtime/bin/java.exe", 1, EmptyHash),
            ]);

        _ = await fixture.Service.CommitAsync(begin.ImportId, hash);
        var terminal = await WaitForTerminalAsync(fixture.Service, begin.ImportId);

        Assert.Equal(ProductServerImportState.Failed, terminal.State);
        Assert.Equal("import.integrity_failed", terminal.ErrorCode);
        Assert.Empty(fixture.Registry.GetAll());
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Layout.Servers,
            fixture.Definition.ServerId.ToString("N"))));
    }

    [Fact]
    public async Task Copy_RejectsReparsePointWithoutReadingItsExternalTarget()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var begin = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(fixture.Definition));
        var staging = begin.StagingDirectory!;
        WritePayloadFile(staging, "server/server.jar", [1]);
        WritePayloadFile(staging, "runtime/bin/java.exe", [2]);
        var outside = Path.Combine(fixture.Layout.Root, "outside");
        Directory.CreateDirectory(outside);
        var outsideSecret = Path.Combine(outside, "secret.dat");
        await File.WriteAllBytesAsync(outsideSecret, [9, 9, 9]);
        var link = Path.Combine(staging, "payload", "server", "linked");
        CreateDirectoryJunction(link, outside);
        var hash = await WriteManifestAsync(
            begin,
            [
                new ProductServerImportManifestEntry(
                    "server/server.jar",
                    1,
                    Convert.ToHexString(SHA256.HashData([1]))),
                new ProductServerImportManifestEntry(
                    "server/linked/secret.dat",
                    3,
                    Convert.ToHexString(SHA256.HashData([9, 9, 9]))),
                new ProductServerImportManifestEntry(
                    "runtime/bin/java.exe",
                    1,
                    Convert.ToHexString(SHA256.HashData([2]))),
            ]);

        _ = await fixture.Service.CommitAsync(begin.ImportId, hash);
        var failed = await WaitForTerminalAsync(fixture.Service, begin.ImportId);

        Assert.Equal(ProductServerImportState.Failed, failed.State);
        Assert.Equal("import.integrity_failed", failed.ErrorCode);
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(outsideSecret));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task Commit_RejectsReplacedStagingCapabilityBeforeOpeningItsManifest()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var begin = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(fixture.Definition));
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"Muhun-MCSV-import-capability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(outside, "payload", "server"));
        Directory.CreateDirectory(Path.Combine(outside, "payload", "runtime", "bin"));
        var sentinel = Path.Combine(outside, "sentinel.keep");
        await File.WriteAllTextAsync(sentinel, "outside must remain untouched");
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new ProductServerImportManifest(
                1,
                begin.ImportId,
                [
                    new ProductServerImportManifestEntry("server/server.jar", 0, EmptyHash),
                    new ProductServerImportManifestEntry("runtime/bin/java.exe", 0, EmptyHash),
                ]),
            JsonOptions);
        await File.WriteAllBytesAsync(
            Path.Combine(outside, ProductServerImportService.ManifestFileName),
            manifestBytes);

        Directory.Delete(begin.StagingDirectory!, recursive: true);
        CreateDirectoryJunction(begin.StagingDirectory!, outside);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.CommitAsync(
                begin.ImportId,
                Convert.ToHexString(SHA256.HashData(manifestBytes))));
            Assert.Equal("outside must remain untouched", await File.ReadAllTextAsync(sentinel));
            Assert.Equal(ProductServerImportState.Staging, fixture.Service.GetStatus(begin.ImportId).State);
        }
        finally
        {
            if (Directory.Exists(begin.StagingDirectory) &&
                File.GetAttributes(begin.StagingDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(begin.StagingDirectory);
            }

            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Cancel_StagingTransactionRemovesOnlyItsCapabilityDirectory()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var begin = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(fixture.Definition));
        var unrelated = Path.Combine(fixture.Layout.Imports, "unrelated.keep");
        await File.WriteAllTextAsync(unrelated, "keep");

        var cancelled = await fixture.Service.CancelAsync(begin.ImportId);

        Assert.Equal(ProductServerImportState.Cancelled, cancelled.State);
        Assert.False(Directory.Exists(begin.StagingDirectory));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task Initialize_ResumesQueuedCrashJournalAndCreatesDurableReceipt()
    {
        await using var fixture = await ImportFixture.CreateAsync(initialize: false);
        fixture.Layout.EnsureCreated();
        var importId = Guid.NewGuid();
        var staging = Path.Combine(fixture.Layout.Imports, importId.ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "payload", "server"));
        Directory.CreateDirectory(Path.Combine(staging, "payload", "runtime"));
        var status = new ProductServerImportStatus(
            importId,
            fixture.Definition.ServerId,
            ProductServerImportState.Staging,
            staging,
            0,
            0,
            0,
            0,
            null,
            null,
            DateTimeOffset.UtcNow);
        var manifestHash = await WritePayloadAsync(
            status,
            new Dictionary<string, byte[]>
            {
                ["server/server.jar"] = [7],
                ["server/world/level.dat"] = [8],
                ["runtime/bin/java.exe"] = [9],
            });
        var manifest = JsonSerializer.Deserialize<ProductServerImportManifest>(
            await File.ReadAllTextAsync(Path.Combine(staging, ProductServerImportService.ManifestFileName)),
            JsonOptions)!;
        var journalDirectory = Path.Combine(fixture.Layout.Operations, "imports");
        Directory.CreateDirectory(Path.Combine(journalDirectory, "receipts"));
        await File.WriteAllTextAsync(
            Path.Combine(journalDirectory, $"{importId:N}.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                importId,
                server = fixture.Definition,
                migrationKey = "preview9:crash-resume",
                state = ProductServerImportState.Queued,
                manifestSha256 = manifestHash,
                totalBytes = manifest.Files.Sum(file => file.Length),
                completedBytes = 0,
                totalFiles = manifest.Files.Count,
                completedFiles = 0,
                serverPromoted = false,
                runtimePromoted = false,
                errorCode = (string?)null,
                errorMessage = (string?)null,
                createdAtUtc = DateTimeOffset.UtcNow,
                updatedAtUtc = DateTimeOffset.UtcNow,
            }, JsonOptions));

        await fixture.Service.InitializeAsync();
        var completed = await WaitForTerminalAsync(fixture.Service, importId);
        var repeated = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(
            fixture.Definition,
            "preview9:crash-resume"));

        Assert.True(
            completed.State == ProductServerImportState.Completed,
            $"{completed.ErrorCode}: {completed.ErrorMessage}");
        Assert.Equal(ProductServerImportState.Completed, repeated.State);
        Assert.Equal(importId, repeated.ImportId);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(journalDirectory, "receipts"),
            "*.json"));
    }

    [Fact]
    public async Task PreexistingFinalDirectoryFailsWithoutDeletingUnknownContent()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var final = Path.Combine(fixture.Layout.Servers, fixture.Definition.ServerId.ToString("N"));
        Directory.CreateDirectory(final);
        var sentinel = Path.Combine(final, "unknown.keep");
        await File.WriteAllTextAsync(sentinel, "do not delete");
        var begin = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(fixture.Definition));
        var hash = await WritePayloadAsync(
            begin,
            new Dictionary<string, byte[]>
            {
                ["server/server.jar"] = [1],
                ["runtime/bin/java.exe"] = [2],
            });

        _ = await fixture.Service.CommitAsync(begin.ImportId, hash);
        var failed = await WaitForTerminalAsync(fixture.Service, begin.ImportId);

        Assert.Equal(ProductServerImportState.Failed, failed.State);
        Assert.Equal("do not delete", await File.ReadAllTextAsync(sentinel));
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task DiskPreflightRejectsBeforeCommitStateChanges()
    {
        await using var fixture = await ImportFixture.CreateAsync(
            diskSpace: new FixedDiskSpaceProbe(0));
        var begin = await fixture.Service.BeginAsync(new ProductServerImportBeginRequest(fixture.Definition));
        var hash = await WritePayloadAsync(
            begin,
            new Dictionary<string, byte[]>
            {
                ["server/server.jar"] = [1],
                ["runtime/bin/java.exe"] = [2],
            });

        await Assert.ThrowsAsync<IOException>(
            () => fixture.Service.CommitAsync(begin.ImportId, hash));

        Assert.Equal(ProductServerImportState.Staging, fixture.Service.GetStatus(begin.ImportId).State);
        Assert.Empty(fixture.Registry.GetAll());
    }

    [Fact]
    public async Task IpcWithoutImportEngineReturnsStableUnavailableError()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        var processor = new ProductIpcMessageProcessor(state, fixture.Runtime);
        var response = await processor.ProcessAsync(new ProductIpcRequest(
            ProductIpcProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            ProductIpcProtocol.ServerImportBeginMethod,
            ProductApiProtocol.MinimumSupportedVersion,
            ProductApiProtocol.CurrentVersion)
        {
            ImportBegin = new ProductServerImportBeginRequest(fixture.Definition),
        }, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("service.import_unavailable", response.Error?.Code);
    }

    private static async Task<string> WritePayloadAsync(
        ProductServerImportStatus status,
        IReadOnlyDictionary<string, byte[]> files)
    {
        var entries = new List<ProductServerImportManifestEntry>();
        foreach (var pair in files)
        {
            WritePayloadFile(status.StagingDirectory!, pair.Key, pair.Value);
            entries.Add(new ProductServerImportManifestEntry(
                pair.Key,
                pair.Value.LongLength,
                Convert.ToHexString(SHA256.HashData(pair.Value))));
        }

        return await WriteManifestAsync(status, entries);
    }

    private static void WritePayloadFile(string staging, string manifestPath, byte[] bytes)
    {
        var destination = Path.Combine(
            staging,
            "payload",
            manifestPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes);
    }

    private static async Task<string> WriteManifestAsync(
        ProductServerImportStatus status,
        IReadOnlyList<ProductServerImportManifestEntry> entries)
    {
        var path = Path.Combine(status.StagingDirectory!, ProductServerImportService.ManifestFileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ProductServerImportManifest(1, status.ImportId, entries),
            JsonOptions);
        await File.WriteAllBytesAsync(path, bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async Task<ProductServerImportStatus> WaitForTerminalAsync(
        ProductServerImportService service,
        Guid importId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var status = service.GetStatus(importId);
            if (status.IsTerminal)
            {
                return status;
            }

            await Task.Delay(25, deadline.Token);
        }
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

    private const string EmptyHash =
        "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
    };

    private sealed class FixedDiskSpaceProbe(long availableBytes) : IProductImportDiskSpaceProbe
    {
        public long GetAvailableBytes(string path) => availableBytes;
    }

    private sealed class ImportFixture : IAsyncDisposable
    {
        private ImportFixture(
            ProductDataLayout layout,
            ProductServerRegistry registry,
            ProductServerRuntime runtime,
            ProductServerImportService service,
            ProductServerImportDefinition definition)
        {
            Layout = layout;
            Registry = registry;
            Runtime = runtime;
            Service = service;
            Definition = definition;
        }

        public ProductDataLayout Layout { get; }
        public ProductServerRegistry Registry { get; }
        public ProductServerRuntime Runtime { get; }
        public ProductServerImportService Service { get; }
        public ProductServerImportDefinition Definition { get; }

        public static async Task<ImportFixture> CreateAsync(
            bool initialize = true,
            IProductImportDiskSpaceProbe? diskSpace = null)
        {
            var layout = ProductServerRegistryTests.CreateLayout();
            var registry = new ProductServerRegistry(layout);
            await registry.LoadAsync();
            var processManager = new ServerProcessManager(
                new ServerProcessManagerOptions { ResourceSamplingInterval = Timeout.InfiniteTimeSpan },
                new ProductServerTestProcessFactory());
            var runtime = new ProductServerRuntime(
                registry,
                layout,
                processManager,
                new ProductDesiredRunIntentStore(layout));
            var service = diskSpace is null
                ? new ProductServerImportService(layout, registry, runtime)
                : new ProductServerImportService(layout, registry, runtime, diskSpace);
            if (initialize)
            {
                await service.InitializeAsync();
            }

            var definition = new ProductServerImportDefinition
            {
                ServerId = Guid.NewGuid(),
                Name = "Imported world",
                LaunchKind = ProductServerLaunchKind.ExecutableJar,
                ServerJarPath = "server.jar",
                JavaExecutablePath = "bin/java.exe",
                CoreType = "Paper",
                MinecraftVersion = "1.21.1",
                MinimumMemoryMb = 1024,
                MaximumMemoryMb = 2048,
                ServerArguments = ["nogui"],
                Port = 25565,
            };
            return new ImportFixture(layout, registry, runtime, service, definition);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Layout.Root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
