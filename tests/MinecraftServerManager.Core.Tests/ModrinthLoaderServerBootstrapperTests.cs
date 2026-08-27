using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class ModrinthLoaderServerBootstrapperTests
{
    [Fact]
    public async Task Vanilla_MergesVerifiedServerIntoNonEmptyChineseStagingWithoutProcess()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "模組包 天空工廠");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var artifacts = new FakeArtifacts();
        var runner = new RecordingRunner();
        var bootstrapper = new ModrinthLoaderServerBootstrapper(artifacts, runner);

        var result = await bootstrapper.BootstrapAsync(
            new ModrinthModpackLoaderInstallRequest(
                ModrinthModpackLoaderKind.Vanilla,
                "1.20.1",
                null),
            staging,
            java);

        Assert.Equal(artifacts.ServerBytes, await File.ReadAllBytesAsync(Path.Combine(staging, "server.jar")));
        Assert.True(File.Exists(Path.Combine(staging, "mods", "pack-mod.jar")));
        Assert.Equal(["server.jar"], result.InstalledPaths);
        Assert.Equal(["server.jar"], result.LaunchCandidates);
        Assert.Null(result.ProcessResult);
        Assert.Empty(runner.StartInfos);
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Fact]
    public async Task Fabric_RunsOfficialInstallerInIsolatedFreshDirectoryAndVerifiesServerJar()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Fabric 測試包");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var artifacts = new FakeArtifacts();
        var runner = new RecordingRunner(async startInfo =>
        {
            Assert.NotEqual(Path.GetFullPath(staging), startInfo.WorkingDirectory);
            Assert.Empty(Directory.EnumerateFileSystemEntries(startInfo.WorkingDirectory));
            await WriteFabricInstallerOutputAsync(
                startInfo.WorkingDirectory,
                "0.16.9",
                artifacts.ServerBytes);
            return SuccessResult();
        });
        var request = new ModrinthModpackLoaderInstallRequest(
            ModrinthModpackLoaderKind.Fabric,
            "1.20.1",
            "0.16.9");

        var result = await new ModrinthLoaderServerBootstrapper(artifacts, runner)
            .BootstrapAsync(request, staging, java);

        var startInfo = Assert.Single(runner.StartInfos);
        Assert.Equal(Path.GetFullPath(java), startInfo.FileName);
        Assert.Equal("-downloadMinecraft", startInfo.ArgumentList[^1]);
        Assert.Equal(1, artifacts.FabricDownloads);
        Assert.Equal(1, artifacts.ServerVerifications);
        Assert.True(File.Exists(Path.Combine(staging, "fabric-server-launch.jar")));
        Assert.Contains("fabric-server-launch.jar", result.LaunchCandidates);
        Assert.Equal(
            OfficialLoaderLaunchLayout.FabricManifestLauncher,
            Assert.IsType<OfficialLoaderInstallProvenance>(result.Provenance).LaunchLayout);
        Assert.Equal(0, result.ProcessResult!.ExitCode);
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData(ModrinthModpackLoaderKind.Forge, "47.2.0")]
    [InlineData(ModrinthModpackLoaderKind.NeoForge, "21.1.248")]
    public async Task ForgeFamily_UsesDirectInstallerAndReturnsRunScripts(
        ModrinthModpackLoaderKind kind,
        string loaderVersion)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, kind.ToString());
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var artifacts = new FakeArtifacts();
        var runner = new RecordingRunner(async startInfo =>
        {
            await WriteStandardForgeFamilyOutputAsync(
                startInfo.WorkingDirectory,
                kind,
                "1.20.1",
                loaderVersion);
            return SuccessResult();
        });

        var result = await new ModrinthLoaderServerBootstrapper(artifacts, runner)
            .BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(kind, "1.20.1", loaderVersion),
                staging,
                java);

        var startInfo = Assert.Single(runner.StartInfos);
        var privateHome = startInfo.Environment["HOME"];
        var privateTemp = startInfo.Environment["TEMP"];
        Assert.Equal(
            [
                $"-Duser.home={privateHome}",
                $"-Djava.io.tmpdir={privateTemp}",
                $"-Duser.dir={startInfo.WorkingDirectory}",
                "-jar", startInfo.ArgumentList[4],
                "--installServer", startInfo.WorkingDirectory,
            ],
            startInfo.ArgumentList);
        Assert.True(Path.IsPathFullyQualified(startInfo.ArgumentList[^1]));
        Assert.Contains("run.bat", result.LaunchCandidates);
        Assert.Contains("run.sh", result.LaunchCandidates);
        Assert.True(File.Exists(Path.Combine(staging, "run.bat")));
        Assert.Equal(
            OfficialLoaderLaunchLayout.StandardArgumentFiles,
            Assert.IsType<OfficialLoaderInstallProvenance>(result.Provenance).LaunchLayout);
        Assert.Equal(kind == ModrinthModpackLoaderKind.Forge ? 1 : 0, artifacts.ForgeDownloads);
        Assert.Equal(kind == ModrinthModpackLoaderKind.NeoForge ? 1 : 0, artifacts.NeoForgeDownloads);
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Fact]
    public async Task Quilt_IsExplicitlyUnsupportedBeforeFilesystemOrNetworkMutation()
    {
        var artifacts = new FakeArtifacts();
        var runner = new RecordingRunner();
        var bootstrapper = new ModrinthLoaderServerBootstrapper(artifacts, runner);

        var error = await Assert.ThrowsAsync<ModrinthLoaderUnsupportedException>(() =>
            bootstrapper.BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Quilt,
                    "1.20.1",
                    "0.26.4"),
                "missing staging",
                "missing java"));

        Assert.Equal(ModrinthModpackLoaderKind.Quilt, error.Kind);
        Assert.Contains("吞掉", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, artifacts.TotalDownloads);
        Assert.Empty(runner.StartInfos);
    }

    [Fact]
    public async Task ProcessFailure_CleansOwnedOperationAndLeavesPackStagingUntouched()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Forge failure");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var artifacts = new FakeArtifacts();
        var runner = new RecordingRunner(_ => throw new ModrinthLoaderBootstrapProcessException(
            new ModrinthLoaderBootstrapProcessResult(1, [], ["failed"])));
        var bootstrapper = new ModrinthLoaderServerBootstrapper(artifacts, runner);

        await Assert.ThrowsAsync<ModrinthLoaderBootstrapProcessException>(() =>
            bootstrapper.BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Forge,
                    "1.20.1",
                    "47.2.0"),
                staging,
                java));

        Assert.True(File.Exists(Path.Combine(staging, "mods", "pack-mod.jar")));
        Assert.Single(Directory.EnumerateFileSystemEntries(staging));
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Fact]
    public async Task PreRunInstallerDownloadFailure_StillCleansTheProtectedOperationTree()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Forge pre-run failure");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var artifacts = new FakeArtifacts { FailInstallerDownload = true };
        var runner = new RecordingRunner();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModrinthLoaderServerBootstrapper(artifacts, runner).BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Forge,
                    "1.20.1",
                    "47.2.0"),
                staging,
                java));

        Assert.Contains("download failed", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.StartInfos);
        Assert.Single(Directory.EnumerateFileSystemEntries(staging));
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Fact]
    public async Task ProcessFailure_AndPersistentCleanupFailure_PreservesBothErrorsAndReportsLeftover()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Forge process and cleanup failure");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var primary = new ModrinthLoaderBootstrapProcessException(
            new ModrinthLoaderBootstrapProcessResult(7, [], ["primary failure"]));
        var cleanupFailure = new IOException("persistent cleanup failure");
        var cleanup = new FailingOperationCleanup(cleanupFailure);
        var runner = new RecordingRunner(_ => throw primary);
        var bootstrapper = new ModrinthLoaderServerBootstrapper(
            new FakeArtifacts(),
            runner,
            commandBuilder: null,
            cleanup);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            bootstrapper.BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Forge,
                    "1.20.1",
                    "47.2.0"),
                staging,
                java));

        Assert.Contains("Loader 安裝失敗", exception.Message, StringComparison.Ordinal);
        var combined = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Same(primary, combined.InnerExceptions[0]);
        Assert.Same(cleanupFailure, combined.InnerExceptions[1]);
        Assert.Equal(1, cleanup.Calls);
        Assert.False(cleanup.LastCancellationToken.CanBeCanceled);
        Assert.NotNull(cleanup.LastOperationRoot);
        Assert.True(Directory.Exists(cleanup.LastOperationRoot));
        Assert.Single(Directory.EnumerateFileSystemEntries(staging));

        SafePath.DeleteTreeWithoutFollowingReparsePoints(
            temporaryDirectory.Path,
            cleanup.LastOperationRoot);
    }

    [Fact]
    public async Task Cancellation_AndPersistentCleanupFailure_RemainsCancellationWithBothErrors()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Forge cancellation cleanup failure");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException? primary = null;
        var runner = new RecordingRunner(_ =>
        {
            cancellation.Cancel();
            primary = new OperationCanceledException(
                "installer cancelled",
                innerException: null,
                cancellation.Token);
            throw primary;
        });
        var cleanupFailure = new IOException("persistent cleanup failure");
        var cleanup = new FailingOperationCleanup(cleanupFailure);
        var bootstrapper = new ModrinthLoaderServerBootstrapper(
            new FakeArtifacts(),
            runner,
            commandBuilder: null,
            cleanup);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            bootstrapper.BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Forge,
                    "1.20.1",
                    "47.2.0"),
                staging,
                java,
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Contains("已取消", exception.Message, StringComparison.Ordinal);
        var combined = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Same(primary, combined.InnerExceptions[0]);
        Assert.Same(cleanupFailure, combined.InnerExceptions[1]);
        Assert.Equal(1, cleanup.Calls);
        Assert.False(cleanup.LastCancellationToken.CanBeCanceled);
        Assert.NotNull(cleanup.LastOperationRoot);
        Assert.True(Directory.Exists(cleanup.LastOperationRoot));

        SafePath.DeleteTreeWithoutFollowingReparsePoints(
            temporaryDirectory.Path,
            cleanup.LastOperationRoot);
    }

    [Fact]
    public async Task SuccessfulOutput_WithPersistentCleanupFailure_FailsClosedAndReportsWarning()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Forge successful output cleanup failure");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var runner = new RecordingRunner(async startInfo =>
        {
            await WriteStandardForgeFamilyOutputAsync(
                startInfo.WorkingDirectory,
                ModrinthModpackLoaderKind.Forge,
                "1.20.1",
                "47.2.0");
            return SuccessResult();
        });
        var cleanupFailure = new IOException("persistent cleanup failure");
        var cleanup = new FailingOperationCleanup(cleanupFailure);
        var output = new List<ModrinthLoaderBootstrapOutputLine>();
        var bootstrapper = new ModrinthLoaderServerBootstrapper(
            new FakeArtifacts(),
            runner,
            commandBuilder: null,
            cleanup);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            bootstrapper.BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Forge,
                    "1.20.1",
                    "47.2.0"),
                staging,
                java,
                processOutput: new InlineProgress(output.Add)));

        Assert.Contains("不回報安裝成功", exception.Message, StringComparison.Ordinal);
        Assert.Same(cleanupFailure, exception.InnerException);
        Assert.Contains(
            output,
            line => line.IsError
                && line.Text.Contains("不會回報成功", StringComparison.Ordinal));
        Assert.Equal(1, cleanup.Calls);
        Assert.NotNull(cleanup.LastOperationRoot);
        Assert.True(Directory.Exists(cleanup.LastOperationRoot));

        SafePath.DeleteTreeWithoutFollowingReparsePoints(
            temporaryDirectory.Path,
            cleanup.LastOperationRoot);
    }

    [Fact]
    public async Task InstallerFailure_CleansPrivateEnvironmentAndCannotRedirectOutputToBuildToolsWork()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Forge hostile environment");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var redirectedDirectory = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "BuildToolsWork redirect")).FullName;
        var sentinel = Path.Combine(redirectedDirectory, "user-owned.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        string? capturedOperationRoot = null;
        var runner = new RecordingRunner(async startInfo =>
        {
            capturedOperationRoot = Directory.GetParent(startInfo.WorkingDirectory)!.FullName;
            var privateHome = startInfo.Environment["HOME"]!;
            var privateTemp = startInfo.Environment["TEMP"]!;
            AssertPathIsUnder(capturedOperationRoot, startInfo.WorkingDirectory);
            AssertPathIsUnder(capturedOperationRoot, privateHome);
            AssertPathIsUnder(capturedOperationRoot, privateTemp);
            Assert.NotEqual(Path.GetFullPath(redirectedDirectory), startInfo.WorkingDirectory);
            Assert.Equal($"-Duser.dir={startInfo.WorkingDirectory}", startInfo.ArgumentList[2]);
            Assert.Equal(startInfo.WorkingDirectory, startInfo.ArgumentList[^1]);
            Assert.DoesNotContain("_JAVA_OPTIONS", startInfo.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("JAVA_TOOL_OPTIONS", startInfo.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("JDK_JAVA_OPTIONS", startInfo.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                startInfo.Environment.Keys,
                static key => key.StartsWith("MAVEN", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("GRADLE", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase));

            await File.WriteAllTextAsync(Path.Combine(privateHome, "installer-home.txt"), "private");
            await File.WriteAllTextAsync(Path.Combine(privateTemp, "installer-temp.txt"), "private");
            await File.WriteAllTextAsync(Path.Combine(startInfo.WorkingDirectory, "partial.txt"), "partial");
            throw new ModrinthLoaderBootstrapProcessException(
                new ModrinthLoaderBootstrapProcessResult(1, [], ["failed"]));
        });

        await Assert.ThrowsAsync<ModrinthLoaderBootstrapProcessException>(() =>
            new ModrinthLoaderServerBootstrapper(new FakeArtifacts(), runner).BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Forge,
                    "1.20.1",
                    "47.2.0"),
                staging,
                java));

        Assert.NotNull(capturedOperationRoot);
        Assert.False(Directory.Exists(capturedOperationRoot));
        Assert.Equal([sentinel], Directory.EnumerateFiles(redirectedDirectory));
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        Assert.True(File.Exists(Path.Combine(staging, "mods", "pack-mod.jar")));
        Assert.Single(Directory.EnumerateFileSystemEntries(staging));
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Fact]
    public async Task OutputConflict_NeverOverwritesExistingServerJar()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Vanilla conflict");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var existing = Path.Combine(staging, "server.jar");
        await File.WriteAllTextAsync(existing, "user-owned-server");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new ModrinthLoaderServerBootstrapper(new FakeArtifacts(), new RecordingRunner())
                .BootstrapAsync(
                    new ModrinthModpackLoaderInstallRequest(
                        ModrinthModpackLoaderKind.Vanilla,
                        "1.20.1",
                        null),
                    staging,
                    java));

        Assert.Contains("不會覆寫", error.Message, StringComparison.Ordinal);
        Assert.Equal("user-owned-server", await File.ReadAllTextAsync(existing));
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Fact]
    public async Task FabricServerVerificationFailure_DoesNotMergeAnyInstallerOutput()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Fabric invalid server");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var artifacts = new FakeArtifacts { FailServerVerification = true };
        var runner = new RecordingRunner(async startInfo =>
        {
            await File.WriteAllTextAsync(Path.Combine(startInfo.WorkingDirectory, "fabric-server-launch.jar"), "launcher");
            await File.WriteAllTextAsync(
                Path.Combine(startInfo.WorkingDirectory, "fabric-server-launcher.properties"),
                "serverJar=server.jar");
            await File.WriteAllTextAsync(Path.Combine(startInfo.WorkingDirectory, "server.jar"), "tampered");
            return SuccessResult();
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModrinthLoaderServerBootstrapper(artifacts, runner).BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Fabric,
                    "1.20.1",
                    "0.16.9"),
                staging,
                java));

        Assert.False(File.Exists(Path.Combine(staging, "fabric-server-launch.jar")));
        Assert.False(File.Exists(Path.Combine(staging, "server.jar")));
        Assert.True(File.Exists(Path.Combine(staging, "mods", "pack-mod.jar")));
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Fact]
    public async Task InstallerExitZeroWithoutRunnableOutput_IsRejectedAndCleaned()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var staging = CreatePackStaging(temporaryDirectory.Path, "Forge incomplete");
        var java = await CreateJavaAsync(temporaryDirectory.Path);
        var runner = new RecordingRunner(async startInfo =>
        {
            await File.WriteAllTextAsync(Path.Combine(startInfo.WorkingDirectory, "install.log"), "done?");
            return SuccessResult();
        });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModrinthLoaderServerBootstrapper(new FakeArtifacts(), runner).BootstrapAsync(
                new ModrinthModpackLoaderInstallRequest(
                    ModrinthModpackLoaderKind.Forge,
                    "1.20.1",
                    "47.2.0"),
                staging,
                java));

        Assert.Contains("可啟動", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(staging, "install.log")));
        Assert.Empty(TemporaryOperationDirectories(temporaryDirectory.Path));
    }

    [Fact]
    public async Task OperationCleanup_TransientWindowsLockRetriesThenSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var operation = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, ".muhun-loader-transient")).FullName;
        var lockedFile = Path.Combine(operation, "installer.log");
        await File.WriteAllTextAsync(lockedFile, "temporary installer output");
        FileStream? blocker = new(
            lockedFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var identity = SafePath.GetExistingObjectIdentity(operation);

        try
        {
            var cleanupTask = new ModrinthLoaderOperationCleanup().DeleteAsync(
                temporaryDirectory.Path,
                operation,
                identity,
                CancellationToken.None);
            Assert.False(cleanupTask.IsCompleted);
            blocker.Dispose();
            blocker = null;

            await cleanupTask.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.False(Directory.Exists(operation));
        }
        finally
        {
            blocker?.Dispose();
            SafePath.DeleteTreeWithoutFollowingReparsePoints(
                temporaryDirectory.Path,
                operation);
        }
    }

    [Fact]
    public async Task OperationCleanup_ReplacedRootReparseNeverDeletesExternalTarget()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var operation = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, ".muhun-loader-original")).FullName;
        var identity = SafePath.GetExistingObjectIdentity(operation);
        var displaced = Path.Combine(temporaryDirectory.Path, "displaced-operation");
        Directory.Move(operation, displaced);
        var outside = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "external-target")).FullName;
        var marker = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        ReparsePointTestHelper.CreateDirectoryLink(operation, outside);

        try
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                new ModrinthLoaderOperationCleanup().DeleteAsync(
                    temporaryDirectory.Path,
                    operation,
                    identity,
                    CancellationToken.None));

            Assert.True(File.Exists(marker));
            Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            if (Directory.Exists(operation))
            {
                Directory.Delete(operation, recursive: false);
            }
        }
    }

    private static async Task WriteFabricInstallerOutputAsync(
        string root,
        string loaderVersion,
        byte[] serverBytes)
    {
        var dependency = Path.Combine(
            root,
            "libraries",
            "net",
            "fabricmc",
            "fabric-loader",
            loaderVersion,
            $"fabric-loader-{loaderVersion}.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(dependency)!);
        await File.WriteAllTextAsync(dependency, "verified Fabric loader dependency");
        await File.WriteAllBytesAsync(Path.Combine(root, "server.jar"), serverBytes);

        await using var output = new FileStream(
            Path.Combine(root, "fabric-server-launch.jar"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            32 * 1024,
            FileOptions.Asynchronous);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
        await using (var stream = manifest.Open())
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "Manifest-Version: 1.0\r\n"
                + "Main-Class: net.fabricmc.loader.impl.launch.server.FabricServerLauncher\r\n"
                + $"Class-Path: libraries/net/fabricmc/fabric-loader/{loaderVersion}/fabric-loader-{loaderVersion}.jar\r\n\r\n");
        }

        var properties = archive.CreateEntry("fabric-server-launch.properties");
        await using (var stream = properties.Open())
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "launch.mainClass=net.fabricmc.loader.impl.launch.knot.KnotServer\n");
        }
    }

    private static async Task WriteStandardForgeFamilyOutputAsync(
        string root,
        ModrinthModpackLoaderKind kind,
        string minecraftVersion,
        string loaderVersion)
    {
        var isForge = kind == ModrinthModpackLoaderKind.Forge;
        var directory = isForge
            ? $"libraries/net/minecraftforge/forge/{minecraftVersion}-{loaderVersion}"
            : $"libraries/net/neoforged/neoforge/{loaderVersion}";
        var absoluteDirectory = Path.Combine(
            root,
            directory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(absoluteDirectory);
        var loaderOption = isForge ? "--fml.forgeVersion" : "--fml.neoForgeVersion";
        var launchTarget = isForge ? "forge_server" : "neoforgeserver";
        var arguments = $"--launchTarget {launchTarget}\n"
            + $"{loaderOption} {loaderVersion}\n"
            + $"--fml.mcVersion {minecraftVersion}\n";
        await File.WriteAllTextAsync(Path.Combine(absoluteDirectory, "win_args.txt"), arguments);
        await File.WriteAllTextAsync(Path.Combine(absoluteDirectory, "unix_args.txt"), arguments);
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.bat"),
            $"@echo off\njava @{directory}/win_args.txt %*\n");
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.sh"),
            $"#!/usr/bin/env sh\njava @{directory}/unix_args.txt \"$@\"\n");
    }

    private static string CreatePackStaging(string parent, string name)
    {
        var staging = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(staging, "mods")).FullName;
        File.WriteAllText(Path.Combine(mods, "pack-mod.jar"), "mod");
        return staging;
    }

    private static async Task<string> CreateJavaAsync(string parent)
    {
        var javaDirectory = Directory.CreateDirectory(Path.Combine(parent, "Java Runtime", "bin")).FullName;
        var java = Path.Combine(javaDirectory, OperatingSystem.IsWindows() ? "java.exe" : "java");
        await File.WriteAllBytesAsync(java, [0x4d, 0x5a]);
        return java;
    }

    private static IEnumerable<string> TemporaryOperationDirectories(string parent)
        => Directory.EnumerateDirectories(parent, ".muhun-loader-*", SearchOption.TopDirectoryOnly);

    private static void AssertPathIsUnder(string parent, string candidate)
    {
        var parentPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent))
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(parentPrefix, Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase);
    }

    private static ModrinthLoaderBootstrapProcessResult SuccessResult()
        => new(0, ["installed"], []);

    private sealed class FakeArtifacts : IModrinthOfficialLoaderArtifactProvider
    {
        public byte[] ServerBytes { get; } = Encoding.UTF8.GetBytes("official minecraft server");

        public bool FailServerVerification { get; init; }

        public bool FailInstallerDownload { get; init; }

        public int FabricDownloads { get; private set; }

        public int ForgeDownloads { get; private set; }

        public int NeoForgeDownloads { get; private set; }

        public int ServerVerifications { get; private set; }

        public int TotalDownloads => FabricDownloads + ForgeDownloads + NeoForgeDownloads + VanillaDownloads;

        private int VanillaDownloads { get; set; }

        public async Task<ModrinthLoaderArtifact> DownloadVanillaServerAsync(
            string minecraftVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            VanillaDownloads++;
            await File.WriteAllBytesAsync(destinationPath, ServerBytes, cancellationToken);
            progress?.Report(1d);
            return Artifact(ModrinthLoaderArtifactKind.MinecraftServer, destinationPath, ServerBytes.Length);
        }

        public async Task VerifyVanillaServerAsync(
            string minecraftVersion,
            string serverJarPath,
            CancellationToken cancellationToken = default)
        {
            ServerVerifications++;
            if (FailServerVerification)
            {
                throw new InvalidDataException("Mojang SHA-1 mismatch");
            }

            Assert.Equal(ServerBytes, await File.ReadAllBytesAsync(serverJarPath, cancellationToken));
        }

        public Task<ModrinthLoaderArtifact> DownloadLatestStableFabricInstallerAsync(
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            FabricDownloads++;
            return WriteInstallerAsync(
                ModrinthLoaderArtifactKind.FabricInstaller,
                destinationPath,
                progress,
                cancellationToken);
        }

        public Task<ModrinthLoaderArtifact> DownloadForgeInstallerAsync(
            string minecraftVersion,
            string loaderVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ForgeDownloads++;
            if (FailInstallerDownload)
            {
                return Task.FromException<ModrinthLoaderArtifact>(
                    new InvalidDataException("installer download failed before process start"));
            }

            return WriteInstallerAsync(
                ModrinthLoaderArtifactKind.ForgeInstaller,
                destinationPath,
                progress,
                cancellationToken);
        }

        public Task<ModrinthLoaderArtifact> DownloadNeoForgeInstallerAsync(
            string loaderVersion,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            NeoForgeDownloads++;
            return WriteInstallerAsync(
                ModrinthLoaderArtifactKind.NeoForgeInstaller,
                destinationPath,
                progress,
                cancellationToken);
        }

        private static async Task<ModrinthLoaderArtifact> WriteInstallerAsync(
            ModrinthLoaderArtifactKind kind,
            string destinationPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes("verified official installer");
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
            progress?.Report(1d);
            return Artifact(kind, destinationPath, bytes.Length);
        }

        private static ModrinthLoaderArtifact Artifact(
            ModrinthLoaderArtifactKind kind,
            string path,
            long size)
            => new(
                kind,
                path,
                new Uri("https://official.example/artifact.jar"),
                size,
                "SHA-256",
                new string('0', 64));
    }

    private sealed class RecordingRunner(
        Func<ProcessStartInfo, Task<ModrinthLoaderBootstrapProcessResult>>? run = null)
        : IModrinthLoaderBootstrapProcessRunner
    {
        public List<ProcessStartInfo> StartInfos { get; } = [];

        public async Task<ModrinthLoaderBootstrapProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            StartInfos.Add(startInfo);
            cancellationToken.ThrowIfCancellationRequested();
            return run is null ? SuccessResult() : await run(startInfo);
        }
    }

    private sealed class FailingOperationCleanup(Exception failure)
        : IModrinthLoaderOperationCleanup
    {
        public int Calls { get; private set; }

        public string? LastOperationRoot { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task DeleteAsync(
            string trustedParent,
            string operationRoot,
            SafePathObjectIdentity? expectedOperationIdentity,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastOperationRoot = operationRoot;
            LastCancellationToken = cancellationToken;
            return Task.FromException(failure);
        }
    }

    private sealed class InlineProgress(Action<ModrinthLoaderBootstrapOutputLine> report)
        : IProgress<ModrinthLoaderBootstrapOutputLine>
    {
        public void Report(ModrinthLoaderBootstrapOutputLine value) => report(value);
    }
}
