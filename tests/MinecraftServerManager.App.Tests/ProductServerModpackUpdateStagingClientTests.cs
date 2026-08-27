using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductServerModpackUpdateStagingClientTests
{
    [Fact]
    public async Task Update_StagesExactCandidateManifestAndPollsUntilAwaitingHealth()
    {
        var root = CreateRoot();
        try
        {
            var candidate = await CreateCandidateAsync(root);
            await using var service = new StagingServiceClient(root);
            var staging = CreateStagingClient(service, root);

            var status = await staging.UpdateAsync(
                candidate,
                service.ServerId,
                "v1",
                Target());

            Assert.Equal(ProductServerModpackUpdateState.AwaitingHealth, status.State);
            Assert.Equal(1, service.StatusCalls);
            Assert.NotNull(service.Manifest);
            Assert.Equal(1, service.Manifest!.SchemaVersion);
            Assert.Equal(service.UpdateId, service.Manifest.UpdateId);
            Assert.Equal(
                ["config/pack.toml", "mods/new.jar", "server.jar", "world/level.dat"],
                service.Manifest.Files.Select(entry => entry.Path).Order().ToArray());
            foreach (var entry in service.Manifest.Files)
            {
                var stagedPath = Path.Combine(
                    service.StagingDirectory,
                    "candidate",
                    entry.Path.Replace('/', Path.DirectorySeparatorChar));
                var bytes = await File.ReadAllBytesAsync(stagedPath);
                Assert.Equal(bytes.LongLength, entry.Length);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256);
            }

            Assert.Empty(Directory.EnumerateFiles(
                service.StagingDirectory,
                "*.tmp",
                SearchOption.TopDirectoryOnly));
            Assert.Equal("live-world", await File.ReadAllTextAsync(
                Path.Combine(candidate.DirectoryPath, "world", "level.dat")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TraversalInTarget_IsRejectedBeforeServiceBegin()
    {
        var root = CreateRoot();
        try
        {
            var candidate = await CreateCandidateAsync(root);
            await using var service = new StagingServiceClient(root);
            var staging = CreateStagingClient(service, root);

            await Assert.ThrowsAsync<InvalidDataException>(() => staging.UpdateAsync(
                candidate,
                service.ServerId,
                "v1",
                Target() with { ServerJarPath = "../outside.jar" }));

            Assert.Equal(0, service.BeginCalls);
            Assert.Equal(0, service.CancelCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReparseInCandidate_IsRejectedAndServiceCancelIsAttempted()
    {
        var root = CreateRoot();
        try
        {
            var candidate = await CreateCandidateAsync(root);
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "sentinel.keep");
            await File.WriteAllTextAsync(sentinel, "outside");
            CreateDirectoryJunction(Path.Combine(candidate.DirectoryPath, "linked"), outside);
            await using var service = new StagingServiceClient(root);
            var staging = CreateStagingClient(service, root);

            await Assert.ThrowsAsync<InvalidDataException>(() => staging.UpdateAsync(
                candidate,
                service.ServerId,
                "v1",
                Target()));

            Assert.Equal(1, service.BeginCalls);
            Assert.Equal(1, service.CancelCalls);
            Assert.Equal("outside", await File.ReadAllTextAsync(sentinel));
            Directory.Delete(Path.Combine(candidate.DirectoryPath, "linked"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ServiceStagingOutsideAuthorizedCapability_IsRejectedBeforeWriteAndCancelled()
    {
        var root = CreateRoot();
        try
        {
            var candidate = await CreateCandidateAsync(root);
            var outside = Path.Combine(root, "not-authorized", Guid.NewGuid().ToString("N"));
            await using var service = new StagingServiceClient(root, stagingOverride: outside);
            var staging = CreateStagingClient(service, root);

            await Assert.ThrowsAsync<InvalidDataException>(() => staging.UpdateAsync(
                candidate,
                service.ServerId,
                "v1",
                Target()));

            Assert.Equal(1, service.CancelCalls);
            Assert.False(Directory.Exists(Path.Combine(outside, "candidate")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationWhilePolling_AttemptsServiceCancel()
    {
        var root = CreateRoot();
        try
        {
            var candidate = await CreateCandidateAsync(root);
            await using var service = new StagingServiceClient(root)
            {
                BlockStatusUntilCancelled = true,
            };
            var staging = CreateStagingClient(service, root);
            using var cancellation = new CancellationTokenSource();
            var update = staging.UpdateAsync(
                candidate,
                service.ServerId,
                "v1",
                Target(),
                cancellation.Token);
            await service.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            Assert.Equal(1, service.CancelCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopController_ForwardsBeginCommitStatusAndCancelOnlyToServiceClient()
    {
        var root = CreateRoot();
        try
        {
            await using var service = new StagingServiceClient(root);
            await using var controller = new ProductServiceDesktopController(service);
            var begun = await controller.BeginModpackUpdateAsync(new(
                service.ServerId,
                "v1",
                Target()));
            var hash = new string('a', 64);

            await controller.CommitModpackUpdateAsync(begun.UpdateId, hash);
            await controller.GetModpackUpdateStatusAsync(begun.UpdateId);
            await controller.CancelModpackUpdateAsync(begun.UpdateId);

            Assert.Equal(["begin", "commit:" + hash, "status", "cancel"], service.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProductServerModpackUpdateStagingClient CreateStagingClient(
        IProductServiceClient service,
        string root)
        => new(
            service,
            Path.Combine(root, "imports"),
            TimeSpan.FromMilliseconds(5));

    private static async Task<ServerInstance> CreateCandidateAsync(string root)
    {
        var directory = Path.Combine(root, "candidate-source");
        Directory.CreateDirectory(Path.Combine(directory, "mods"));
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        Directory.CreateDirectory(Path.Combine(directory, "world"));
        await File.WriteAllTextAsync(Path.Combine(directory, "server.jar"), "new-core");
        await File.WriteAllTextAsync(Path.Combine(directory, "mods", "new.jar"), "new-mod");
        await File.WriteAllTextAsync(Path.Combine(directory, "config", "pack.toml"), "new-config");
        await File.WriteAllTextAsync(Path.Combine(directory, "world", "level.dat"), "live-world");
        return new ServerInstance
        {
            Name = "Candidate",
            DirectoryPath = directory,
            ServerJarPath = "server.jar",
            LaunchKind = ServerLaunchKind.ExecutableJar,
            CoreType = CoreType.NeoForge,
            MinecraftVersion = "1.21.1",
            ModpackProviderId = "builtin.modrinth",
            ModpackSource = ModpackSourceKind.Modrinth,
            ModpackProjectId = "project",
            ModpackVersionId = "v2",
            ModpackVersionName = "1.7.0",
        };
    }

    private static ProductServerModpackUpdateDefinition Target() => new()
    {
        LaunchKind = ProductServerLaunchKind.ExecutableJar,
        ServerJarPath = "server.jar",
        CoreType = "NeoForge",
        MinecraftVersion = "1.21.1",
        ServerArguments = ["nogui"],
        ModpackProviderId = "builtin.modrinth",
        ModpackSource = ProductModpackSourceKind.Modrinth,
        ModpackProjectId = "project",
        ModpackVersionId = "v2",
        ModpackVersionName = "1.7.0",
    };

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-app-modpack-update-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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

    private sealed class StagingServiceClient : IProductServiceClient
    {
        public StagingServiceClient(string root, string? stagingOverride = null)
        {
            UpdateId = Guid.NewGuid();
            ServerId = Guid.NewGuid();
            StagingDirectory = stagingOverride ?? Path.Combine(
                root,
                "imports",
                "modpack-updates",
                UpdateId.ToString("N"));
            Directory.CreateDirectory(StagingDirectory);
        }

        public Guid UpdateId { get; }
        public Guid ServerId { get; }
        public string StagingDirectory { get; }
        public int BeginCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public bool BlockStatusUntilCancelled { get; init; }
        public TaskCompletionSource StatusEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ProductServerModpackUpdateManifest? Manifest { get; private set; }
        public List<string> Calls { get; } = [];

        public Task<ProductServerModpackUpdateStatus> BeginModpackUpdateAsync(
            ProductServerModpackUpdateBeginRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(ServerId, request.ServerId);
            BeginCalls++;
            Calls.Add("begin");
            return Task.FromResult(Status(ProductServerModpackUpdateState.Staging));
        }

        public async Task<ProductServerModpackUpdateStatus> CommitModpackUpdateAsync(
            Guid updateId,
            string manifestSha256,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(UpdateId, updateId);
            Calls.Add("commit:" + manifestSha256);
            var path = Path.Combine(StagingDirectory, "manifest.v1.json");
            if (File.Exists(path))
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), manifestSha256);
                Manifest = JsonSerializer.Deserialize<ProductServerModpackUpdateManifest>(
                    bytes,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            return Status(ProductServerModpackUpdateState.Queued);
        }

        public async Task<ProductServerModpackUpdateStatus> GetModpackUpdateStatusAsync(
            Guid updateId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(UpdateId, updateId);
            StatusCalls++;
            Calls.Add("status");
            StatusEntered.TrySetResult();
            if (BlockStatusUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Status(ProductServerModpackUpdateState.AwaitingHealth);
        }

        public Task<ProductServerModpackUpdateStatus> CancelModpackUpdateAsync(
            Guid updateId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(UpdateId, updateId);
            CancelCalls++;
            Calls.Add("cancel");
            return Task.FromResult(Status(ProductServerModpackUpdateState.Cancelled));
        }

        public Task<ProductLocalHandshakePayload> HandshakeAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerStatus> GetStatusAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerStatus> RegisterAsync(
            ProductServerRegistration registration,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerMutationResult> StartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerMutationResult> StopAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerMutationResult> RestartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductConsolePage> ReadConsoleAsync(
            Guid serverId,
            long afterCursor,
            int limit = 50,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerStatus> SendCommandAsync(
            Guid serverId,
            string command,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private ProductServerModpackUpdateStatus Status(ProductServerModpackUpdateState state)
            => new(
                UpdateId,
                ServerId,
                state,
                state == ProductServerModpackUpdateState.Staging ? StagingDirectory : null,
                Manifest?.Files.Sum(entry => entry.Length) ?? 0,
                0,
                Manifest?.Files.Count ?? 0,
                0,
                null,
                null,
                null,
                DateTimeOffset.UtcNow);
    }
}
