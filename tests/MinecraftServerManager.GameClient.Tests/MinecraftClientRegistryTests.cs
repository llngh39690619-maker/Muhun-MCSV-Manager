using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-client-registry-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Registry_RoundTripsClientSettingsWithoutCredentials()
    {
        var path = Path.Combine(_root, "client-instances.v1.json");
        var javaPath = Path.Combine(_root, "runtimes", "java-21", "bin", "java.exe");
        var startedAtUtc = new DateTimeOffset(2026, 8, 28, 1, 2, 3, TimeSpan.Zero);
        var instance = new MinecraftClientInstance
        {
            Name = "Fabric 1.21.8",
            DirectoryPath = Path.Combine(_root, "instances", "fabric"),
            GameVersion = "1.21.8",
            InstalledVersionId = "fabric-loader-0.17.2-1.21.8",
            Loader = MinecraftClientLoader.Fabric,
            LoaderVersion = "0.17.2",
            AccountId = "account-reference-only",
            MemoryMode = MinecraftClientMemoryMode.Automatic,
            CatalogProvider = "modrinth",
            CatalogProjectId = "PackGood1",
            CatalogVersionId = "StableV1",
            CatalogIconUri = new Uri("https://cdn.modrinth.com/data/PackGood1/icon.png"),
            CatalogPreviewUri = new Uri("https://cdn.modrinth.com/data/PackGood1/images/preview.png"),
            JavaExecutablePath = javaPath,
            ActiveProcessId = 42_424,
            ActiveProcessStartedAtUtc = startedAtUtc,
            ActiveProcessExecutablePath = javaPath,
        };

        using (var registry = new MinecraftClientRegistry(path))
        {
            await registry.SaveAsync(new MinecraftClientRegistryDocument
            {
                Instances = [instance],
            });
        }

        using var reopened = new MinecraftClientRegistry(path);
        var loadedDocument = await reopened.LoadAsync();
        var loaded = Assert.Single(loadedDocument.Instances);
        Assert.Equal(instance.Id, loaded.Id);
        Assert.Equal(MinecraftClientLoader.Fabric, loaded.Loader);
        Assert.Equal("account-reference-only", loaded.AccountId);
        Assert.Equal("modrinth", loaded.CatalogProvider);
        Assert.Equal("PackGood1", loaded.CatalogProjectId);
        Assert.Equal("StableV1", loaded.CatalogVersionId);
        Assert.Equal("cdn.modrinth.com", loaded.CatalogPreviewUri?.Host);
        Assert.Equal(42_424, loaded.ActiveProcessId);
        Assert.Equal(startedAtUtc, loaded.ActiveProcessStartedAtUtc);
        Assert.Equal(javaPath, loaded.ActiveProcessExecutablePath);
        Assert.Equal(MinecraftClientRegistryDocument.CurrentSchemaVersion, loadedDocument.SchemaVersion);
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registry_LoadsSchemaOneWithoutAProcessMarkerAndUpgradesOnNextSave()
    {
        var path = Path.Combine(_root, "legacy-registry.json");
        var directory = Path.Combine(_root, "instances", "legacy");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            $$"""
              {
                "schemaVersion": 1,
                "instances": [
                  {
                    "id": "{{Guid.NewGuid()}}",
                    "name": "Legacy instance",
                    "directoryPath": "{{directory.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                    "gameVersion": "1.20.1",
                    "installedVersionId": "1.20.1"
                  }
                ]
              }
              """);

        using var registry = new MinecraftClientRegistry(path);
        var document = await registry.LoadAsync();

        var instance = Assert.Single(document.Instances);
        Assert.Equal(1, document.SchemaVersion);
        Assert.Null(instance.ActiveProcessId);
        Assert.Null(instance.ActiveProcessStartedAtUtc);
        Assert.Null(instance.ActiveProcessExecutablePath);

        await registry.SaveAsync(document);
        Assert.Equal(
            MinecraftClientRegistryDocument.CurrentSchemaVersion,
            (await registry.LoadAsync()).SchemaVersion);
    }

    [Fact]
    public async Task Registry_RejectsPartialOrNonJavaProcessMarkers()
    {
        var partial = CreateInstance("partial", Path.Combine(_root, "instances", "partial"));
        partial.ActiveProcessId = 123;
        var nonJava = CreateInstance("non-java", Path.Combine(_root, "instances", "non-java"));
        nonJava.ActiveProcessId = 456;
        nonJava.ActiveProcessStartedAtUtc = DateTimeOffset.UtcNow;
        nonJava.ActiveProcessExecutablePath = Path.Combine(_root, "notepad.exe");
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "process-registry.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [partial] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [nonJava] }));
    }

    [Fact]
    public async Task Registry_RejectsDuplicateActiveProcessIdentities()
    {
        var javaPath = Path.Combine(_root, "runtime", "bin", "javaw.exe");
        var first = CreateInstance("first", Path.Combine(_root, "instances", "first"));
        var second = CreateInstance("second", Path.Combine(_root, "instances", "second"));
        foreach (var instance in new[] { first, second })
        {
            instance.ActiveProcessId = 7_777;
            instance.ActiveProcessStartedAtUtc = new DateTimeOffset(2026, 8, 28, 2, 3, 4, TimeSpan.Zero);
            instance.ActiveProcessExecutablePath = javaPath;
        }

        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "duplicate-process.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [first, second] }));
    }

    [Fact]
    public async Task Registry_RejectsDuplicateInstanceDirectories()
    {
        var directory = Path.Combine(_root, "instances", "same");
        var document = new MinecraftClientRegistryDocument
        {
            Instances =
            [
                CreateInstance("one", directory),
                CreateInstance("two", directory.ToUpperInvariant()),
            ],
        };
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "registry.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(document));
    }

    [Fact]
    public async Task Registry_RejectsBedrockBecauseItUsesAnIndependentShortcutRegistry()
    {
        var instance = CreateInstance(
            "bedrock",
            Path.Combine(_root, "instances", "bedrock"));
        instance.Edition = MinecraftClientEdition.Bedrock;
        using var registry = new MinecraftClientRegistry(
            Path.Combine(_root, "bedrock-must-not-enter-java-registry.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [instance] }));
    }

    [Fact]
    public async Task Registry_RejectsUntrustedCatalogMediaUri()
    {
        var instance = CreateInstance("unsafe", Path.Combine(_root, "instances", "unsafe"));
        instance.CatalogProvider = "modrinth";
        instance.CatalogProjectId = "PackGood1";
        instance.CatalogVersionId = "StableV1";
        instance.CatalogIconUri = new Uri("https://example.invalid/icon.png");
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "unsafe-registry.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [instance] }));
    }

    [Fact]
    public async Task Registry_RejectsInvalidFtbIdentityAndNonFtbArtworkHost()
    {
        var invalidIdentity = CreateInstance(
            "invalid-ftb-id",
            Path.Combine(_root, "instances", "invalid-ftb-id"));
        invalidIdentity.CatalogProvider = "ftb";
        invalidIdentity.CatalogProjectId = "0130";
        invalidIdentity.CatalogVersionId = "100140";
        var invalidArtwork = CreateInstance(
            "invalid-ftb-art",
            Path.Combine(_root, "instances", "invalid-ftb-art"));
        invalidArtwork.CatalogProvider = "ftb";
        invalidArtwork.CatalogProjectId = "130";
        invalidArtwork.CatalogVersionId = "100140";
        invalidArtwork.CatalogIconUri = new Uri("https://example.invalid/icon.png");
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "unsafe-ftb-registry.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [invalidIdentity] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [invalidArtwork] }));
    }

    [Fact]
    public async Task Registry_AcceptsExactCurseForgeIdentityAndOfficialArtworkHost()
    {
        var instance = CreateInstance(
            "curseforge",
            Path.Combine(_root, "instances", "curseforge"));
        instance.CatalogProvider = "curseforge";
        instance.CatalogProjectId = "285109";
        instance.CatalogVersionId = "4612979";
        instance.CatalogIconUri = new Uri("https://media.forgecdn.net/avatars/123/icon.png");
        instance.CatalogPreviewUri = new Uri("https://mediafilez.forgecdn.net/screenshots/preview.jpg");
        using var registry = new MinecraftClientRegistry(Path.Combine(_root, "curseforge-registry.json"));

        await registry.SaveAsync(new MinecraftClientRegistryDocument { Instances = [instance] });

        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Equal("curseforge", stored.CatalogProvider);
        Assert.Equal("285109", stored.CatalogProjectId);
        Assert.Equal("4612979", stored.CatalogVersionId);
    }

    [Fact]
    public async Task Registry_RejectsInvalidCurseForgeIdentityAndNonOfficialArtworkHost()
    {
        var invalidIdentity = CreateInstance(
            "invalid-curseforge-id",
            Path.Combine(_root, "instances", "invalid-curseforge-id"));
        invalidIdentity.CatalogProvider = "curseforge";
        invalidIdentity.CatalogProjectId = "0285109";
        invalidIdentity.CatalogVersionId = "4612979";
        var invalidArtwork = CreateInstance(
            "invalid-curseforge-art",
            Path.Combine(_root, "instances", "invalid-curseforge-art"));
        invalidArtwork.CatalogProvider = "curseforge";
        invalidArtwork.CatalogProjectId = "285109";
        invalidArtwork.CatalogVersionId = "4612979";
        invalidArtwork.CatalogIconUri = new Uri("https://forgecdn.net.example.invalid/icon.png");
        using var registry = new MinecraftClientRegistry(
            Path.Combine(_root, "unsafe-curseforge-registry.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [invalidIdentity] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SaveAsync(
            new MinecraftClientRegistryDocument { Instances = [invalidArtwork] }));
    }

    [Fact]
    public async Task Registry_DisposeWaitsForDurableInFlightCommitBeforeClosingStore()
    {
        var path = Path.Combine(_root, "dispose-during-commit.json");
        using var durableSaveReached = new ManualResetEventSlim();
        using var allowCommitReturn = new ManualResetEventSlim();
        var instance = CreateInstance(
            "durable",
            Path.Combine(_root, "instances", "durable"));
        var registry = new MinecraftClientRegistry(
            path,
            () =>
            {
                durableSaveReached.Set();
                Assert.True(allowCommitReturn.Wait(TimeSpan.FromSeconds(10)));
            });
        try
        {
            var update = Task.Run(() => registry.UpdateAsync(
                document =>
                {
                    document.Instances.Add(instance);
                    return true;
                }));
            Assert.True(durableSaveReached.Wait(TimeSpan.FromSeconds(10)));

            var dispose = Task.Run(registry.Dispose);
            await Task.Delay(100);
            Assert.False(dispose.IsCompleted);

            allowCommitReturn.Set();
            Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(10)));
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));

            using var reopened = new MinecraftClientRegistry(path);
            Assert.Equal(instance.Id, Assert.Single((await reopened.LoadAsync()).Instances).Id);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.LoadAsync());
        }
        finally
        {
            allowCommitReturn.Set();
            registry.Dispose();
        }
    }

    private static MinecraftClientInstance CreateInstance(string name, string directory) => new()
    {
        Name = name,
        DirectoryPath = directory,
        GameVersion = "1.20.1",
        InstalledVersionId = "1.20.1",
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
