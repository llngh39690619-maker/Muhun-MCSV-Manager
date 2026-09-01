using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
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
        var registration = Registration() with
        {
            MemoryAllocationMode = ProductServerMemoryAllocationMode.Automatic,
            SeparateDiagnosticOutput = true,
            EnableHangWatchdog = true,
            WatchdogCheckIntervalSeconds = 45,
            WatchdogProbeTimeoutSeconds = 9,
            WatchdogFailureThreshold = 4,
            WatchdogStartupGraceSeconds = 240,
            EnableAutomaticRecoveryPoints = true,
            RecoveryPointIntervalMinutes = 60,
            RecoveryPointRetentionCount = 5,
        };

        await registry.UpsertAsync(registration);
        ((string[])registration.JvmArguments)[0] = "-Dmutated=true";

        var reloaded = new ProductServerRegistry(layout);
        await reloaded.LoadAsync();
        var stored = Assert.Single(reloaded.GetAll());
        Assert.Equal("-Dsafe=true", Assert.Single(stored.JvmArguments));
        Assert.Equal(registration.MemoryAllocationMode, stored.MemoryAllocationMode);
        Assert.Equal(registration.SeparateDiagnosticOutput, stored.SeparateDiagnosticOutput);
        Assert.Equal(registration.EnableHangWatchdog, stored.EnableHangWatchdog);
        Assert.Equal(registration.WatchdogCheckIntervalSeconds, stored.WatchdogCheckIntervalSeconds);
        Assert.Equal(registration.WatchdogProbeTimeoutSeconds, stored.WatchdogProbeTimeoutSeconds);
        Assert.Equal(registration.WatchdogFailureThreshold, stored.WatchdogFailureThreshold);
        Assert.Equal(registration.WatchdogStartupGraceSeconds, stored.WatchdogStartupGraceSeconds);
        Assert.Equal(
            registration.EnableAutomaticRecoveryPoints,
            stored.EnableAutomaticRecoveryPoints);
        Assert.Equal(registration.RecoveryPointIntervalMinutes, stored.RecoveryPointIntervalMinutes);
        Assert.Equal(registration.RecoveryPointRetentionCount, stored.RecoveryPointRetentionCount);
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

    [Fact]
    public async Task LegacyRegistryWithoutServiceInstanceSettings_LoadsSafeDefaultsAndMigrates()
    {
        var layout = CreateLayout();
        var id = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(layout.Data, ProductServerRegistry.FileName),
            $$"""
            {
              "schemaVersion": 1,
              "servers": [
                {
                  "id": "{{id}}",
                  "name": "Legacy Service Server",
                  "serverDirectory": "legacy-server",
                  "javaRuntimePath": "java/bin/java.exe",
                  "launchKind": 0,
                  "serverJarPath": "server.jar",
                  "javaArgumentFilePaths": [],
                  "coreType": "Paper",
                  "minecraftVersion": "1.21.1",
                  "minimumMemoryMb": 1536,
                  "maximumMemoryMb": 3072,
                  "jvmArguments": [],
                  "serverArguments": ["nogui"],
                  "port": 25565,
                  "autoRestart": false,
                  "modpackSource": 0,
                  "isInstallerArtifact": false
                }
              ]
            }
            """);

        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        var stored = Assert.Single(registry.GetAll());

        Assert.Equal(ProductServerMemoryAllocationMode.Manual, stored.MemoryAllocationMode);
        Assert.True(stored.SeparateDiagnosticOutput);
        Assert.False(stored.EnableHangWatchdog);
        Assert.Equal(30, stored.WatchdogCheckIntervalSeconds);
        Assert.Equal(8, stored.WatchdogProbeTimeoutSeconds);
        Assert.Equal(3, stored.WatchdogFailureThreshold);
        Assert.Equal(180, stored.WatchdogStartupGraceSeconds);
        Assert.False(stored.EnableAutomaticRecoveryPoints);
        Assert.Equal(30, stored.RecoveryPointIntervalMinutes);
        Assert.Equal(3, stored.RecoveryPointRetentionCount);

        await registry.UpsertAsync(stored);
        var reloaded = new ProductServerRegistry(layout);
        await reloaded.LoadAsync();
        var migrated = Assert.Single(reloaded.GetAll());
        Assert.Equal(stored.Id, migrated.Id);
        Assert.Equal(stored.MemoryAllocationMode, migrated.MemoryAllocationMode);
        Assert.Equal(stored.SeparateDiagnosticOutput, migrated.SeparateDiagnosticOutput);
        Assert.Equal(stored.WatchdogCheckIntervalSeconds, migrated.WatchdogCheckIntervalSeconds);
        Assert.Equal(stored.RecoveryPointRetentionCount, migrated.RecoveryPointRetentionCount);
    }

    [Fact]
    public async Task Registry_RejectsUnknownMemoryModeAndEveryInvalidReliabilityBoundary()
    {
        var layout = CreateLayout();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        var invalid = new[]
        {
            Registration() with { MemoryAllocationMode = (ProductServerMemoryAllocationMode)99 },
            Registration() with { WatchdogCheckIntervalSeconds = 9 },
            Registration() with { WatchdogCheckIntervalSeconds = 301 },
            Registration() with { WatchdogProbeTimeoutSeconds = 1 },
            Registration() with { WatchdogProbeTimeoutSeconds = 31 },
            Registration() with { WatchdogProbeTimeoutSeconds = 30, WatchdogCheckIntervalSeconds = 30 },
            Registration() with { WatchdogFailureThreshold = 1 },
            Registration() with { WatchdogFailureThreshold = 11 },
            Registration() with { WatchdogStartupGraceSeconds = 29 },
            Registration() with { WatchdogStartupGraceSeconds = 3601 },
            Registration() with { RecoveryPointIntervalMinutes = 9 },
            Registration() with { RecoveryPointIntervalMinutes = 1441 },
            Registration() with { RecoveryPointRetentionCount = 0 },
            Registration() with { RecoveryPointRetentionCount = 21 },
        };

        foreach (var registration in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(registration));
        }
    }

    [Theory]
    [InlineData(ProductServerMemoryAllocationMode.Manual, MemoryAllocationMode.Manual)]
    [InlineData(ProductServerMemoryAllocationMode.UseManagerDefault, MemoryAllocationMode.UseManagerDefault)]
    [InlineData(ProductServerMemoryAllocationMode.Automatic, MemoryAllocationMode.Automatic)]
    public void LaunchSnapshot_MapsAllServiceInstanceSettings(
        ProductServerMemoryAllocationMode productMode,
        MemoryAllocationMode coreMode)
    {
        var layout = CreateLayout();
        var registration = Registration() with
        {
            MemoryAllocationMode = productMode,
            SeparateDiagnosticOutput = true,
            EnableHangWatchdog = true,
            WatchdogCheckIntervalSeconds = 45,
            WatchdogProbeTimeoutSeconds = 9,
            WatchdogFailureThreshold = 4,
            WatchdogStartupGraceSeconds = 240,
            EnableAutomaticRecoveryPoints = true,
            RecoveryPointIntervalMinutes = 60,
            RecoveryPointRetentionCount = 5,
        };
        var snapshot = new ServerInstance();

        ProductServerRuntime.ApplyRegistrationLaunchSnapshot(snapshot, registration, layout);

        Assert.Equal(registration.MinimumMemoryMb, snapshot.MinimumMemoryMb);
        Assert.Equal(registration.MaximumMemoryMb, snapshot.MaximumMemoryMb);
        Assert.Equal(coreMode, snapshot.MemoryAllocationMode);
        Assert.True(snapshot.SeparateDiagnosticOutput);
        Assert.True(snapshot.EnableHangWatchdog);
        Assert.Equal(45, snapshot.WatchdogCheckIntervalSeconds);
        Assert.Equal(9, snapshot.WatchdogProbeTimeoutSeconds);
        Assert.Equal(4, snapshot.WatchdogFailureThreshold);
        Assert.Equal(240, snapshot.WatchdogStartupGraceSeconds);
        Assert.True(snapshot.EnableAutomaticRecoveryPoints);
        Assert.Equal(60, snapshot.RecoveryPointIntervalMinutes);
        Assert.Equal(5, snapshot.RecoveryPointRetentionCount);
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
