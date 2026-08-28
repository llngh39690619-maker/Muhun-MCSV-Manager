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
