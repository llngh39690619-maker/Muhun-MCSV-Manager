using System.IO.Compression;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class OfficialLoaderInstallerOutputValidatorTests
{
    [Fact]
    public async Task Fabric_CurrentManifestOnlyLauncher_DoesNotRequireObsoleteExternalProperties()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "Fabric 中文 空白")).FullName;
        const string loaderVersion = "0.19.3";
        WriteFabricOutput(root, loaderVersion);
        var installer = WriteInstallerMarker(temporaryDirectory.Path, "fabric-installer.jar");
        var request = new ModrinthModpackLoaderInstallRequest(
            ModrinthModpackLoaderKind.Fabric,
            "26.2",
            loaderVersion);

        var provenance = await OfficialLoaderInstallerOutputValidator.ValidateAndCreateAsync(
            request,
            root,
            installer,
            EnumerateRelativeFiles(root));

        Assert.Equal(OfficialLoaderLaunchLayout.FabricManifestLauncher, provenance.LaunchLayout);
        Assert.False(File.Exists(Path.Combine(root, "fabric-server-launcher.properties")));
        await OfficialLoaderInstallerOutputValidator.RevalidateAsync(provenance, root);

        await File.AppendAllTextAsync(
            Path.Combine(
                root,
                "libraries",
                "net",
                "fabricmc",
                "fabric-loader",
                loaderVersion,
                $"fabric-loader-{loaderVersion}.jar"),
            "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OfficialLoaderInstallerOutputValidator.RevalidateAsync(provenance, root));
    }

    [Fact]
    public async Task NeoForge26_DirectOfficialMain_IsTypedAcceptedWhileGenericOnlineGateStaysStrict()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "NeoForge 中文 空白")).FullName;
        const string loaderVersion = "26.2.0.61";
        WriteNeoForgeDirectMainOutput(root, loaderVersion);
        var installer = WriteInstallerMarker(temporaryDirectory.Path, "neoforge-installer.jar");
        var request = new ModrinthModpackLoaderInstallRequest(
            ModrinthModpackLoaderKind.NeoForge,
            "26.2",
            loaderVersion);

        var detection = await new ServerPackDetector().DetectAsync(root);
        Assert.True(detection.IsRunnable, detection.Error);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OnlineServerPackSafetyValidator.ValidateAsync(detection));

        var provenance = await OfficialLoaderInstallerOutputValidator.ValidateAndCreateAsync(
            request,
            root,
            installer,
            EnumerateRelativeFiles(root));
        Assert.Equal(OfficialLoaderLaunchLayout.NeoForgeDirectMainClass, provenance.LaunchLayout);
        await OfficialLoaderInstallerOutputValidator.RevalidateAsync(provenance, root);

        var winArgs = NeoForgeArgsPath(root, loaderVersion, "win_args.txt");
        File.WriteAllText(
            winArgs,
            File.ReadAllText(winArgs).Replace(
                "net.neoforged.fml.startup.Server",
                "com.example.UntrustedWrapper",
                StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OfficialLoaderInstallerOutputValidator.ValidateAndCreateAsync(
                request,
                root,
                installer,
                EnumerateRelativeFiles(root)));
    }

    [Theory]
    [InlineData("@nested-arguments.txt")]
    [InlineData("-jar untrusted-wrapper.jar")]
    [InlineData("-javaagent:untrusted-agent.jar")]
    [InlineData("-XX:OnError=calc.exe")]
    [InlineData("-Djava.system.class.loader=com.example.Loader")]
    [InlineData("--class-path untrusted.jar")]
    [InlineData("com.example.ReplacementMain")]
    public async Task NeoForge26_TypedProvenance_StillRejectsUnsafeAuxiliaryJavaTokens(
        string unsafeArguments)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "Neo unsafe")).FullName;
        const string loaderVersion = "26.2.0.61";
        WriteNeoForgeDirectMainOutput(root, loaderVersion);
        File.WriteAllText(Path.Combine(root, "user_jvm_args.txt"), unsafeArguments + "\n");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OfficialLoaderInstallerOutputValidator.ValidateAndCreateAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.NeoForge,
                    "26.2",
                    loaderVersion),
                root,
                WriteInstallerMarker(temporaryDirectory.Path, "neoforge-installer.jar"),
                EnumerateRelativeFiles(root)));
    }

    [Fact]
    public async Task Forge26_ExactInstallerEmbeddedShim_IsNormalizedToSingleSafeLaunch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "Forge 中文 空白")).FullName;
        var installer = WriteForgeShimOutput(root, temporaryDirectory.Path);
        var request = new ModrinthModpackLoaderInstallRequest(
            ModrinthModpackLoaderKind.Forge,
            "26.2",
            "26.2-65.1.1");

        var provenance = await OfficialLoaderInstallerOutputValidator.ValidateAndCreateAsync(
            request,
            root,
            installer,
            EnumerateRelativeFiles(root));

        Assert.Equal(
            OfficialLoaderLaunchLayout.ForgeInstallerEmbeddedShim,
            provenance.LaunchLayout);
        Assert.DoesNotContain("--onlyCheckJava", File.ReadAllText(Path.Combine(root, "run.bat")));
        Assert.StartsWith(
            "#!/usr/bin/env sh\n# Muhun:",
            File.ReadAllText(Path.Combine(root, "run.sh")),
            StringComparison.Ordinal);
        await OfficialLoaderInstallerOutputValidator.RevalidateAsync(provenance, root);
    }

    [Fact]
    public async Task Forge26_ShimDifferentFromVerifiedInstaller_IsRejected()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "Forge mismatch")).FullName;
        var installer = WriteForgeShimOutput(root, temporaryDirectory.Path);
        await File.AppendAllTextAsync(Path.Combine(root, "forge-26.2-65.1.1-shim.jar"), "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OfficialLoaderInstallerOutputValidator.ValidateAndCreateAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Forge,
                    "26.2",
                    "26.2-65.1.1"),
                root,
                installer,
                EnumerateRelativeFiles(root)));
    }

    private static void WriteFabricOutput(string root, string loaderVersion)
    {
        var loader = Path.Combine(
            root,
            "libraries",
            "net",
            "fabricmc",
            "fabric-loader",
            loaderVersion,
            $"fabric-loader-{loaderVersion}.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(loader)!);
        File.WriteAllText(loader, "official loader dependency");
        File.WriteAllText(Path.Combine(root, "server.jar"), "minecraft server");
        WriteZip(
            Path.Combine(root, "fabric-server-launch.jar"),
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] =
                    "Manifest-Version: 1.0\r\n"
                    + "Main-Class: net.fabricmc.loader.impl.launch.server.FabricServerLauncher\r\n"
                    + $"Class-Path: libraries/net/fabricmc/fabric-loader/{loaderVersion}/fabric-loader-{loaderVersion}.jar\r\n\r\n",
                ["fabric-server-launch.properties"] =
                    "launch.mainClass=net.fabricmc.loader.impl.launch.knot.KnotServer\n"
            });
    }

    private static void WriteNeoForgeDirectMainOutput(string root, string loaderVersion)
    {
        var loader = "libraries/net/neoforged/fancymodloader/loader/11.0.16/loader-11.0.16.jar";
        var loaderPath = Path.Combine(root, loader.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(loaderPath)!);
        File.WriteAllText(loaderPath, "official fancy mod loader");
        var directory = $"libraries/net/neoforged/neoforge/{loaderVersion}";
        var absoluteDirectory = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(absoluteDirectory);
        File.WriteAllText(
            Path.Combine(absoluteDirectory, "win_args.txt"),
            NeoForgeArguments(loader, ';', loaderVersion));
        File.WriteAllText(
            Path.Combine(absoluteDirectory, "unix_args.txt"),
            NeoForgeArguments(loader, ':', loaderVersion));
        File.WriteAllText(
            Path.Combine(root, "run.bat"),
            $"@echo off\njava @user_jvm_args.txt @{directory}/win_args.txt %*\n");
        File.WriteAllText(
            Path.Combine(root, "run.sh"),
            $"#!/usr/bin/env sh\njava @user_jvm_args.txt @{directory}/unix_args.txt \"$@\"\n");
        File.WriteAllText(Path.Combine(root, "user_jvm_args.txt"), "# safe defaults\n-Xmx4G\n");
    }

    private static string NeoForgeArguments(
        string loader,
        char separator,
        string loaderVersion)
        => "--add-opens java.base/java.lang.invoke=ALL-UNNAMED\n"
           + "-Djava.net.preferIPv6Addresses=system\n"
           + "-DlibraryDirectory=libraries\n"
           + "-classpath\n"
           + loader.Replace(':', separator) + "\n"
           + "net.neoforged.fml.startup.Server\n"
           + $"--fml.neoForgeVersion {loaderVersion}\n"
           + "--fml.mcVersion 26.2\n"
           + "--fml.neoFormVersion 2\n";

    private static string NeoForgeArgsPath(string root, string loaderVersion, string fileName)
        => Path.Combine(
            root,
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            loaderVersion,
            fileName);

    private static string WriteForgeShimOutput(string root, string parent)
    {
        const string coordinate = "26.2-65.1.1";
        var shimName = $"forge-{coordinate}-shim.jar";
        var shim = Path.Combine(root, shimName);
        WriteZip(
            shim,
            new Dictionary<string, string>
            {
                ["META-INF/MANIFEST.MF"] =
                    "Manifest-Version: 1.0\r\n"
                    + "Main-Class: net.minecraftforge.bootstrap.shim.Main\r\n\r\n"
                    + "Name: bootstrap-shim.properties\r\nSHA-384-Digest: fixture\r\n",
                ["bootstrap-shim.properties"] =
                    "Arguments=--launchTarget forge_server\n"
                    + "Java-Version=25\n"
                    + "Main-Class=net.minecraftforge.bootstrap.ForgeBootstrap\n",
                ["bootstrap-shim.list"] =
                    $"fixture\tnet.minecraftforge:forge:{coordinate}:server\tnet/minecraftforge/forge/{coordinate}/server.jar\n",
                ["META-INF/FORGE.SF"] = "signed metadata",
                ["META-INF/FORGE.RSA"] = "signature block"
            });

        var argumentDirectory = $"libraries/net/minecraftforge/forge/{coordinate}";
        var absoluteArgumentDirectory = Path.Combine(
            root,
            argumentDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(absoluteArgumentDirectory);
        var args = $"-Djava.net.preferIPv6Addresses=system -XX:+UseCompactObjectHeaders -jar {shimName}\n";
        File.WriteAllText(Path.Combine(absoluteArgumentDirectory, "win_args.txt"), args);
        File.WriteAllText(Path.Combine(absoluteArgumentDirectory, "unix_args.txt"), args);
        File.WriteAllText(Path.Combine(root, "user_jvm_args.txt"), "# user JVM options\n");
        File.WriteAllText(
            Path.Combine(root, "run.bat"),
            $"@echo off\njava -jar {shimName} --onlyCheckJava\n"
            + $"java @user_jvm_args.txt @{argumentDirectory}/win_args.txt %*\n");
        File.WriteAllText(
            Path.Combine(root, "run.sh"),
            $"#!/usr/bin/env sh\njava -jar {shimName} --onlyCheckJava || exit 1\n"
            + $"java @user_jvm_args.txt @{argumentDirectory}/unix_args.txt \"$@\"\n");

        var installer = Path.Combine(parent, "forge-installer.jar");
        WriteZip(
            installer,
            new Dictionary<string, byte[]>
            {
                [$"maven/net/minecraftforge/forge/{coordinate}/{shimName}"] = File.ReadAllBytes(shim)
            });
        return installer;
    }

    private static string WriteInstallerMarker(string parent, string fileName)
    {
        var path = Path.Combine(parent, fileName);
        File.WriteAllText(path, "hash-verified official installer");
        return path;
    }

    private static IReadOnlyList<string> EnumerateRelativeFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void WriteZip(string path, IReadOnlyDictionary<string, string> entries)
        => WriteZip(
            path,
            entries.ToDictionary(
                static pair => pair.Key,
                static pair => Encoding.UTF8.GetBytes(pair.Value),
                StringComparer.Ordinal));

    private static void WriteZip(string path, IReadOnlyDictionary<string, byte[]> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var pair in entries)
        {
            var entry = archive.CreateEntry(pair.Key, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(pair.Value);
        }
    }
}
