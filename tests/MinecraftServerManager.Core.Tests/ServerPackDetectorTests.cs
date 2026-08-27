using System.Runtime.InteropServices;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class ServerPackDetectorTests
{
    [Fact]
    public async Task DetectAsync_WindowsFtbNeoForgePack_UsesBundledJavaAndWinArgs()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.Path, "FTB 天空 pack with spaces");
        await CreateNeoForgePackAsync(root);
        var detector = new ServerPackDetector(
            new FakeHostPlatformProbe(HostOperatingSystem.Windows, Architecture.X64));

        var result = await detector.DetectAsync(root);

        Assert.True(result.IsRecognized);
        Assert.True(result.IsRunnable, result.Error);
        Assert.Null(result.Error);
        Assert.Equal(HostOperatingSystem.Windows, result.HostOperatingSystem);
        Assert.Equal("FTB Skies 2: Aero", result.PackName);
        Assert.Equal("1.6.0", result.PackVersion);
        Assert.Equal(CoreType.NeoForge, result.CoreType);
        Assert.Equal("1.21.1", result.MinecraftVersion);
        Assert.Equal("21.1.248", result.ModLoaderVersion);
        Assert.Equal(21, result.JavaMajorVersion);
        Assert.Equal(
            Path.Combine(root, "jre", "21.0.10+7-LTS", "bin", "java.exe"),
            result.JavaExecutablePath);
        Assert.Equal(Path.Combine(root, "run.bat"), result.SourceLaunchScriptPath);
        Assert.Equal(
            [
                "user_jvm_args.txt",
                "libraries/net/neoforged/neoforge/21.1.248/win_args.txt",
            ],
            result.JavaArgumentFilePaths);
        Assert.Equal(["nogui"], result.ServerArguments);
        Assert.Equal(1024, result.MinimumMemoryMb);
        Assert.Equal(8192, result.MaximumMemoryMb);
        Assert.InRange(result.ConfidencePercent, 90, 100);
    }

    [Fact]
    public async Task DetectAsync_LinuxPack_SelectsRunShUnixArgsAndPathJava()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.Path, "Linux 測試 server");
        await CreateNeoForgePackAsync(root);
        var detector = new ServerPackDetector(
            new FakeHostPlatformProbe(HostOperatingSystem.Linux, Architecture.Arm64));

        var result = await detector.DetectAsync(root);

        Assert.True(result.IsRunnable, result.Error);
        Assert.Equal(HostOperatingSystem.Linux, result.HostOperatingSystem);
        Assert.Equal("java", result.JavaExecutablePath);
        Assert.Equal(Path.Combine(root, "run.sh"), result.SourceLaunchScriptPath);
        Assert.Equal(
            [
                "user_jvm_args.txt",
                "libraries/net/neoforged/neoforge/21.1.248/unix_args.txt",
            ],
            result.JavaArgumentFilePaths);
        Assert.Empty(result.ServerArguments);
    }

    [Fact]
    public async Task DetectAsync_InstalledForgePack_IsRecognized()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.Path, "forge-pack");
        Directory.CreateDirectory(Path.Combine(
            root,
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            "1.20.1-47.4.0"));
        Directory.CreateDirectory(Path.Combine(root, "jre", "17", "bin"));
        await File.WriteAllTextAsync(Path.Combine(root, "jre", "17", "bin", "java.exe"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(root, "user_jvm_args.txt"), "-Xms2G\n-Xmx6G\n");
        await File.WriteAllTextAsync(
            Path.Combine(root, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.4.0", "win_args.txt"),
            "--fml.mcVersion 1.20.1\n--fml.forgeVersion 47.4.0\n");
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.bat"),
            "@echo off\n\"jre\\17\\bin\\java.exe\" @user_jvm_args.txt "
            + "@libraries/net/minecraftforge/forge/1.20.1-47.4.0/win_args.txt nogui %*\n");
        var detector = new ServerPackDetector(
            new FakeHostPlatformProbe(HostOperatingSystem.Windows, Architecture.X64));

        var result = await detector.DetectAsync(root);

        Assert.True(result.IsRunnable, result.Error);
        Assert.Equal(CoreType.Forge, result.CoreType);
        Assert.Equal("1.20.1", result.MinecraftVersion);
        Assert.Equal("47.4.0", result.ModLoaderVersion);
        Assert.Equal(2048, result.MinimumMemoryMb);
        Assert.Equal(6144, result.MaximumMemoryMb);
    }

    [Fact]
    public async Task DetectAsync_MissingReferencedArgumentFile_IsNotRunnable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.Path;
        await CreateNeoForgePackAsync(root);
        File.Delete(Path.Combine(
            root,
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            "21.1.248",
            "win_args.txt"));
        var detector = WindowsDetector();

        var result = await detector.DetectAsync(root);

        Assert.True(result.IsRecognized);
        Assert.False(result.IsRunnable);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectAsync_ArgumentFileTraversal_IsRejectedEvenWhenTargetExists()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.Path, "pack");
        await CreateNeoForgePackAsync(root);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "outside.txt"), "-Xmx99G");
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.bat"),
            "\"jre\\21.0.10+7-LTS\\bin\\java.exe\" @../outside.txt "
            + "@libraries/net/neoforged/neoforge/21.1.248/win_args.txt nogui %*\n");

        var result = await WindowsDetector().DetectAsync(root);

        Assert.False(result.IsRunnable);
        Assert.Contains("outside", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectAsync_ArgumentFileBehindIntermediateJunction_IsRejected()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.Path, "pack");
        await CreateNeoForgePackAsync(root);
        var librariesPath = Path.Combine(root, "libraries");
        var outsideLibrariesPath = Path.Combine(temporaryDirectory.Path, "outside-libraries");
        Directory.Move(librariesPath, outsideLibrariesPath);
        ReparsePointTestHelper.CreateDirectoryLink(librariesPath, outsideLibrariesPath);

        try
        {
            var result = await WindowsDetector().DetectAsync(root);

            Assert.False(result.IsRunnable);
            Assert.Contains("reparse-point", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(librariesPath);
        }
    }

    [Theory]
    [InlineData("powershell -NoProfile -Command whoami")]
    [InlineData("del /q world\\level.dat")]
    [InlineData("\"jre\\21.0.10+7-LTS\\bin\\java.exe\" @user_jvm_args.txt @libraries/net/neoforged/neoforge/21.1.248/win_args.txt & calc.exe")]
    public async Task DetectAsync_UnapprovedOrDangerousBatchCommand_IsRejected(string maliciousLine)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await CreateNeoForgePackAsync(temporaryDirectory.Path);
        await File.AppendAllTextAsync(
            Path.Combine(temporaryDirectory.Path, "run.bat"),
            Environment.NewLine + maliciousLine);

        var result = await WindowsDetector().DetectAsync(temporaryDirectory.Path);

        Assert.False(result.IsRunnable);
        Assert.Contains("Rejected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectAsync_MultipleJavaCommands_IsRejectedAsAmbiguous()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await CreateNeoForgePackAsync(temporaryDirectory.Path);
        var path = Path.Combine(temporaryDirectory.Path, "run.bat");
        var launch = (await File.ReadAllTextAsync(path)).Split('\n')
            .Single(line => line.Contains("java.exe", StringComparison.OrdinalIgnoreCase));
        await File.AppendAllTextAsync(path, Environment.NewLine + launch);

        var result = await WindowsDetector().DetectAsync(temporaryDirectory.Path);

        Assert.False(result.IsRunnable);
        Assert.Contains("multiple", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectAsync_UnsupportedHost_IsRecognizedButNotRunnable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await CreateNeoForgePackAsync(temporaryDirectory.Path);
        var detector = new ServerPackDetector(
            new FakeHostPlatformProbe(HostOperatingSystem.Unsupported, Architecture.X64));

        var result = await detector.DetectAsync(temporaryDirectory.Path);

        Assert.True(result.IsRecognized);
        Assert.False(result.IsRunnable);
        Assert.Contains("Windows and Linux", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static ServerPackDetector WindowsDetector() => new(
        new FakeHostPlatformProbe(HostOperatingSystem.Windows, Architecture.X64));

    private static async Task CreateNeoForgePackAsync(string root)
    {
        var loaderDirectory = Path.Combine(
            root,
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            "21.1.248");
        Directory.CreateDirectory(loaderDirectory);
        Directory.CreateDirectory(Path.Combine(root, "jre", "21.0.10+7-LTS", "bin"));
        await File.WriteAllTextAsync(
            Path.Combine(root, ".manifest.json"),
            """
            {
              "id": 134,
              "name": "FTB Skies 2: Aero",
              "versionName": "1.6.0",
              "versionId": 100458,
              "modPackTargets": {
                "modLoader": { "name": "neoforge", "version": "21.1.248" },
                "javaVersion": "21.0.10+7-LTS",
                "mcVersion": "1.21.1"
              }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "user_jvm_args.txt"),
            "# Memory configuration\n-Xmx8192M\n");
        await File.WriteAllTextAsync(
            Path.Combine(loaderDirectory, "win_args.txt"),
            "--launchTarget forgeserver\n--fml.neoForgeVersion 21.1.248\n--fml.mcVersion 1.21.1\n");
        await File.WriteAllTextAsync(
            Path.Combine(loaderDirectory, "unix_args.txt"),
            "--launchTarget forgeserver\n--fml.neoForgeVersion 21.1.248\n--fml.mcVersion 1.21.1\n");
        await File.WriteAllTextAsync(
            Path.Combine(root, "jre", "21.0.10+7-LTS", "bin", "java.exe"),
            string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.bat"),
            """
            @echo off
            REM Forge requires JVM and program argument files.
            "jre\21.0.10+7-LTS\bin\java.exe" @user_jvm_args.txt @libraries/net/neoforged/neoforge/21.1.248/win_args.txt nogui %*
            pause
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.sh"),
            """
            #!/usr/bin/env sh
            # The detector reads this text but never executes it.
            java @user_jvm_args.txt @libraries/net/neoforged/neoforge/21.1.248/unix_args.txt "$@"
            """);
    }

    private sealed class FakeHostPlatformProbe(
        HostOperatingSystem operatingSystem,
        Architecture architecture) : IHostPlatformProbe
    {
        public HostOperatingSystem OperatingSystem { get; } = operatingSystem;

        public Architecture OSArchitecture { get; } = architecture;
    }
}
