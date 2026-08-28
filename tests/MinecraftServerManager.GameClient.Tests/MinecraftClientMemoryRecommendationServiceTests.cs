using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientMemoryRecommendationServiceTests : IDisposable
{
    private const long Mebibyte = 1024L * 1024L;
    private const long Gibibyte = 1024L * Mebibyte;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-client-memory-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(MinecraftClientLoader.Vanilla, 0, 1024, 3072)]
    [InlineData(MinecraftClientLoader.Fabric, 0, 1536, 3584)]
    [InlineData(MinecraftClientLoader.Forge, 0, 2048, 4096)]
    [InlineData(MinecraftClientLoader.Vanilla, 1, 2048, 4096)]
    [InlineData(MinecraftClientLoader.Vanilla, 40, 2048, 4096)]
    [InlineData(MinecraftClientLoader.Vanilla, 41, 3072, 6144)]
    [InlineData(MinecraftClientLoader.Vanilla, 101, 4096, 8192)]
    [InlineData(MinecraftClientLoader.Vanilla, 201, 6144, 10240)]
    [InlineData(MinecraftClientLoader.Vanilla, 351, 8192, 12288)]
    public void Resolve_AutomaticUsesLoaderAndBoundedModCountTiers(
        MinecraftClientLoader loader,
        int modCount,
        int expectedMinimumMb,
        int expectedMaximumMb)
    {
        var instance = CreateInstance(loader, MinecraftClientMemoryMode.Automatic);
        CreateModFiles(instance.DirectoryPath, modCount);
        var service = CreateService(totalGib: 64, availableGib: 64);

        var result = service.Resolve(instance, new NewMinecraftClientDefaultsSettings());

        Assert.Equal(MinecraftClientMemoryMode.Automatic, result.EffectiveMode);
        Assert.Equal(expectedMinimumMb, result.MinimumMemoryMb);
        Assert.Equal(expectedMaximumMb, result.MaximumMemoryMb);
        Assert.Equal(modCount, result.InstalledModJarCount);
        Assert.False(result.WasConstrainedBySafetyCeiling);
    }

    [Fact]
    public void Resolve_AutomaticScansOnlyTopLevelRegularJarMetadata()
    {
        var instance = CreateInstance(MinecraftClientLoader.Fabric, MinecraftClientMemoryMode.Automatic);
        var mods = Directory.CreateDirectory(Path.Combine(instance.DirectoryPath, "mods")).FullName;
        Directory.CreateDirectory(Path.Combine(mods, "nested"));
        CreateSizedFile(Path.Combine(mods, "one.jar"), 123);
        CreateSizedFile(Path.Combine(mods, "TWO.JAR"), 456);
        CreateSizedFile(Path.Combine(mods, "disabled.jar.disabled"), 789);
        CreateSizedFile(Path.Combine(mods, "nested", "ignored.jar"), 1_000);
        var service = CreateService(totalGib: 64, availableGib: 64);

        var result = service.Resolve(instance, new NewMinecraftClientDefaultsSettings());

        Assert.Equal(2, result.InstalledModJarCount);
        Assert.Equal(579, result.InstalledModJarBytes);
    }

    [Fact]
    public void Resolve_AutomaticBoundsTheMetadataScan()
    {
        var instance = CreateInstance(MinecraftClientLoader.Forge, MinecraftClientMemoryMode.Automatic);
        CreateModFiles(
            instance.DirectoryPath,
            MinecraftClientMemoryRecommendationService.MaximumScannedModFiles + 1);

        var result = CreateService(totalGib: 64, availableGib: 64)
            .Resolve(instance, new NewMinecraftClientDefaultsSettings());

        Assert.Equal(
            MinecraftClientMemoryRecommendationService.MaximumScannedModFiles,
            result.InstalledModJarCount);
        Assert.True(result.ModScanTruncated);
        Assert.Equal(8192, result.MinimumMemoryMb);
        Assert.Equal(12288, result.MaximumMemoryMb);
    }

    [Fact]
    public void Resolve_UseGlobalDefaultAutomaticStillInspectsTheInstance()
    {
        var instance = CreateInstance(
            MinecraftClientLoader.NeoForge,
            MinecraftClientMemoryMode.UseGlobalDefault);
        CreateModFiles(instance.DirectoryPath, 101);
        var defaults = new NewMinecraftClientDefaultsSettings
        {
            MemoryMode = MinecraftClientMemoryMode.Automatic,
        };

        var result = CreateService(64, 64).Resolve(instance, defaults);

        Assert.Equal(MinecraftClientMemoryMode.UseGlobalDefault, result.ConfiguredMode);
        Assert.Equal(MinecraftClientMemoryMode.Automatic, result.EffectiveMode);
        Assert.Equal(4096, result.MinimumMemoryMb);
        Assert.Equal(8192, result.MaximumMemoryMb);
        Assert.Equal(101, result.InstalledModJarCount);
    }

    [Fact]
    public void Resolve_UseGlobalDefaultManualUsesTheSavedRange()
    {
        var instance = CreateInstance(
            MinecraftClientLoader.Vanilla,
            MinecraftClientMemoryMode.UseGlobalDefault);
        var defaults = new NewMinecraftClientDefaultsSettings
        {
            MemoryMode = MinecraftClientMemoryMode.Manual,
            MinimumMemoryMb = 3072,
            MaximumMemoryMb = 7168,
        };

        var result = CreateService(64, 64).Resolve(instance, defaults);

        Assert.Equal(MinecraftClientMemoryMode.Manual, result.EffectiveMode);
        Assert.Equal(3072, result.MinimumMemoryMb);
        Assert.Equal(7168, result.MaximumMemoryMb);
        Assert.Equal(0, result.InstalledModJarCount);
    }

    [Fact]
    public void Resolve_ManualRangeIsReducedWhenAvailableMemoryCannotPreserveTheReserve()
    {
        var instance = CreateInstance(MinecraftClientLoader.Vanilla, MinecraftClientMemoryMode.Manual);
        instance.MinimumMemoryMb = 4096;
        instance.MaximumMemoryMb = 8192;

        var result = CreateService(totalGib: 8, availableGib: 5)
            .Resolve(instance, new NewMinecraftClientDefaultsSettings());

        Assert.Equal(4096, result.ReservedSystemMemoryMb);
        Assert.Equal(1024, result.SystemSafeAllocationCeilingMb);
        Assert.Equal(1024, result.MinimumMemoryMb);
        Assert.Equal(1024, result.MaximumMemoryMb);
        Assert.True(result.WasConstrainedBySafetyCeiling);
    }

    [Fact]
    public void Resolve_ManualRangeNeverExceedsTheProductHeapSafetyCap()
    {
        var instance = CreateInstance(MinecraftClientLoader.Vanilla, MinecraftClientMemoryMode.Manual);
        instance.MinimumMemoryMb = 16_384;
        instance.MaximumMemoryMb = 131_072;

        var result = CreateService(totalGib: 256, availableGib: 256)
            .Resolve(instance, new NewMinecraftClientDefaultsSettings());

        Assert.True(result.SystemSafeAllocationCeilingMb >
                    MinecraftClientMemoryRecommendationService.MaximumClientHeapMb);
        Assert.Equal(
            MinecraftClientMemoryRecommendationService.MaximumClientHeapMb,
            result.EffectiveAllocationCeilingMb);
        Assert.Equal(16_384, result.MinimumMemoryMb);
        Assert.Equal(32_768, result.MaximumMemoryMb);
        Assert.True(result.WasConstrainedBySafetyCeiling);
    }

    [Theory]
    [InlineData(511, 4096)]
    [InlineData(4096, 2048)]
    [InlineData(2048, 262145)]
    public void Resolve_ManualRejectsInvalidConfiguredRanges(int minimumMb, int maximumMb)
    {
        var instance = CreateInstance(MinecraftClientLoader.Vanilla, MinecraftClientMemoryMode.Manual);
        instance.MinimumMemoryMb = minimumMb;
        instance.MaximumMemoryMb = maximumMb;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateService(64, 64).Resolve(instance, new NewMinecraftClientDefaultsSettings()));
    }

    [Fact]
    public void Resolve_ReservesTwentyPercentWhenThatExceedsFourGibibytes()
    {
        var instance = CreateInstance(MinecraftClientLoader.Vanilla, MinecraftClientMemoryMode.Automatic);

        var result = CreateService(totalGib: 64, availableGib: 64)
            .Resolve(instance, new NewMinecraftClientDefaultsSettings());

        Assert.Equal(13_108, result.ReservedSystemMemoryMb);
        Assert.True(result.SystemSafeAllocationCeilingMb <= 65_536 - result.ReservedSystemMemoryMb);
    }

    [Fact]
    public void Resolve_ReportsAConservativeFallbackProbe()
    {
        var instance = CreateInstance(MinecraftClientLoader.Vanilla, MinecraftClientMemoryMode.Automatic);
        var service = new MinecraftClientMemoryRecommendationService(
            new FixedMemoryProbe(new SystemMemorySnapshot(
                8 * Gibibyte,
                4 * Gibibyte,
                IsFallback: true)));

        var result = service.Resolve(instance, new NewMinecraftClientDefaultsSettings());

        Assert.True(result.UsedFallbackMemoryProbe);
        Assert.Contains("conservative fallback", result.Explanation, StringComparison.Ordinal);
        Assert.Equal(512, result.MaximumMemoryMb);
    }

    [Fact]
    public void Resolve_RejectsRecursiveGlobalDefaultMode()
    {
        var instance = CreateInstance(
            MinecraftClientLoader.Vanilla,
            MinecraftClientMemoryMode.UseGlobalDefault);
        var defaults = new NewMinecraftClientDefaultsSettings
        {
            MemoryMode = MinecraftClientMemoryMode.UseGlobalDefault,
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => CreateService(16, 16).Resolve(instance, defaults));

        Assert.Contains("cannot reference itself", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsBedrockBecauseItHasNoJavaHeap()
    {
        var instance = CreateInstance(MinecraftClientLoader.Vanilla, MinecraftClientMemoryMode.Automatic);
        instance.Edition = MinecraftClientEdition.Bedrock;

        Assert.Throws<NotSupportedException>(
            () => CreateService(16, 16).Resolve(instance, new NewMinecraftClientDefaultsSettings()));
    }

    [Fact]
    public async Task ResolveAsync_ObservesCancellationBeforeScanning()
    {
        var instance = CreateInstance(MinecraftClientLoader.Vanilla, MinecraftClientMemoryMode.Automatic);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(16, 16).ResolveAsync(
                instance,
                new NewMinecraftClientDefaultsSettings(),
                cancellation.Token));
    }

    private MinecraftClientInstance CreateInstance(
        MinecraftClientLoader loader,
        MinecraftClientMemoryMode mode)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new MinecraftClientInstance
        {
            Name = "Memory test",
            DirectoryPath = directory,
            GameVersion = "1.21.8",
            Loader = loader,
            MemoryMode = mode,
        };
    }

    private static MinecraftClientMemoryRecommendationService CreateService(
        int totalGib,
        int availableGib)
        => new(new FixedMemoryProbe(new SystemMemorySnapshot(
            totalGib * Gibibyte,
            availableGib * Gibibyte)));

    private static void CreateModFiles(string instanceDirectory, int count)
    {
        var mods = Directory.CreateDirectory(Path.Combine(instanceDirectory, "mods")).FullName;
        for (var index = 0; index < count; index++)
        {
            File.WriteAllBytes(Path.Combine(mods, $"mod-{index}.jar"), []);
        }
    }

    private static void CreateSizedFile(string path, long size)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(size);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedMemoryProbe(SystemMemorySnapshot snapshot) : ISystemMemoryProbe
    {
        public SystemMemorySnapshot GetSnapshot() => snapshot;
    }
}
