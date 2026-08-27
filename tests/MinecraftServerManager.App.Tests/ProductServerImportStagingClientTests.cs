using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductServerImportStagingClientTests
{
    [Fact]
    public async Task Import_CopiesWorldAndRuntimeWithoutChangingPreviewSource()
    {
        var root = CreateRoot();
        try
        {
            var server = Path.Combine(root, "preview-server");
            var runtime = Path.Combine(root, "temurin-21");
            Directory.CreateDirectory(Path.Combine(server, "world", "region"));
            Directory.CreateDirectory(Path.Combine(runtime, "bin"));
            var jar = Path.Combine(server, "server.jar");
            var world = Path.Combine(server, "world", "region", "r.0.0.mca");
            var java = Path.Combine(runtime, "bin", "java.exe");
            var worldBytes = RandomNumberGenerator.GetBytes(2048);
            await File.WriteAllBytesAsync(jar, [1, 2, 3]);
            await File.WriteAllBytesAsync(world, worldBytes);
            await File.WriteAllBytesAsync(java, [4, 5, 6]);
            var model = CreateModel(server, jar, java);
            await using var service = new StagingServiceClient(root);
            var staging = new ProductServerImportStagingClient(
                service,
                Path.Combine(root, "imports"));

            var completed = await staging.ImportAsync(model, $"preview9:{model.Id:N}");

            Assert.Equal(ProductServerImportState.Completed, completed.State);
            Assert.Equal(worldBytes, await File.ReadAllBytesAsync(world));
            Assert.Equal(
                worldBytes,
                await File.ReadAllBytesAsync(Path.Combine(
                    service.StagingDirectory!,
                    "payload",
                    "server",
                    "world",
                    "region",
                    "r.0.0.mca")));
            Assert.Contains(service.Manifest!.Files, entry =>
                entry.Path == "server/world/region/r.0.0.mca");
            Assert.Contains(service.Manifest.Files, entry => entry.Path == "runtime/bin/java.exe");
            Assert.DoesNotContain(service.RequestedDefinition!.GetType().GetProperties(), property =>
                property.PropertyType == typeof(string) &&
                (property.Name.Contains("Source", StringComparison.OrdinalIgnoreCase) ||
                 property.Name.Contains("Final", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ServiceUnavailable_LeavesPreviewWorldAndSettingsSourceUntouched()
    {
        var root = CreateRoot();
        try
        {
            var server = Path.Combine(root, "preview-server");
            var runtime = Path.Combine(root, "temurin-21");
            Directory.CreateDirectory(Path.Combine(server, "world"));
            Directory.CreateDirectory(Path.Combine(runtime, "bin"));
            var jar = Path.Combine(server, "server.jar");
            var world = Path.Combine(server, "world", "level.dat");
            var java = Path.Combine(runtime, "bin", "java.exe");
            await File.WriteAllBytesAsync(jar, [1]);
            await File.WriteAllBytesAsync(world, [7, 8, 9]);
            await File.WriteAllBytesAsync(java, [2]);
            var model = CreateModel(server, jar, java);
            await using var service = new UnavailableServiceClient();
            var staging = new ProductServerImportStagingClient(service);

            var error = await Assert.ThrowsAsync<ProductServiceClientException>(
                () => staging.ImportAsync(model, $"preview9:{model.Id:N}"));

            Assert.Equal("service.connection_failed", error.Code);
            Assert.Equal([7, 8, 9], await File.ReadAllBytesAsync(world));
            Assert.Equal(server, model.DirectoryPath);
            Assert.Equal(jar, model.ServerJarPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ServiceReturnedPathOutsideAuthorizedImports_IsRejectedBeforeAnyWrite()
    {
        var root = CreateRoot();
        try
        {
            var server = Path.Combine(root, "preview-server");
            var runtime = Path.Combine(root, "temurin-21");
            Directory.CreateDirectory(server);
            Directory.CreateDirectory(Path.Combine(runtime, "bin"));
            var jar = Path.Combine(server, "server.jar");
            var java = Path.Combine(runtime, "bin", "java.exe");
            await File.WriteAllBytesAsync(jar, [1]);
            await File.WriteAllBytesAsync(java, [2]);
            var model = CreateModel(server, jar, java);
            await using var service = new StagingServiceClient(root);
            var staging = new ProductServerImportStagingClient(
                service,
                Path.Combine(root, "different-authorized-imports"));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => staging.ImportAsync(model, $"preview9:{model.Id:N}"));

            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(service.StagingDirectory!, "payload"),
                "*",
                SearchOption.AllDirectories));
            Assert.Equal([1], await File.ReadAllBytesAsync(jar));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ServerInstance CreateModel(string server, string jar, string java) => new()
    {
        Name = "Preview world",
        DirectoryPath = server,
        ServerJarPath = jar,
        JavaExecutablePath = java,
        JavaMajorVersion = 21,
        LaunchKind = ServerLaunchKind.ExecutableJar,
        CoreType = CoreType.Paper,
        MinecraftVersion = "1.21.1",
        MinimumMemoryMb = 1024,
        MaximumMemoryMb = 2048,
        ServerArguments = ["nogui"],
        Port = 25565,
    };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "muhun-app-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private abstract class ImportClientBase : IProductServiceClient
    {
        public abstract Task<ProductServerImportStatus> BeginImportAsync(
            ProductServerImportBeginRequest request,
            CancellationToken cancellationToken = default);

        public virtual Task<ProductServerImportStatus> CommitImportAsync(
            Guid importId,
            string manifestSha256,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public virtual Task<ProductServerImportStatus> GetImportStatusAsync(
            Guid importId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public virtual Task<ProductServerImportStatus> CancelImportAsync(
            Guid importId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductLocalHandshakePayload> HandshakeAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerStatus> GetStatusAsync(Guid serverId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerStatus> RegisterAsync(ProductServerRegistration registration, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(Guid serverId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerMutationResult> StartAsync(Guid serverId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerMutationResult> StopAsync(Guid serverId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerMutationResult> RestartAsync(Guid serverId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductConsolePage> ReadConsoleAsync(Guid serverId, long afterCursor, int limit = 50, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerStatus> SendCommandAsync(Guid serverId, string command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnavailableServiceClient : ImportClientBase
    {
        public override Task<ProductServerImportStatus> BeginImportAsync(
            ProductServerImportBeginRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<ProductServerImportStatus>(new ProductServiceClientException(
                "service.connection_failed",
                "Service is unavailable."));
    }

    private sealed class StagingServiceClient : ImportClientBase
    {
        private readonly Guid _importId = Guid.NewGuid();

        public StagingServiceClient(string root)
        {
            StagingDirectory = Path.Combine(root, "imports", _importId.ToString("N"));
            Directory.CreateDirectory(Path.Combine(StagingDirectory, "payload", "server"));
            Directory.CreateDirectory(Path.Combine(StagingDirectory, "payload", "runtime"));
        }

        public string? StagingDirectory { get; }
        public ProductServerImportManifest? Manifest { get; private set; }
        public ProductServerImportDefinition? RequestedDefinition { get; private set; }

        public override Task<ProductServerImportStatus> BeginImportAsync(
            ProductServerImportBeginRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestedDefinition = request.Server;
            return Task.FromResult(Status(ProductServerImportState.Staging, request.Server.ServerId));
        }

        public override async Task<ProductServerImportStatus> CommitImportAsync(
            Guid importId,
            string manifestSha256,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_importId, importId);
            var manifestPath = Path.Combine(StagingDirectory!, "manifest.v1.json");
            var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), manifestSha256);
            Manifest = JsonSerializer.Deserialize<ProductServerImportManifest>(
                bytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Status(ProductServerImportState.Completed, RequestedDefinition!.ServerId);
        }

        private ProductServerImportStatus Status(ProductServerImportState state, Guid serverId)
            => new(
                _importId,
                serverId,
                state,
                state == ProductServerImportState.Staging ? StagingDirectory : null,
                0,
                0,
                0,
                0,
                null,
                null,
                DateTimeOffset.UtcNow);
    }
}
