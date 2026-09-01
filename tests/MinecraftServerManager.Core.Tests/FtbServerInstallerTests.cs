using System.Diagnostics;
using System.Runtime.InteropServices;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class FtbServerInstallerTests
{
    [Fact]
    public async Task InstalledServerValidator_RequiresMatchingManifestAndRunnableServerPack()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.Path, "FTB 天空：Aero 1.6.1");
        await CreateRunnableNeoForgePackAsync(root, packId: 134, versionId: 100466);
        var validator = new FtbInstalledServerValidator(new ServerPackDetector(new WindowsHostProbe()));

        var detection = await validator.ValidateAsync(root, 134, 100466);

        Assert.True(detection.IsRunnable, detection.Error);
        Assert.Equal("FTB Skies 2: Aero", detection.PackName);
        Assert.Equal("1.6.1", detection.PackVersion);
        Assert.Equal(CoreType.NeoForge, detection.CoreType);
        Assert.Equal(21, detection.JavaMajorVersion);
    }

    [Fact]
    public async Task InstalledServerValidator_RejectsDifferentPackOrVersionBeforeDetection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await CreateRunnableNeoForgePackAsync(temporaryDirectory.Path, packId: 134, versionId: 100466);
        var validator = new FtbInstalledServerValidator(new ServerPackDetector(new WindowsHostProbe()));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            validator.ValidateAsync(temporaryDirectory.Path, 129, 100466));

        Assert.Contains("預期 Pack 129", error.Message, StringComparison.Ordinal);
        Assert.Contains("實際 Pack 134", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledServerValidator_RejectsWrapperJarHiddenInLoaderArgumentFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await CreateRunnableNeoForgePackAsync(
            temporaryDirectory.Path,
            packId: 134,
            versionId: 100466);
        var loaderArguments = Path.Combine(
            temporaryDirectory.Path,
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            "21.1.248",
            "win_args.txt");
        await File.WriteAllTextAsync(
            loaderArguments,
            "-jar ServerStart.jar\n--launchTarget forgeserver\n"
            + "--fml.neoForgeVersion 21.1.248\n--fml.mcVersion 1.21.1\n");
        var validator = new FtbInstalledServerValidator(
            new ServerPackDetector(new WindowsHostProbe()));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            validator.ValidateAsync(temporaryDirectory.Path, 134, 100466));

        Assert.Contains("-jar", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_UsesFreshChineseStagingAndKeepsItOnlyAfterValidation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installerPath = Path.Combine(temporaryDirectory.Path, "官方 工具.exe");
        await File.WriteAllBytesAsync(installerPath, [0x4d, 0x5a]);
        var staging = Path.Combine(temporaryDirectory.Path, "匯入 暫存 Aero");
        var runner = new RecordingRunner();
        var validator = new RecordingValidator();
        var installer = new FtbServerInstaller(runner, validator);
        var request = new FtbInstallRequest(
            134,
            100466,
            installerPath,
            staging,
            MinecraftEulaAccepted: true);

        var result = await installer.InstallAsync(request);

        Assert.True(Directory.Exists(staging));
        Assert.Equal(staging, result.InstallationDirectory);
        Assert.Equal((staging, 134, 100466), Assert.Single(validator.Calls));
        var startInfo = Assert.Single(runner.StartInfos);
        Assert.Equal(Path.GetFullPath(installerPath), startInfo.FileName);
        Assert.Equal(Path.GetFullPath(staging), startInfo.WorkingDirectory);
        Assert.Contains(Path.GetFullPath(staging), startInfo.ArgumentList);
    }

    [Fact]
    public async Task InstallAsync_ProcessFailureRemovesOnlyOwnedStaging()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installerPath = Path.Combine(temporaryDirectory.Path, "installer.exe");
        await File.WriteAllBytesAsync(installerPath, [0x4d, 0x5a]);
        var staging = Path.Combine(temporaryDirectory.Path, "new-staging");
        var installer = new FtbServerInstaller(
            new RecordingRunner(_ => throw new FtbInstallerProcessException(
                new FtbInstallerProcessResult(1, [], ["failed"]))),
            new RecordingValidator());

        await Assert.ThrowsAsync<FtbInstallerProcessException>(() => installer.InstallAsync(
            new FtbInstallRequest(
                134,
                100466,
                installerPath,
                staging,
                MinecraftEulaAccepted: true)));

        Assert.False(Directory.Exists(staging));
        Assert.True(File.Exists(installerPath));
    }

    [Fact]
    public async Task InstallAsync_ValidationFailureRemovesOwnedStaging()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installerPath = Path.Combine(temporaryDirectory.Path, "installer.exe");
        await File.WriteAllBytesAsync(installerPath, [0x4d, 0x5a]);
        var staging = Path.Combine(temporaryDirectory.Path, "new-staging");
        var installer = new FtbServerInstaller(
            new RecordingRunner(),
            new RecordingValidator(_ => throw new InvalidDataException("manifest mismatch")));

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(
            new FtbInstallRequest(
                134,
                100466,
                installerPath,
                staging,
                MinecraftEulaAccepted: true)));

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task InstallAsync_ExistingDirectoryIsNeverModifiedOrRemoved()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installerPath = Path.Combine(temporaryDirectory.Path, "installer.exe");
        await File.WriteAllBytesAsync(installerPath, [0x4d, 0x5a]);
        var staging = Path.Combine(temporaryDirectory.Path, "existing");
        Directory.CreateDirectory(staging);
        var sentinel = Path.Combine(staging, "world.dat");
        await File.WriteAllTextAsync(sentinel, "user data");
        var installer = new FtbServerInstaller(new RecordingRunner(), new RecordingValidator());

        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(
            new FtbInstallRequest(
                134,
                100466,
                installerPath,
                staging,
                MinecraftEulaAccepted: true)));

        Assert.Equal("user data", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task InstallAsync_RejectsMissingMinecraftEulaConsentBeforeFilesystemOrProcessWork()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installerPath = Path.Combine(temporaryDirectory.Path, "missing-installer.exe");
        var staging = Path.Combine(temporaryDirectory.Path, "must-not-exist");
        var runner = new RecordingRunner();
        var validator = new RecordingValidator();
        var installer = new FtbServerInstaller(runner, validator);

        await Assert.ThrowsAsync<MinecraftEulaAcceptanceRequiredException>(() =>
            installer.InstallAsync(new FtbInstallRequest(134, 100466, installerPath, staging)));

        Assert.False(Directory.Exists(staging));
        Assert.Empty(runner.StartInfos);
        Assert.Empty(validator.Calls);
    }

    private static async Task CreateRunnableNeoForgePackAsync(
        string root,
        int packId,
        int versionId)
    {
        var loaderDirectory = Path.Combine(
            root,
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            "21.1.248");
        Directory.CreateDirectory(loaderDirectory);
        var javaDirectory = Path.Combine(root, "jre", "21.0.10+7-LTS", "bin");
        Directory.CreateDirectory(javaDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(root, ".manifest.json"),
            $$"""
            {
              "id": {{packId}},
              "name": "FTB Skies 2: Aero",
              "versionName": "1.6.1",
              "versionId": {{versionId}},
              "modPackTargets": {
                "modLoader": { "name": "neoforge", "version": "21.1.248" },
                "javaVersion": "21.0.10+7-LTS",
                "mcVersion": "1.21.1"
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "user_jvm_args.txt"), "-Xmx8192M\n");
        await File.WriteAllTextAsync(
            Path.Combine(loaderDirectory, "win_args.txt"),
            "--launchTarget forgeserver\n--fml.neoForgeVersion 21.1.248\n--fml.mcVersion 1.21.1\n");
        await File.WriteAllTextAsync(Path.Combine(javaDirectory, "java.exe"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.bat"),
            "@echo off\n\"jre\\21.0.10+7-LTS\\bin\\java.exe\" @user_jvm_args.txt "
            + "@libraries/net/neoforged/neoforge/21.1.248/win_args.txt nogui %*\n");
    }

    private sealed class WindowsHostProbe : IHostPlatformProbe
    {
        public HostOperatingSystem OperatingSystem => HostOperatingSystem.Windows;

        public Architecture OSArchitecture => Architecture.X64;
    }

    private sealed class RecordingRunner(
        Func<ProcessStartInfo, Task<FtbInstallerProcessResult>>? run = null)
        : IFtbInstallerProcessRunner
    {
        public List<ProcessStartInfo> StartInfos { get; } = [];

        public async Task<FtbInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            IProgress<FtbInstallerOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            StartInfos.Add(startInfo);
            cancellationToken.ThrowIfCancellationRequested();
            return run is null
                ? new FtbInstallerProcessResult(0, ["ok"], [])
                : await run(startInfo);
        }
    }

    private sealed class RecordingValidator(
        Func<(string Directory, int PackId, int VersionId), Task<ServerPackDetectionResult>>? validate = null)
        : IFtbInstalledServerValidator
    {
        public List<(string Directory, int PackId, int VersionId)> Calls { get; } = [];

        public async Task<ServerPackDetectionResult> ValidateAsync(
            string installationDirectory,
            int expectedPackId,
            int expectedVersionId,
            CancellationToken cancellationToken = default)
        {
            var call = (installationDirectory, expectedPackId, expectedVersionId);
            Calls.Add(call);
            cancellationToken.ThrowIfCancellationRequested();
            return validate is null
                ? new ServerPackDetectionResult
                {
                    DirectoryPath = installationDirectory,
                    IsRecognized = true,
                    IsRunnable = true,
                }
                : await validate(call);
        }
    }
}
