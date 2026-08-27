using System.IO.Compression;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class ModpackUpdateBackupPlannerTests
{
    [Fact]
    public async Task CreatePlanAsync_BackupContainsOnlyUpdateDataAndNeverCoreArtifacts()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.Path;
        await WriteAsync("mods/old-mod.jar", "mod");
        await WriteAsync("plugins/old-plugin.jar", "plugin");
        await WriteAsync("config/cache/generated.toml", "config-cache-is-data");
        await WriteAsync("defaultconfigs/default.toml", "default");
        await WriteAsync("kubejs/server_scripts/main.js", "script");
        await WriteAsync("scripts/recipe.zs", "script");
        await WriteAsync("world/level.dat", "world");
        await WriteAsync("world/playerdata/player.dat", "player");
        await WriteAsync("world_nether/region/r.0.0.mca", "nether");
        await WriteAsync("world_the_end/DIM1/region/r.0.0.mca", "end");
        await WriteAsync("ops.json", "[]");
        await WriteAsync("whitelist.json", "[]");
        await WriteAsync("banned-ips.json", "[]");
        await WriteAsync("banned-players.json", "[]");
        await WriteAsync("usercache.json", "[]");
        await WriteAsync("server.properties", "level-name=world\nserver-port=25565\n");
        await WriteAsync("eula.txt", "eula=true");
        await WriteAsync("user_jvm_args.txt", "-Xmx8G");

        await WriteAsync("libraries/loader/core.jar", "core");
        await WriteAsync("versions/1.21.1/server.jar", "core");
        await WriteAsync("jre/bin/java.exe", "core");
        await WriteAsync("server.jar", "core");
        await WriteAsync("run.bat", "core");
        await WriteAsync("run.sh", "core");
        await WriteAsync("win_args.txt", "core");
        await WriteAsync("logs/latest.log", "log");
        await WriteAsync("cache/download.tmp", "cache");
        await WriteAsync("backups/old.zip", "backup");
        await WriteAsync("unrelated.bin", "not-allowlisted");
        await WriteAsync(".minecraft-server-manager.lock", "coordination");

        var instance = new ServerInstance
        {
            Name = "Example Pack",
            DirectoryPath = root,
            MinecraftVersion = "1.21.1",
            ModpackVersionName = "1.6.0"
        };
        var plan = await new ModpackUpdateBackupPlanner().CreatePlanAsync(instance, "1.7.0");

        Assert.False(plan.IsCompleteServerBackup);
        Assert.Contains("不能單獨啟動", plan.Notice, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(root, "backups", "modpack-updates"), plan.Options.DestinationDirectory);
        Assert.Contains("pre-update-1.6.0-to-1.7.0", plan.Options.ArchiveFileName, StringComparison.Ordinal);
        Assert.Contains("world", plan.IncludedRelativePaths);
        Assert.Contains("mods", plan.IncludedRelativePaths);
        Assert.DoesNotContain("libraries", plan.IncludedRelativePaths);

        var result = await new BackupService().CreateBackupAsync(
            instance,
            plan.Options);
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        var entries = archive.Entries.Select(static entry => entry.FullName).ToArray();

        Assert.Contains("mods/old-mod.jar", entries);
        Assert.Contains("plugins/old-plugin.jar", entries);
        Assert.Contains("config/cache/generated.toml", entries);
        Assert.Contains("world/level.dat", entries);
        Assert.Contains("world/playerdata/player.dat", entries);
        Assert.Contains("world_nether/region/r.0.0.mca", entries);
        Assert.Contains("world_the_end/DIM1/region/r.0.0.mca", entries);
        Assert.Contains("server.properties", entries);
        Assert.Contains("user_jvm_args.txt", entries);
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("versions/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("jre/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("logs/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("cache/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("backups/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("server.jar", entries);
        Assert.DoesNotContain("run.bat", entries);
        Assert.DoesNotContain("run.sh", entries);
        Assert.DoesNotContain("win_args.txt", entries);
        Assert.DoesNotContain(".minecraft-server-manager.lock", entries);
        Assert.DoesNotContain("unrelated.bin", entries);

        async Task WriteAsync(string relativePath, string contents)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, contents);
        }
    }

    [Fact]
    public async Task CreatePlanAsync_UsesBukkitContainerForAllThreeWorldDirectories()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.Path;
        await File.WriteAllTextAsync(Path.Combine(root, "server.properties"), "level-name=realm\n");
        await File.WriteAllTextAsync(Path.Combine(root, "bukkit.yml"), "world-container: worlds\n");
        foreach (var name in new[] { "realm", "realm_nether", "realm_the_end" })
        {
            var path = Path.Combine(root, "worlds", name);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "level.dat"), name);
        }

        var plan = await new ModpackUpdateBackupPlanner().CreatePlanAsync(
            new ServerInstance { Name = "Bukkit", DirectoryPath = root },
            "2.0");

        Assert.Contains("worlds/realm", plan.IncludedRelativePaths);
        Assert.Contains("worlds/realm_nether", plan.IncludedRelativePaths);
        Assert.Contains("worlds/realm_the_end", plan.IncludedRelativePaths);
    }

    [Theory]
    [InlineData("libraries")]
    [InlineData("mods")]
    [InlineData("backups")]
    public async Task CreatePlanAsync_RejectsWorldThatCollidesWithManagedOrCoreDirectory(string levelName)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory.Path, "server.properties"),
            $"level-name={levelName}\n");
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, levelName));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModpackUpdateBackupPlanner().CreatePlanAsync(
                new ServerInstance { Name = "Conflict", DirectoryPath = temporaryDirectory.Path },
                "2.0"));

        Assert.Contains("衝突", error.Message, StringComparison.Ordinal);
    }
}
