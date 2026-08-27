using System.IO.Compression;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class JarCoreDetectorTests
{
    private readonly JarCoreDetector _detector = new();

    [Fact]
    public void Detect_UsesManifestAndEntriesWithoutRunningJar()
    {
        using var directory = new TemporaryDirectory();
        var jarPath = CreateJar(
            directory.Path,
            "mystery-1.21.4.jar",
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] =
                    "Manifest-Version: 1.0\r\nMain-Class: io.papermc.paperclip.Main\r\nMinecraft-Version: 1.21.4\r\n",
                ["io/papermc/paperclip/Main.class"] = "not executable test data"
            });

        var result = _detector.Detect(jarPath);

        Assert.Equal(CoreType.Paper, result.CoreType);
        Assert.Equal("1.21.4", result.MinecraftVersion);
        Assert.Equal("io.papermc.paperclip.Main", result.MainClass);
        Assert.True(result.IsValidJar);
        Assert.InRange(result.ConfidencePercent, 80, 100);
    }

    [Fact]
    public void Detect_PrefersNeoForgeOverForgeSubstring()
    {
        using var directory = new TemporaryDirectory();
        var jarPath = CreateJar(
            directory.Path,
            "neoforge-21.1.200.jar",
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] = "Manifest-Version: 1.0\r\nMain-Class: net.neoforged.bootstrap.Main\r\n",
                ["META-INF/neoforge.mods.toml"] = string.Empty,
                ["net/neoforged/bootstrap/Main.class"] = string.Empty
            });

        var result = _detector.Detect(jarPath);

        Assert.Equal(CoreType.NeoForge, result.CoreType);
        Assert.Equal("1.21.1", result.MinecraftVersion);
    }

    [Fact]
    public void Detect_ReadsMinecraftVersionFromVersionJson()
    {
        using var directory = new TemporaryDirectory();
        var jarPath = CreateJar(
            directory.Path,
            "server.jar",
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] = "Manifest-Version: 1.0\r\nMain-Class: net.minecraft.server.Main\r\n",
                ["net/minecraft/server/Main.class"] = string.Empty,
                ["version.json"] = "{\"id\":\"1.20.6\"}"
            });

        var result = _detector.Detect(jarPath);

        Assert.Equal(CoreType.Vanilla, result.CoreType);
        Assert.Equal("1.20.6", result.MinecraftVersion);
    }

    [Fact]
    public void Detect_RecognizesModernMojangBundlerWithHighConfidence()
    {
        using var directory = new TemporaryDirectory();
        var jarPath = CreateJar(
            directory.Path,
            "server.jar",
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] =
                    "Manifest-Version: 1.0\r\nMain-Class: net.minecraft.bundler.Main\r\n",
                ["net/minecraft/bundler/Main.class"] = string.Empty,
                ["META-INF/versions.list"] = "fixture"
            });

        var result = _detector.Detect(jarPath);

        Assert.Equal(CoreType.Vanilla, result.CoreType);
        Assert.Equal("net.minecraft.bundler.Main", result.MainClass);
        Assert.InRange(result.ConfidencePercent, 80, 100);
    }

    [Fact]
    public void Detect_MalformedJarReturnsDiagnosticInsteadOfExecutingOrThrowing()
    {
        using var directory = new TemporaryDirectory();
        var jarPath = Path.Combine(directory.Path, "unknown.jar");
        File.WriteAllText(jarPath, "this is not a zip");

        var result = _detector.Detect(jarPath);

        Assert.Equal(CoreType.Unknown, result.CoreType);
        Assert.False(result.IsValidJar);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task DetectAsync_HonorsAlreadyCancelledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _detector.DetectAsync("unused.jar", cancellation.Token));
    }

    [Theory]
    [InlineData(CoreType.Mohist, "com.mohistmc.MohistMCStart", "com/mohistmc/MohistMCStart.class")]
    [InlineData(CoreType.Arclight, "io.izzel.arclight.server.Launcher", "io/izzel/arclight/server/Launcher.class")]
    [InlineData(CoreType.CatServer, "catserver.server.CatServerLaunch", "catserver/server/CatServerLaunch.class")]
    [InlineData(CoreType.Akarin, "net.minecraft.launchwrapper.Launch", "io/akarin/server/mixin/bootstrap/Bootstrap.class")]
    public void Detect_RecognizesHybridCoreFromOfficialProjectMarkers(
        CoreType expected,
        string mainClass,
        string marker)
    {
        using var directory = new TemporaryDirectory();
        var jarPath = CreateJar(
            directory.Path,
            "server.jar",
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] =
                    $"Manifest-Version: 1.0\r\nMain-Class: {mainClass}\r\n",
                [marker] = string.Empty
            });

        var result = _detector.Detect(jarPath);

        Assert.Equal(expected, result.CoreType);
        Assert.InRange(result.ConfidencePercent, 80, 100);
    }

    [Fact]
    public void Detect_RecognizesOfficialCatServer118FoxLaunchStructure()
    {
        using var directory = new TemporaryDirectory();
        var jarPath = CreateJar(
            directory.Path,
            "server.jar",
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] =
                    "Manifest-Version: 1.0\r\nMain-Class: foxlaunch.FoxServerLauncher\r\n",
                ["foxlaunch/FoxServerLauncher.class"] = string.Empty,
                ["data/server.lzma"] = "fixture"
            });

        var result = _detector.Detect(jarPath);

        Assert.Equal(CoreType.CatServer, result.CoreType);
        Assert.InRange(result.ConfidencePercent, 80, 100);
    }

    [Theory]
    [InlineData("mohist-server.jar")]
    [InlineData("arclight-forge.jar")]
    [InlineData("catserver.jar")]
    [InlineData("akarin-1.12.2.jar")]
    public void Detect_HybridFilenameAloneNeverReachesOnlineConfidenceGate(
        string fileName)
    {
        using var directory = new TemporaryDirectory();
        var jarPath = CreateJar(
            directory.Path,
            fileName,
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] =
                    "Manifest-Version: 1.0\r\nMain-Class: example.Wrapper\r\n",
                ["example/Wrapper.class"] = string.Empty
            });

        var result = _detector.Detect(jarPath);

        Assert.True(result.ConfidencePercent < 80);
    }

    private static string CreateJar(
        string directory,
        string fileName,
        IReadOnlyDictionary<string, string> entries)
    {
        var path = Path.Combine(directory, fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entryName, contents) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
            writer.Write(contents);
        }

        return path;
    }
}
