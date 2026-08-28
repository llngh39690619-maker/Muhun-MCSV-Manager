using System.Diagnostics;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientInstanceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-client-install-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsync_PromotesCompletedPayloadAndCommitsRegistry()
    {
        var request = CreateRequest();
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));
        var payload = new FakePayloadInstaller(async (directory, cancellationToken) =>
        {
            Directory.CreateDirectory(Path.Combine(directory, "versions", request.GameVersion));
            await File.WriteAllTextAsync(
                Path.Combine(directory, "versions", request.GameVersion, "verified.marker"),
                "ok",
                cancellationToken);
            return request.GameVersion;
        });
        var manager = CreateManager(registry, payload, request.GameVersion);

        var result = await manager.InstallAsync(request, javaExecutablePath: null);

        Assert.Equal(request.GameVersion, result.InstalledVersionId);
        Assert.Equal(request.GameVersion, result.Instance.InstalledVersionId);
        Assert.True(File.Exists(Path.Combine(
            result.Instance.DirectoryPath,
            "versions",
            request.GameVersion,
            "verified.marker")));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging", request.InstanceId.ToString("N"))));
        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Equal(request.InstanceId, stored.Id);
        Assert.Equal(result.Instance.DirectoryPath, stored.DirectoryPath);
        Assert.Equal(21, result.Instance.JavaMajorVersion);
        Assert.Equal(21, stored.JavaMajorVersion);
    }

    [Fact]
    public async Task InstallAsync_RejectsNonReleaseBeforeCreatingPayload()
    {
        var request = CreateRequest() with { GameVersion = "26w20a" };
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));
        var payload = new FakePayloadInstaller((_, _) => Task.FromResult(request.GameVersion));
        var manager = CreateManager(registry, payload, "26.2");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InstallAsync(request, javaExecutablePath: null));

        Assert.Equal(0, payload.CallCount);
        Assert.Empty((await registry.LoadAsync()).Instances);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(100)]
    public async Task InstallAsync_RejectsUnsafeJavaMajorBeforeCreatingPayload(int javaMajorVersion)
    {
        var request = CreateRequest() with { JavaMajorVersion = javaMajorVersion };
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));
        var payload = new FakePayloadInstaller((_, _) => Task.FromResult(request.GameVersion));
        var manager = CreateManager(registry, payload, request.GameVersion);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.InstallAsync(request, javaExecutablePath: null));

        Assert.Equal(0, payload.CallCount);
        Assert.Empty((await registry.LoadAsync()).Instances);
    }

    [Fact]
    public async Task InstallAsync_RemovesStagingWhenPayloadFails()
    {
        var request = CreateRequest();
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));
        var payload = new FakePayloadInstaller((directory, _) =>
        {
            File.WriteAllText(Path.Combine(directory, "partial.bin"), "partial");
            throw new InvalidDataException("hash mismatch");
        });
        var manager = CreateManager(registry, payload, request.GameVersion);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.InstallAsync(request, javaExecutablePath: null));

        Assert.False(Directory.Exists(Path.Combine(_root, "staging", request.InstanceId.ToString("N"))));
        Assert.Empty((await registry.LoadAsync()).Instances);
    }

    [Fact]
    public async Task DeleteAsync_StagesThenRemovesRegistryAndPayload()
    {
        var request = CreateRequest();
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));
        var manager = CreateManager(
            registry,
            new FakePayloadInstaller((directory, _) =>
            {
                File.WriteAllText(Path.Combine(directory, "payload.bin"), "owned");
                return Task.FromResult(request.GameVersion);
            }),
            request.GameVersion);
        var installed = await manager.InstallAsync(request, javaExecutablePath: null);

        var deleted = await manager.DeleteAsync(request.InstanceId);

        Assert.Equal(request.InstanceId, deleted.Id);
        Assert.False(Directory.Exists(installed.Instance.DirectoryPath));
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "staging")));
    }

    [Fact]
    public async Task CommitStagedDeletionAsync_RegistryFailureRestoresOriginalDirectory()
    {
        var instances = Path.Combine(_root, "instances");
        var staging = Path.Combine(_root, "staging");
        var instance = Path.Combine(instances, Guid.NewGuid().ToString("N"));
        var tombstone = Path.Combine(staging, $"delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(instance);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(instance, "world.dat"), "keep");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MinecraftClientInstanceManager.CommitStagedDeletionAsync(
                instances,
                instance,
                staging,
                tombstone,
                () => Task.FromException<bool>(new InvalidDataException("registry failed"))));

        Assert.True(File.Exists(Path.Combine(instance, "world.dat")));
        Assert.False(Directory.Exists(tombstone));
    }

    [Fact]
    public async Task DeleteAsync_RejectsRegistryPathOutsideExactManagedIdDirectory()
    {
        var id = Guid.NewGuid();
        var external = Path.Combine(_root, "external-instance");
        Directory.CreateDirectory(external);
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));
        await registry.SaveAsync(new MinecraftClientRegistryDocument
        {
            Instances = [CreateStoredInstance(id, external)],
        });
        var manager = CreateManager(
            registry,
            new FakePayloadInstaller((_, _) => Task.FromResult("26.2")),
            "26.2");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.DeleteAsync(id));

        Assert.True(Directory.Exists(external));
        Assert.Single((await registry.LoadAsync()).Instances);
    }

    [Fact]
    public async Task DeleteAsync_RejectsMatchingLiveJavaProcess()
    {
        var id = Guid.NewGuid();
        var instancePath = Path.Combine(_root, "instances", id.ToString("N"));
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(instancePath);
        Directory.CreateDirectory(runtime);
        var javaPath = Path.Combine(runtime, "java.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), javaPath);
        using var process = Process.Start(new ProcessStartInfo(javaPath, "/c ping 127.0.0.1 -n 30 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Unable to start the fake Java test process.");
        try
        {
            var stored = CreateStoredInstance(id, instancePath);
            stored.JavaExecutablePath = javaPath;
            MinecraftClientProcessRecoveryService.RecordIdentity(
                stored,
                new MinecraftClientProcessIdentity(
                    process.Id,
                    new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                    javaPath));
            using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));
            await registry.SaveAsync(new MinecraftClientRegistryDocument { Instances = [stored] });
            var manager = CreateManager(
                registry,
                new FakePayloadInstaller((_, _) => Task.FromResult("26.2")),
                "26.2");

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DeleteAsync(id));

            Assert.True(Directory.Exists(instancePath));
            Assert.Single((await registry.LoadAsync()).Instances);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public void Constructor_RemovesOnlyOwnedStaleStagingDirectories()
    {
        var staleId = Guid.NewGuid().ToString("N");
        var freshId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(_root, "staging");
        var stale = Path.Combine(stagingRoot, staleId);
        var fresh = Path.Combine(stagingRoot, freshId);
        var staleDeletion = Path.Combine(
            stagingRoot,
            $"delete-{(DateTime.UtcNow - MinecraftClientInstanceManager.StaleStagingAge - TimeSpan.FromHours(1)).Ticks:D19}-{Guid.NewGuid():N}");
        var freshDeletion = Path.Combine(
            stagingRoot,
            $"delete-{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}");
        var unrelated = Path.Combine(stagingRoot, "user-folder");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(staleDeletion);
        Directory.CreateDirectory(freshDeletion);
        Directory.CreateDirectory(unrelated);
        Directory.SetLastWriteTimeUtc(
            stale,
            DateTime.UtcNow - MinecraftClientInstanceManager.StaleStagingAge - TimeSpan.FromHours(1));
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));

        _ = CreateManager(
            registry,
            new FakePayloadInstaller((_, _) => Task.FromResult("26.2")),
            "26.2");

        Assert.False(Directory.Exists(stale));
        Assert.False(Directory.Exists(staleDeletion));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(freshDeletion));
        Assert.True(Directory.Exists(unrelated));
    }

    [Theory]
    [InlineData(MinecraftClientLoader.OptiFine)]
    [InlineData(MinecraftClientLoader.LabyMod)]
    public async Task InstallAsync_DoesNotSilentlyInstallExternalProducts(MinecraftClientLoader loader)
    {
        var request = CreateRequest() with { Loader = loader };
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "client-instances.v1.json"));
        var manager = CreateManager(
            registry,
            new FakePayloadInstaller((_, _) => Task.FromResult("unexpected")),
            request.GameVersion);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.InstallAsync(request, javaExecutablePath: null));
    }

    private MinecraftClientInstanceManager CreateManager(
        MinecraftClientRegistry registry,
        IMinecraftClientPayloadInstaller payload,
        string stableVersion) =>
        new(
            Path.Combine(_root, "instances"),
            Path.Combine(_root, "staging"),
            registry,
            new StubReleaseCatalog(stableVersion),
            payload);

    private static MinecraftClientInstallRequest CreateRequest() =>
        new(
            Guid.NewGuid(),
            "Vanilla 26.2",
            MinecraftClientEdition.Java,
            "26.2",
            MinecraftClientLoader.Vanilla,
            null,
            MinecraftClientMemoryMode.Automatic,
            2_048,
            4_096,
            1280,
            720,
            false,
            JavaMajorVersion: 21);

    private static MinecraftClientInstance CreateStoredInstance(Guid id, string directoryPath) =>
        new()
        {
            Id = id,
            Name = "Managed test",
            DirectoryPath = Path.GetFullPath(directoryPath),
            Edition = MinecraftClientEdition.Java,
            GameVersion = "26.2",
            InstalledVersionId = "26.2",
            MinimumMemoryMb = 2_048,
            MaximumMemoryMb = 4_096,
            WindowWidth = 1280,
            WindowHeight = 720,
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StubReleaseCatalog(string version) : IMinecraftReleaseCatalog
    {
        public Task<MinecraftReleaseCatalogSnapshot> GetStableReleasesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MinecraftReleaseCatalogSnapshot(
                version,
                DateTimeOffset.UtcNow,
                [new MinecraftReleaseInfo(
                    version,
                    DateTimeOffset.UtcNow,
                    new Uri($"https://piston-meta.mojang.com/v1/packages/{new string('a', 40)}/{version}.json"),
                    new string('a', 40),
                    1)]));
    }

    private sealed class FakePayloadInstaller(
        Func<string, CancellationToken, Task<string>> install)
        : IMinecraftClientPayloadInstaller
    {
        public int CallCount { get; private set; }

        public Task<string> InstallAsync(
            MinecraftClientInstallRequest request,
            string stagingDirectory,
            string? javaExecutablePath,
            IProgress<MinecraftClientInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return install(stagingDirectory, cancellationToken);
        }
    }
}
