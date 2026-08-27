using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerRegistryTests
{
    [Fact]
    public async Task Registry_RoundTripsAnAtomicIndependentSnapshot()
    {
        var layout = CreateLayout();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        var registration = Registration();

        await registry.UpsertAsync(registration);
        ((string[])registration.JvmArguments)[0] = "-Dmutated=true";

        var reloaded = new ProductServerRegistry(layout);
        await reloaded.LoadAsync();
        var stored = Assert.Single(reloaded.GetAll());
        Assert.Equal("-Dsafe=true", Assert.Single(stored.JvmArguments));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(layout.Data),
            path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../outside", "java/bin/java.exe")]
    [InlineData("server-one", "../../Windows/System32/cmd.exe")]
    [InlineData("C:/outside", "java/bin/java.exe")]
    public async Task Registry_RejectsPathsOutsideProductLayout(string serverPath, string runtimePath)
    {
        var layout = CreateLayout();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(
            Registration() with
            {
                ServerDirectory = serverPath,
                JavaRuntimePath = runtimePath,
            }));
    }

    [Fact]
    public async Task CorruptRegistry_IsRejectedInsteadOfBeingSilentlyReplaced()
    {
        var layout = CreateLayout();
        File.WriteAllText(
            Path.Combine(layout.Data, ProductServerRegistry.FileName),
            "{not-json");

        var registry = new ProductServerRegistry(layout);

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.LoadAsync());
        Assert.Equal("{not-json", File.ReadAllText(registry.FilePath));
    }

    [Fact]
    public async Task Registry_FileUsesVersionedBoundedEnvelope()
    {
        var layout = CreateLayout();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(Registration());

        using var json = JsonDocument.Parse(File.ReadAllText(registry.FilePath));

        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("servers").GetArrayLength());
        Assert.True(new FileInfo(registry.FilePath).Length < 1024 * 1024);
    }

    [Fact]
    public async Task ConcurrentUpserts_DoNotLoseCommittedServers()
    {
        var layout = CreateLayout();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        var registrations = Enumerable.Range(0, 16)
            .Select(index => Registration() with
            {
                Name = $"Server {index}",
                ServerDirectory = $"server-{index}",
            })
            .ToArray();

        await Task.WhenAll(registrations.Select(server => registry.UpsertAsync(server)));

        var reloaded = new ProductServerRegistry(layout);
        await reloaded.LoadAsync();
        Assert.Equal(16, reloaded.GetAll().Count);
        Assert.Equal(
            registrations.Select(server => server.Id).Order(),
            reloaded.GetAll().Select(server => server.Id).Order());
    }

    [Theory]
    [InlineData("999")]
    [InlineData("NotARealCore")]
    public async Task UnknownCoreType_IsRejected(string coreType)
    {
        var layout = CreateLayout();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => registry.UpsertAsync(Registration() with { CoreType = coreType }));
    }

    internal static ProductDataLayout CreateLayout()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-product-runtime-tests",
            Guid.NewGuid().ToString("N"));
        var layout = new ProductDataLayout(root);
        layout.EnsureCreated();
        return layout;
    }

    internal static ProductServerRegistration Registration(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "Service Test",
        ServerDirectory = "server-one",
        JavaRuntimePath = "java/bin/java.exe",
        LaunchKind = ProductServerLaunchKind.ExecutableJar,
        ServerJarPath = "server.jar",
        CoreType = "Paper",
        MinecraftVersion = "1.21.1",
        MinimumMemoryMb = 1024,
        MaximumMemoryMb = 2048,
        JvmArguments = new[] { "-Dsafe=true" },
        ServerArguments = new[] { "nogui" },
        Port = 25565,
    };
}
