using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class MinecraftWorldLayoutResolverTests
{
    [Fact]
    public async Task ResolveAsync_MissingConfigurationUsesVanillaWorldLayout()
    {
        using var temporaryDirectory = new TemporaryDirectory();

        var result = await new MinecraftWorldLayoutResolver().ResolveAsync(temporaryDirectory.Path);

        Assert.Equal("world", result.LevelName);
        Assert.Equal(".", result.WorldContainerRelativePath);
        Assert.Equal(["world", "world_nether", "world_the_end"], result.RelativeWorldDirectories);
    }

    [Fact]
    public async Task ResolveAsync_DecodesLevelNameAndAppliesRootConfinedBukkitContainer()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory.Path, "server.properties"),
            "motd=test\nlevel-name=My\\ World\n");
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory.Path, "bukkit.yml"),
            "settings:\n  world-container: worlds # stored below the server root\n");
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "worlds", "My World"));

        var result = await new MinecraftWorldLayoutResolver().ResolveAsync(temporaryDirectory.Path);

        Assert.Equal("My World", result.LevelName);
        Assert.Equal("worlds", result.WorldContainerRelativePath);
        Assert.Equal(
            ["worlds/My World", "worlds/My World_nether", "worlds/My World_the_end"],
            result.RelativeWorldDirectories);
    }

    [Fact]
    public async Task ResolveAsync_LastActiveLevelNameWinsLikeJavaProperties()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory.Path, "server.properties"),
            "level-name=old\n# level-name=ignored\nlevel-name:new\n");

        var result = await new MinecraftWorldLayoutResolver().ResolveAsync(temporaryDirectory.Path);

        Assert.Equal("new", result.LevelName);
        Assert.Equal("new", result.RelativeWorldDirectories[0]);
    }

    [Fact]
    public async Task ResolveAsync_RejectsUnicodeEscapedTraversal()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory.Path, "server.properties"),
            "level-name=\\u002e\\u002e/outside\n");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MinecraftWorldLayoutResolver().ResolveAsync(temporaryDirectory.Path));

        Assert.Contains("根目錄外部", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RejectsExternalBukkitWorldContainerExplicitly()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var server = Path.Combine(temporaryDirectory.Path, "server");
        var outside = Path.Combine(temporaryDirectory.Path, "outside-worlds");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(
            Path.Combine(server, "bukkit.yml"),
            $"settings:\n  world-container: \"{outside}\"\n");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MinecraftWorldLayoutResolver().ResolveAsync(server));

        Assert.Contains("Bukkit world-container", error.Message, StringComparison.Ordinal);
        Assert.Contains("根目錄外部", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RejectsWorldReparsePointInsteadOfFollowingIt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var server = Path.Combine(temporaryDirectory.Path, "server");
        var outside = Path.Combine(temporaryDirectory.Path, "outside-world");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "level.dat"), "outside");
        var linkedWorld = Path.Combine(server, "world");
        ReparsePointTestHelper.CreateDirectoryLink(linkedWorld, outside);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new MinecraftWorldLayoutResolver().ResolveAsync(server));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedWorld);
        }
    }

    [Fact]
    public async Task ResolveAsync_RejectsOversizedMetadataBeforeAllocatingItsDeclaredContents()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(
            Path.Combine(temporaryDirectory.Path, "server.properties"),
            new byte[1024 * 1024 + 1]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MinecraftWorldLayoutResolver().ResolveAsync(temporaryDirectory.Path));

        Assert.Contains("安全上限", error.Message, StringComparison.Ordinal);
    }
}
