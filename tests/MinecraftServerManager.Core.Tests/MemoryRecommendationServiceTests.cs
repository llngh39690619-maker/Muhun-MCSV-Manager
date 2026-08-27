using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class MemoryRecommendationServiceTests
{
    private const long Mebibyte = 1024L * 1024L;
    private const long Gibibyte = 1024L * Mebibyte;

    [Theory]
    [InlineData(0, 1024, 2048)]
    [InlineData(1, 2048, 4096)]
    [InlineData(50, 2048, 4096)]
    [InlineData(51, 3072, 6144)]
    [InlineData(120, 3072, 6144)]
    [InlineData(121, 4096, 8192)]
    [InlineData(220, 4096, 8192)]
    [InlineData(221, 6144, 10240)]
    [InlineData(350, 6144, 10240)]
    [InlineData(351, 8192, 12288)]
    public void Recommend_UsesDeterministicTopLevelJarCountTiers(
        int jarCount,
        int expectedMinimumMb,
        int expectedMaximumMb)
    {
        using var server = new TemporaryDirectory();
        CreateJarFiles(Path.Combine(server.Path, "mods"), jarCount);
        var service = CreateService(totalGib: 64, availableGib: 64);

        var result = service.Recommend(server.Path);

        Assert.Equal(jarCount, result.AddonJarCount);
        Assert.Equal(expectedMinimumMb, result.MinimumMemoryMb);
        Assert.Equal(expectedMaximumMb, result.MaximumMemoryMb);
        Assert.False(result.WasConstrainedBySystemMemory);
    }

    [Fact]
    public void Recommend_ScansOnlyTopLevelJarMetadataInModsAndPlugins()
    {
        using var server = new TemporaryDirectory();
        var mods = Directory.CreateDirectory(Path.Combine(server.Path, "mods")).FullName;
        var plugins = Directory.CreateDirectory(Path.Combine(server.Path, "plugins")).FullName;
        Directory.CreateDirectory(Path.Combine(mods, "nested"));

        CreateSizedFile(Path.Combine(mods, "first.jar"), 123);
        CreateSizedFile(Path.Combine(mods, "SECOND.JAR"), 456);
        CreateSizedFile(Path.Combine(plugins, "third.jar"), 789);
        CreateSizedFile(Path.Combine(mods, "not-a-jar.zip"), 1000);
        CreateSizedFile(Path.Combine(mods, "nested", "ignored.jar"), 2000);
        var service = CreateService(totalGib: 64, availableGib: 64);

        var result = service.Recommend(server.Path);

        Assert.Equal(3, result.AddonJarCount);
        Assert.Equal(123 + 456 + 789, result.AddonJarBytes);
    }

    [Fact]
    public void Recommend_LowAvailableRamClampsToAValidRangeAndPreservesReserve()
    {
        using var server = new TemporaryDirectory();
        CreateJarFiles(Path.Combine(server.Path, "mods"), 121);
        var service = CreateService(totalGib: 8, availableGib: 5);

        var result = service.Recommend(server.Path);

        Assert.Equal(4096, result.ReservedSystemMemoryMb);
        Assert.Equal(1024, result.SafeAllocationCeilingMb);
        Assert.Equal(1024, result.MinimumMemoryMb);
        Assert.Equal(1024, result.MaximumMemoryMb);
        Assert.True(result.WasConstrainedBySystemMemory);
        Assert.InRange(result.MinimumMemoryMb, 1, result.MaximumMemoryMb);
    }

    [Fact]
    public void Recommend_ReservesTwentyPercentWhenItExceedsFourGibibytes()
    {
        using var server = new TemporaryDirectory();
        var service = CreateService(totalGib: 64, availableGib: 64);

        var result = service.Recommend(server.Path);

        Assert.Equal(13108, result.ReservedSystemMemoryMb);
        Assert.True(result.SafeAllocationCeilingMb <= 65536 - result.ReservedSystemMemoryMb);
    }

    private static MemoryRecommendationService CreateService(int totalGib, int availableGib)
        => new(new FixedMemoryProbe(new SystemMemorySnapshot(
            totalGib * Gibibyte,
            availableGib * Gibibyte)));

    private static void CreateJarFiles(string directory, int count)
    {
        Directory.CreateDirectory(directory);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllBytes(Path.Combine(directory, $"mod-{index}.jar"), []);
        }
    }

    private static void CreateSizedFile(string path, long size)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(size);
    }

    private sealed class FixedMemoryProbe(SystemMemorySnapshot snapshot) : ISystemMemoryProbe
    {
        public SystemMemorySnapshot GetSnapshot() => snapshot;
    }
}
