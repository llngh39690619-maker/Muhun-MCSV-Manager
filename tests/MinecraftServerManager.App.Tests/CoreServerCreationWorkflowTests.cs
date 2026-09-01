using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class CoreServerCreationWorkflowTests
{
    [Fact]
    public async Task DirectJar_ChineseAndSpacePath_PromotesReadyInstanceWithoutManagerSideEffects()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var appRoot = Path.Combine(directory.Path, "管理器 中文 空白");
        var fixture = CreateFixture(
            appRoot,
            CoreServerSoftware.Paper,
            CoreType.Paper,
            install: async (staging, _, cancellationToken) =>
            {
                await WriteJarAsync(
                    Path.Combine(staging, "server.jar"),
                    "io/papermc/paper/Test.class",
                    "io.papermc.paperclip.Main",
                    cancellationToken);
                return new CoreServerBackendInstallResult(["server.jar"]);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "中文 核心 Server", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(CoreType.Paper, instance.CoreType);
        Assert.Equal(ServerLaunchKind.ExecutableJar, instance.LaunchKind);
        Assert.Equal("1.20.1", instance.MinecraftVersion);
        Assert.Contains("中文 核心 Server", instance.DirectoryPath, StringComparison.Ordinal);
        Assert.True(File.Exists(instance.ServerJarPath));
        Assert.True(File.Exists(instance.JavaExecutablePath));
        Assert.Equal(["nogui"], instance.ServerArguments);
        Assert.Null(instance.StopCommand);
        Assert.Contains(
            "eula=true",
            await File.ReadAllTextAsync(Path.Combine(instance.DirectoryPath, "eula.txt")),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(instance.DirectoryPath, "server.properties")));
        Assert.False(File.Exists(fixture.Paths.SettingsFile));
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Fact]
    public async Task ArgumentFileLoader_IsStaticallyValidatedAndReturnedReady()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var sourcePlan = CreateLoaderBuild(
            CoreType.Forge,
            "1.20.1",
            "47.2.0",
            OfficialServerInstallStrategy.ForgeInstaller);
        var installer = Path.Combine(directory.Path, "verified-forge-installer.jar");
        await File.WriteAllTextAsync(installer, "verified installer");
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Forge,
            CoreType.Forge,
            acceptsArgumentFiles: true,
            loaderVersion: "47.2.0",
            sourceId: OfficialCoreServerCreationBackend.SourceId,
            sourcePlan: sourcePlan,
            installKind: CoreServerInstallKind.OfficialLoaderInstaller,
            install: async (staging, _, cancellationToken) =>
            {
                var loaderDirectory = Path.Combine(
                    staging,
                    "libraries",
                    "net",
                    "minecraftforge",
                    "forge",
                    "1.20.1-47.2.0");
                Directory.CreateDirectory(loaderDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(loaderDirectory, "win_args.txt"),
                    "--launchTarget forge_server\n--fml.mcVersion 1.20.1\n--fml.forgeVersion 47.2.0\n",
                    cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(loaderDirectory, "unix_args.txt"),
                    "--launchTarget forge_server\n--fml.mcVersion 1.20.1\n--fml.forgeVersion 47.2.0\n",
                    cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "user_jvm_args.txt"),
                    "-Xms1G\n-Xmx4G\n",
                    cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "run.bat"),
                    "@echo off\r\njava @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.2.0/win_args.txt nogui\r\n",
                    cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "run.sh"),
                    "#!/bin/sh\njava @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.2.0/unix_args.txt nogui\n",
                    cancellationToken);
                return await CreateOfficialLoaderInstallResultAsync(
                    staging,
                    installer,
                    sourcePlan,
                    ["run.bat", "run.sh"],
                    cancellationToken);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Forge Argfile", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(CoreType.Forge, instance.CoreType);
        Assert.Equal(ServerLaunchKind.JavaArgumentFiles, instance.LaunchKind);
        Assert.Contains(
            instance.JavaArgumentFilePaths,
            path => path.EndsWith("win_args.txt", StringComparison.OrdinalIgnoreCase));
        Assert.All(instance.JavaArgumentFilePaths, path =>
        {
            Assert.False(Path.IsPathRooted(path));
            Assert.True(File.Exists(Path.Combine(instance.DirectoryPath, path)));
        });
        Assert.Equal(1024, instance.MinimumMemoryMb);
        Assert.Equal(4096, instance.MaximumMemoryMb);
        Assert.True(File.Exists(instance.SourceLaunchScriptPath));
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Fact]
    public async Task Fabric26_TypedManifestLauncher_IsPromotedWithoutGenericWrapperRejection()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var sourcePlan = CreateLoaderBuild(
            CoreType.Fabric,
            "26.2",
            "0.19.3",
            OfficialServerInstallStrategy.FabricInstaller);
        var installer = Path.Combine(directory.Path, "verified-fabric-installer.jar");
        await File.WriteAllTextAsync(installer, "verified installer");
        var fixture = CreateFixture(
            Path.Combine(directory.Path, "管理器 中文 空白"),
            CoreServerSoftware.Fabric,
            CoreType.Fabric,
            loaderVersion: "0.19.3",
            minecraftVersion: "26.2",
            sourceId: OfficialCoreServerCreationBackend.SourceId,
            sourcePlan: sourcePlan,
            installKind: CoreServerInstallKind.OfficialLoaderInstaller,
            install: async (staging, _, cancellationToken) =>
            {
                await WriteFabricManifestLauncherAsync(staging, "0.19.3", cancellationToken);
                return await CreateOfficialLoaderInstallResultAsync(
                    staging,
                    installer,
                    sourcePlan,
                    ["fabric-server-launch.jar"],
                    cancellationToken);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Fabric 26.2 中文 Server", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(CoreType.Fabric, instance.CoreType);
        Assert.Equal(ServerLaunchKind.ExecutableJar, instance.LaunchKind);
        Assert.EndsWith(
            "fabric-server-launch.jar",
            instance.ServerJarPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(instance.ServerJarPath));
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Fact]
    public async Task NeoForge26_TypedDirectMainArguments_ArePromotedWithoutWeakeningOnlineGate()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var sourcePlan = CreateLoaderBuild(
            CoreType.NeoForge,
            "26.2",
            "26.2.0.61",
            OfficialServerInstallStrategy.NeoForgeInstaller);
        var installer = Path.Combine(directory.Path, "verified-neoforge-installer.jar");
        await File.WriteAllTextAsync(installer, "verified installer");
        var fixture = CreateFixture(
            Path.Combine(directory.Path, "管理器 中文 空白"),
            CoreServerSoftware.NeoForge,
            CoreType.NeoForge,
            acceptsArgumentFiles: true,
            loaderVersion: "26.2.0.61",
            minecraftVersion: "26.2",
            sourceId: OfficialCoreServerCreationBackend.SourceId,
            sourcePlan: sourcePlan,
            installKind: CoreServerInstallKind.OfficialLoaderInstaller,
            install: async (staging, _, cancellationToken) =>
            {
                await WriteNeoForgeDirectMainOutputAsync(
                    staging,
                    "26.2.0.61",
                    cancellationToken);
                return await CreateOfficialLoaderInstallResultAsync(
                    staging,
                    installer,
                    sourcePlan,
                    ["run.bat", "run.sh"],
                    cancellationToken);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "NeoForge 26.2 中文 Server", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(CoreType.NeoForge, instance.CoreType);
        Assert.Equal(ServerLaunchKind.JavaArgumentFiles, instance.LaunchKind);
        Assert.Contains(
            instance.JavaArgumentFilePaths,
            path => path.EndsWith("win_args.txt", StringComparison.OrdinalIgnoreCase));
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Fact]
    public async Task Cancellation_RemovesManagerOwnedStagingAndDoesNotPromote()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var installStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Paper,
            CoreType.Paper,
            install: async (staging, _, cancellationToken) =>
            {
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "incomplete.bin"),
                    "partial",
                    cancellationToken);
                installStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);
        using var cancellation = new CancellationTokenSource();

        var operation = fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Cancelled Server", true),
            new InlineProgress<CoreServerCreationProgress>(),
            cancellation.Token);
        await installStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.Servers));
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Fact]
    public async Task Cancellation_RemovesReadOnlyGitLikeFilesBeforeReportingCancelled()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var installStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Spigot,
            CoreType.Spigot,
            installKind: CoreServerInstallKind.SpigotBuildTools,
            requireMinecraftEvidence: false,
            install: async (staging, _, cancellationToken) =>
            {
                var pack = Path.Combine(staging, "BuildData", ".git", "objects", "pack");
                Directory.CreateDirectory(pack);
                var index = Path.Combine(pack, "pack-test.idx");
                await File.WriteAllTextAsync(index, "partial", cancellationToken);
                File.SetAttributes(index, File.GetAttributes(index) | FileAttributes.ReadOnly);
                installStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);
        using var cancellation = new CancellationTokenSource();

        var operation = fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Cancelled ReadOnly Build", true),
            new InlineProgress<CoreServerCreationProgress>(),
            cancellation.Token);
        await installStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.Servers));
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrapperOrWrongCoreJar_IsRejectedAndCleaned(bool wrongRecognizedCore)
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Paper,
            CoreType.Paper,
            install: async (staging, _, cancellationToken) =>
            {
                if (wrongRecognizedCore)
                {
                    await WriteJarAsync(
                        Path.Combine(staging, "server.jar"),
                        "net/minecraft/bundler/Main.class",
                        "net.minecraft.bundler.Main",
                        cancellationToken);
                }
                else
                {
                    await WriteJarAsync(
                        Path.Combine(staging, "server.jar"),
                        "example/wrapper/Bootstrap.class",
                        "example.wrapper.Bootstrap",
                        cancellationToken,
                        includeVersion: false);
                }

                return new CoreServerBackendInstallResult(["server.jar"]);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Workflow.CreateAsync(
                new CoreServerCreationRequest(product, version, "Rejected Wrapper", true),
                new InlineProgress<CoreServerCreationProgress>(),
                CancellationToken.None));

        Assert.Contains("wrapper", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.Servers));
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Fact]
    public async Task MutatedCoreOrVersion_IsRejectedBeforeInstall()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Paper,
            CoreType.Paper,
            install: (_, _, _) => throw new Xunit.Sdk.XunitException(
                "Mutated selection must fail before install."));
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(
                product with { Software = CoreServerSoftware.Vanilla },
                version,
                "Mutated",
                true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(
                product,
                version with { Build = "untrusted-wrapper-build" },
                "Mutated",
                true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Paths.Servers));
    }

    [Fact]
    public async Task Velocity_UsesPortArgumentAndShutdownCommandWithoutServerProperties()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Velocity,
            CoreType.Velocity,
            minecraftVersion: "4.0.0",
            requireMinecraftEvidence: false,
            install: async (staging, _, cancellationToken) =>
            {
                await WriteJarAsync(
                    Path.Combine(staging, "server.jar"),
                    "com/velocitypowered/proxy/Velocity.class",
                    "com.velocitypowered.proxy.Velocity",
                    cancellationToken,
                    includeVersion: false);
                return new CoreServerBackendInstallResult(["server.jar"]);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Velocity Proxy"),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(["--port", "25565"], instance.ServerArguments);
        Assert.Equal("shutdown", instance.StopCommand);
        Assert.False(File.Exists(Path.Combine(instance.DirectoryPath, "server.properties")));
    }

    [Fact]
    public async Task PublicComposition_OwnsAllProductionBackendsAndReturnsRequestedOrder()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var workflow = new CoreServerCreationWorkflow(new ApplicationPaths(directory.Path));
        var backend = typeof(CoreServerCreationWorkflow).GetField(
            "_backend",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(backend);
        Assert.IsType<CompositeCoreServerCreationBackend>(backend!.GetValue(workflow));
        var products = await workflow.GetAvailableCoresAsync(CancellationToken.None);
        Assert.Equal(
            Enum.GetValues<CoreServerSoftware>(),
            products.Select(product => product.Software));
    }

    [Theory]
    [InlineData(CoreServerSoftware.Spigot, CoreType.Spigot, true)]
    [InlineData(CoreServerSoftware.CraftBukkit, CoreType.CraftBukkit, false)]
    public async Task BuildToolsOutput_DeterministicallyDistinguishesSpigotAndCraftBukkit(
        CoreServerSoftware software,
        CoreType coreType,
        bool includeSpigotMarker)
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var fixture = CreateFixture(
            directory.Path,
            software,
            coreType,
            requireMinecraftEvidence: false,
            installKind: CoreServerInstallKind.SpigotBuildTools,
            install: async (staging, javaExecutable, cancellationToken) =>
            {
                Assert.Contains("JDK 中文", javaExecutable, StringComparison.Ordinal);
                Assert.True(File.Exists(Path.Combine(
                    Path.GetDirectoryName(javaExecutable)!,
                    "javac.exe")));
                var entries = new List<string> { "org/bukkit/craftbukkit/Main.class" };
                if (includeSpigotMarker)
                {
                    entries.Add("org/spigotmc/SpigotConfig.class");
                }

                await WriteJarWithEntriesAsync(
                    Path.Combine(staging, "server.jar"),
                    entries,
                    "org.bukkit.craftbukkit.Main",
                    cancellationToken,
                    includeVersion: false);
                return new CoreServerBackendInstallResult(["server.jar"]);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, $"{software} exact output", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(coreType, instance.CoreType);
    }

    [Fact]
    public async Task SpigotBackend_UsesVerifiedJdkAndPassesServerStagingAsRunnerTrustRoot()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var source = new FakeSpigotBuildToolsSource(CoreType.Spigot, "1.21.4", 21);
        var installer = new RecordingSpigotBuildToolsInstaller();
        var backend = new SpigotCoreServerCreationBackend(source, installer);
        var paths = new ApplicationPaths(Path.Combine(directory.Path, "中文 Spigot 空白"));
        using var workflow = new CoreServerCreationWorkflow(
            paths,
            backend,
            new UnexpectedJavaResolver(),
            new FakeJdkResolver(paths.Root, 21));
        var product = Assert.Single(await workflow.GetAvailableCoresAsync(CancellationToken.None),
            item => item.Software == CoreServerSoftware.Spigot);
        var version = Assert.Single(await workflow.GetVersionsAsync(product, CancellationToken.None));

        var progress = new InlineProgress<CoreServerCreationProgress>();
        var instance = await workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Spigot-1.21.4", true),
            progress,
            CancellationToken.None);

        Assert.Equal(CoreType.Spigot, instance.CoreType);
        Assert.Contains("JDK 中文 21", installer.JavaExecutablePath, StringComparison.Ordinal);
        Assert.Equal(installer.StagingRoot, Path.GetDirectoryName(installer.DestinationPath));
        Assert.StartsWith(installer.StagingRoot, installer.BuildToolsJarPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(instance.DirectoryPath, ".buildtools")));
        Assert.True(File.Exists(instance.ServerJarPath));
        var genericDetection = new JarCoreDetector().Detect(instance.ServerJarPath);
        Assert.NotEqual(CoreType.Spigot, genericDetection.CoreType);
        Assert.Equal("org.bukkit.craftbukkit.bootstrap.Main", genericDetection.MainClass);
        var buildOutput = Assert.Single(
            progress.Values,
            value => value.Detail == "Starting clone of Bukkit");
        Assert.Equal(CoreServerCreationStage.Installing, buildOutput.Stage);
        Assert.Contains("隔離環境建置 Spigot 1.21.4", buildOutput.Message, StringComparison.Ordinal);
        Assert.Equal(48, buildOutput.Percentage);
        Assert.True(buildOutput.IsDetailIndeterminate);
        Assert.Contains(
            progress.Values,
            value => value.Stage == CoreServerCreationStage.Verifying
                     && value.Percentage == 86
                     && value.Message.Contains("官方 output SHA-256", StringComparison.Ordinal)
                     && value.Detail is null);
    }

    [Fact]
    public async Task BuildToolsTypedProvenance_RehashesStagingAndRejectsPostBackendMutation()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var expectedBytes = CreateModernSpigotBootstrapJarBytes("1.21.4");
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant();
        var sourcePlan = new SpigotBuildPlan(
            CoreType.Spigot,
            "Spigot",
            "1.21.4",
            21,
            "server.jar",
            expectedSha256,
            181,
            "4458",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BuildData"] = new string('a', 40),
                ["Bukkit"] = new string('b', 40),
                ["CraftBukkit"] = new string('c', 40),
                ["Spigot"] = new string('d', 40)
            },
            SpigotBuildToolsProvider.ReviewedBuildTools);
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Spigot,
            CoreType.Spigot,
            minecraftVersion: "1.21.4",
            javaMajorVersion: 21,
            requireMinecraftEvidence: false,
            sourceId: SpigotCoreServerCreationBackend.SourceId,
            sourcePlan: sourcePlan,
            installKind: CoreServerInstallKind.SpigotBuildTools,
            install: async (staging, _, cancellationToken) =>
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(staging, "server.jar"),
                    CreateModernSpigotBootstrapJarBytes("1.21.4", payloadByte: 0x05),
                    cancellationToken);
                return new CoreServerBackendInstallResult(
                    ["server.jar"],
                    new CoreServerVerifiedInstallProvenance(
                        CoreServerInstallProvenanceKind.SpigotBuildToolsOfficialOutput,
                        CoreType.Spigot,
                        "1.21.4",
                        ["server.jar"],
                        expectedSha256));
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Workflow.CreateAsync(
                new CoreServerCreationRequest(product, version, "mutated Spigot", true),
                new InlineProgress<CoreServerCreationProgress>(),
                CancellationToken.None));

        Assert.Contains("backend 驗證後已變更", exception.Message, StringComparison.Ordinal);
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Fact]
    public async Task SpigotBackend_HistoricalCatalogClearlyLabelsOfficialSourceVerification()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var source = new FakeSpigotBuildToolsSource(
            CoreType.Spigot,
            "1.8.8",
            8,
            SpigotBuildOutputVerificationKind.OfficialSourceRefs);
        var backend = new SpigotCoreServerCreationBackend(
            source,
            new RecordingSpigotBuildToolsInstaller());
        var paths = new ApplicationPaths(directory.Path);
        using var workflow = new CoreServerCreationWorkflow(
            paths,
            backend,
            new UnexpectedJavaResolver(),
            new FakeJdkResolver(paths.Root, 8));
        var product = Assert.Single(await workflow.GetAvailableCoresAsync(CancellationToken.None),
            item => item.Software == CoreServerSoftware.Spigot);

        var version = Assert.Single(await workflow.GetVersionsAsync(product, CancellationToken.None));

        Assert.Contains("官方來源 refs 驗證", version.Build, StringComparison.Ordinal);
        Assert.Contains("上游未提供成品 SHA-256", version.Build, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoricalSpigot188_TypedSourceProvenancePromotesToFinalServerInstance()
    {
        // This reproduces the flat layout emitted by the live 1.8.8 BuildTools gate. Historical
        // JARs do not self-report the Minecraft version, so the version identity comes from the
        // pinned four-ref plan while the backend proof locks the verified bytes through promotion.
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var jarBytes = CreateLegacySpigotJarBytes();
        var actualSha256 = Convert.ToHexString(SHA256.HashData(jarBytes)).ToLowerInvariant();
        var sourcePlan = new SpigotBuildPlan(
            CoreType.Spigot,
            "Spigot",
            "1.8.8",
            8,
            "server.jar",
            ExpectedOutputSha256: null,
            RequiredBuildToolsVersion: 1,
            VersionIdentity: "582b",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BuildData"] = "838b40587fa7a68a130b75252959bc8a3481d94f",
                ["Bukkit"] = "01d1820664a5f881665b84b28871dadd132deaef",
                ["CraftBukkit"] = "741a1bdf3db8c4d5237407df2872d9857427bfaf",
                ["Spigot"] = "3c60ece1480c9b686b06f33daa6ca23c8883e9f2"
            },
            SpigotBuildToolsProvider.ReviewedBuildTools,
            SpigotBuildOutputVerificationKind.OfficialSourceRefs,
            BuildRevision: "1.8.8");
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Spigot,
            CoreType.Spigot,
            minecraftVersion: "1.8.8",
            javaMajorVersion: 8,
            requireMinecraftEvidence: false,
            sourceId: SpigotCoreServerCreationBackend.SourceId,
            sourcePlan: sourcePlan,
            installKind: CoreServerInstallKind.SpigotBuildTools,
            install: async (staging, _, cancellationToken) =>
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(staging, "server.jar"),
                    jarBytes,
                    cancellationToken);
                return new CoreServerBackendInstallResult(
                    ["server.jar"],
                    new CoreServerVerifiedInstallProvenance(
                        CoreServerInstallProvenanceKind.SpigotBuildToolsOfficialSources,
                        CoreType.Spigot,
                        "1.8.8",
                        ["server.jar"],
                        actualSha256));
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Spigot-1.8.8", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(CoreType.Spigot, instance.CoreType);
        Assert.Equal("1.8.8", instance.MinecraftVersion);
        Assert.True(File.Exists(instance.ServerJarPath));
        Assert.Equal(jarBytes, await File.ReadAllBytesAsync(instance.ServerJarPath));
        var postDetector = new JarCoreDetector().Detect(instance.ServerJarPath);
        Assert.True(postDetector.IsValidJar);
        AssertNoTemporaryTrees(fixture.Paths.Servers);
    }

    [Fact]
    public async Task HistoricalPaper_UsesPinnedArtifactIdentityWhenJarOmitsEmbeddedVersion()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Paper,
            CoreType.Paper,
            minecraftVersion: "1.12.2",
            requireMinecraftEvidence: false,
            install: async (staging, _, cancellationToken) =>
            {
                await WriteJarAsync(
                    Path.Combine(staging, "server.jar"),
                    "io/papermc/paperclip/Main.class",
                    "io.papermc.paperclip.Main",
                    cancellationToken,
                    includeVersion: false);
                return new CoreServerBackendInstallResult(["server.jar"]);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Paper-1.12.2", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(CoreType.Paper, instance.CoreType);
        Assert.Equal("1.12.2", instance.MinecraftVersion);
    }

    [Fact]
    public async Task LegacyVanilla125_ExactMojangPlanAcceptsOfficialMainClassWithoutEmbeddedVersion()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var jarBytes = CreateJarBytes(
            "net/minecraft/server/MinecraftServer.class",
            "net.minecraft.server.MinecraftServer",
            includeVersion: false);
        var sha1 = Convert.ToHexString(SHA1.HashData(jarBytes)).ToLowerInvariant();
        var sourcePlan = CreateVanilla125Build(
            new Uri($"https://launcher.mojang.com/v1/objects/{sha1}/server.jar"),
            jarBytes.Length,
            sha1);
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Vanilla,
            CoreType.Vanilla,
            minecraftVersion: "1.2.5",
            javaMajorVersion: 8,
            requireMinecraftEvidence: false,
            sourceId: OfficialCoreServerCreationBackend.SourceId,
            sourcePlan: sourcePlan,
            install: async (staging, _, cancellationToken) =>
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(staging, "server.jar"),
                    jarBytes,
                    cancellationToken);
                return new CoreServerBackendInstallResult(["server.jar"]);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var instance = await fixture.Workflow.CreateAsync(
            new CoreServerCreationRequest(product, version, "Vanilla-1.2.5", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(CoreType.Vanilla, instance.CoreType);
        Assert.Equal("1.2.5", instance.MinecraftVersion);
        Assert.True(File.Exists(instance.ServerJarPath));
    }

    [Fact]
    public async Task LegacyVanilla125_ActualOfficialMetadataRejectsDifferentJarBytes()
    {
        // Mojang version metadata currently pins the real 1.2.5 server to this immutable object.
        // The fixture intentionally differs, proving that the legacy exception still rechecks it.
        const string actualSha1 = "d8321edc9470e56b8ad5c67bbd16beba25843336";
        const long actualSize = 1_408_470;
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var jarBytes = CreateJarBytes(
            "net/minecraft/server/MinecraftServer.class",
            "net.minecraft.server.MinecraftServer",
            includeVersion: false);
        var sourcePlan = CreateVanilla125Build(
            new Uri($"https://launcher.mojang.com/v1/objects/{actualSha1}/server.jar"),
            actualSize,
            actualSha1);
        var fixture = CreateFixture(
            directory.Path,
            CoreServerSoftware.Vanilla,
            CoreType.Vanilla,
            minecraftVersion: "1.2.5",
            javaMajorVersion: 8,
            requireMinecraftEvidence: false,
            sourceId: OfficialCoreServerCreationBackend.SourceId,
            sourcePlan: sourcePlan,
            install: async (staging, _, cancellationToken) =>
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(staging, "server.jar"),
                    jarBytes,
                    cancellationToken);
                return new CoreServerBackendInstallResult(["server.jar"]);
            });
        var (product, version) = await ReadSingleSelectionAsync(fixture.Workflow);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Workflow.CreateAsync(
                new CoreServerCreationRequest(product, version, "Rejected Vanilla", true),
                new InlineProgress<CoreServerCreationProgress>(),
                CancellationToken.None));

        Assert.Contains("大小", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.Servers));
    }

    [Fact]
    public async Task HybridArclight_UsesActualLoaderSelectionsAndVerifiedDirectJar()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var jarBytes = CreateJarBytes(
            "io/izzel/arclight/server/Launcher.class",
            "io.izzel.arclight.server.Launcher",
            includeVersion: false);
        var sha256 = Convert.ToHexString(SHA256.HashData(jarBytes)).ToLowerInvariant();
        const string tag = "1.20.1/1.0.0";
        var assets = new[] { "fabric", "forge" }
            .Select((loader, index) =>
            {
                var fileName = $"arclight-{loader}-1.20.1-1.0.0-abcdef{index + 1}.jar";
                return new
                {
                    id = 7001 + index,
                    state = "uploaded",
                    name = fileName,
                    digest = "sha256:" + sha256,
                    size = jarBytes.Length,
                    browser_download_url =
                        $"https://github.com/IzzelAliz/Arclight/releases/download/{tag}/{fileName}"
                };
            })
            .ToArray();
        var catalogJson = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                id = 6001,
                tag_name = tag,
                draft = false,
                prerelease = false,
                assets
            }
        });
        using var githubCatalog = new HttpClient(new StubHttpHandler(request =>
        {
            Assert.Equal(
                "https://api.github.com/repos/IzzelAliz/Arclight/releases?per_page=100&page=1",
                request.RequestUri!.AbsoluteUri);
            return CreateBytesResponse(catalogJson);
        }));
        using var mohistCatalog = new HttpClient(new StubHttpHandler(_ =>
            throw new Xunit.Sdk.XunitException("Arclight 不應呼叫 Mohist catalog。")));
        using var githubArtifacts = new HttpClient(new StubHttpHandler(request =>
        {
            var fileName = Path.GetFileName(request.RequestUri!.AbsolutePath);
            Assert.Contains("arclight-forge-", fileName, StringComparison.Ordinal);
            var response = CreateBytesResponse(jarBytes);
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = fileName
            };
            return response;
        }));
        using var mohistArtifacts = new HttpClient(new StubHttpHandler(_ =>
            throw new Xunit.Sdk.XunitException("Arclight 不應呼叫 Mohist artifact。")));
        var backend = new HybridCoreServerCreationBackend(
            new HybridServerCoreCatalogProvider(
                githubCatalog,
                mohistCatalog,
                "MuhunMCSVManager.Tests/1.0"),
            new HybridServerCoreDownloader(githubArtifacts, mohistArtifacts));
        var paths = new ApplicationPaths(Path.Combine(directory.Path, "中文 Hybrid 工作區"));
        using var workflow = new CoreServerCreationWorkflow(
            paths,
            backend,
            new FakeJavaResolver(paths.Root));
        var product = (await workflow.GetAvailableCoresAsync(CancellationToken.None))
            .Single(item => item.Software == CoreServerSoftware.Arclight);
        var versions = await workflow.GetVersionsAsync(product, CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, item => item.DisplayName.EndsWith("· fabric", StringComparison.Ordinal));
        var forge = versions.Single(item => item.DisplayName.EndsWith("· forge", StringComparison.Ordinal));

        var instance = await workflow.CreateAsync(
            new CoreServerCreationRequest(product, forge, "Arclight 中文 Server", true),
            new InlineProgress<CoreServerCreationProgress>(),
            CancellationToken.None);

        Assert.Equal(CoreType.Arclight, instance.CoreType);
        Assert.Equal("1.20.1", instance.MinecraftVersion);
        Assert.True(File.Exists(instance.ServerJarPath));
        Assert.Equal(jarBytes, await File.ReadAllBytesAsync(instance.ServerJarPath));
        AssertNoTemporaryTrees(paths.Servers);
    }

    private static WorkflowFixture CreateFixture(
        string root,
        CoreServerSoftware software,
        CoreType coreType,
        Func<string, string, CancellationToken, Task<CoreServerBackendInstallResult>> install,
        bool acceptsArgumentFiles = false,
        string? loaderVersion = null,
        string minecraftVersion = "1.20.1",
        bool requireMinecraftEvidence = true,
        int javaMajorVersion = 17,
        string sourceId = "fake",
        object? sourcePlan = null,
        CoreServerInstallKind installKind = CoreServerInstallKind.DirectJar)
    {
        var paths = new ApplicationPaths(root);
        var product = new CoreServerBackendProduct(
            new CoreServerProduct(software, $"{sourceId}:core", software.ToString(), "Test core"),
            coreType,
            sourceId,
            coreType == CoreType.Velocity);
        var version = new CoreServerBackendVersion(
            new CoreServerVersion(
                product.Product.CoreId,
                "fake:version",
                minecraftVersion,
                minecraftVersion,
                "exact-test-build"),
            javaMajorVersion);
        var backend = new FakeBackend(
            product,
            version,
            install,
            acceptsArgumentFiles,
            loaderVersion,
            requireMinecraftEvidence,
            sourcePlan,
            installKind);
        var java = new FakeJavaResolver(root, javaMajorVersion);
        var workflow = new CoreServerCreationWorkflow(
            paths,
            backend,
            java,
            new FakeJdkResolver(root, javaMajorVersion));
        return new WorkflowFixture(paths, workflow);
    }

    private static async Task<(CoreServerProduct Product, CoreServerVersion Version)>
        ReadSingleSelectionAsync(CoreServerCreationWorkflow workflow)
    {
        var product = Assert.Single(await workflow.GetAvailableCoresAsync(CancellationToken.None));
        var version = Assert.Single(await workflow.GetVersionsAsync(product, CancellationToken.None));
        return (product, version);
    }

    private static OfficialServerCoreBuildInfo CreateLoaderBuild(
        CoreType coreType,
        string minecraftVersion,
        string loaderVersion,
        OfficialServerInstallStrategy strategy)
        => new(
            coreType,
            coreType.ToString(),
            minecraftVersion,
            minecraftVersion,
            loaderVersion,
            loaderVersion,
            strategy,
            IsStable: true,
            DownloadUri: null,
            FileName: null,
            Size: null,
            HashAlgorithm: null,
            Hash: null);

    private static async Task<CoreServerBackendInstallResult> CreateOfficialLoaderInstallResultAsync(
        string staging,
        string installer,
        OfficialServerCoreBuildInfo build,
        IReadOnlyList<string> launchCandidates,
        CancellationToken cancellationToken)
    {
        var kind = build.InstallStrategy switch
        {
            OfficialServerInstallStrategy.FabricInstaller => ModrinthModpackLoaderKind.Fabric,
            OfficialServerInstallStrategy.ForgeInstaller => ModrinthModpackLoaderKind.Forge,
            OfficialServerInstallStrategy.NeoForgeInstaller => ModrinthModpackLoaderKind.NeoForge,
            _ => throw new InvalidOperationException("Test build is not a loader installer.")
        };
        var installedPaths = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(staging, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var provenance = await OfficialLoaderInstallerOutputValidator.ValidateAndCreateAsync(
            new ModrinthModpackLoaderInstallRequest(
                kind,
                build.MinecraftVersion,
                build.LoaderVersion),
            staging,
            installer,
            installedPaths,
            cancellationToken);
        return new CoreServerBackendInstallResult(
            launchCandidates,
            new CoreServerVerifiedInstallProvenance(
                CoreServerInstallProvenanceKind.OfficialLoader,
                build.CoreType,
                build.MinecraftVersion,
                launchCandidates,
                ArtifactSha256: null,
                LoaderVersion: build.LoaderVersion,
                OfficialLoader: provenance));
    }

    private static async Task WriteFabricManifestLauncherAsync(
        string root,
        string loaderVersion,
        CancellationToken cancellationToken)
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
        await File.WriteAllTextAsync(loader, "official Fabric loader", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "server.jar"),
            "official Minecraft server",
            cancellationToken);
        await using var output = new FileStream(
            Path.Combine(root, "fabric-server-launch.jar"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
        await using (var stream = manifest.Open())
        {
            await stream.WriteAsync(
                Encoding.UTF8.GetBytes(
                    "Manifest-Version: 1.0\r\n"
                    + "Main-Class: net.fabricmc.loader.impl.launch.server.FabricServerLauncher\r\n"
                    + $"Class-Path: libraries/net/fabricmc/fabric-loader/{loaderVersion}/fabric-loader-{loaderVersion}.jar\r\n\r\n"),
                cancellationToken);
        }

        var properties = archive.CreateEntry("fabric-server-launch.properties");
        await using (var stream = properties.Open())
        {
            await stream.WriteAsync(
                Encoding.UTF8.GetBytes(
                    "launch.mainClass=net.fabricmc.loader.impl.launch.knot.KnotServer\n"),
                cancellationToken);
        }
    }

    private static async Task WriteNeoForgeDirectMainOutputAsync(
        string root,
        string loaderVersion,
        CancellationToken cancellationToken)
    {
        const string loader =
            "libraries/net/neoforged/fancymodloader/loader/11.0.16/loader-11.0.16.jar";
        var loaderPath = Path.Combine(root, loader.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(loaderPath)!);
        await File.WriteAllTextAsync(loaderPath, "official FML loader", cancellationToken);
        var directory = $"libraries/net/neoforged/neoforge/{loaderVersion}";
        var absoluteDirectory = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(absoluteDirectory);
        var prefix = "--add-opens java.base/java.lang.invoke=ALL-UNNAMED\n"
            + "-Djava.net.preferIPv6Addresses=system\n"
            + "-DlibraryDirectory=libraries\n-classpath\n";
        var suffix = "\nnet.neoforged.fml.startup.Server\n"
            + $"--fml.neoForgeVersion {loaderVersion}\n"
            + "--fml.mcVersion 26.2\n--fml.neoFormVersion 2\n";
        await File.WriteAllTextAsync(
            Path.Combine(absoluteDirectory, "win_args.txt"),
            prefix + loader + suffix,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(absoluteDirectory, "unix_args.txt"),
            prefix + loader + suffix,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.bat"),
            $"@echo off\njava @user_jvm_args.txt @{directory}/win_args.txt %*\n",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "run.sh"),
            $"#!/usr/bin/env sh\njava @user_jvm_args.txt @{directory}/unix_args.txt \"$@\"\n",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "user_jvm_args.txt"),
            "# safe defaults\n-Xmx4G\n",
            cancellationToken);
    }

    private static async Task WriteJarAsync(
        string path,
        string classEntry,
        string mainClass,
        CancellationToken cancellationToken,
        bool includeVersion = true)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
        await using (var output = manifest.Open())
        {
            var bytes = Encoding.UTF8.GetBytes($"Manifest-Version: 1.0\r\nMain-Class: {mainClass}\r\n\r\n");
            await output.WriteAsync(bytes, cancellationToken);
        }

        var marker = archive.CreateEntry(classEntry);
        await using (var output = marker.Open())
        {
            await output.WriteAsync(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, cancellationToken);
        }

        if (includeVersion)
        {
            var version = archive.CreateEntry("version.json");
            await using var output = version.Open();
            await output.WriteAsync(
                Encoding.UTF8.GetBytes("{\"id\":\"1.20.1\"}"),
                cancellationToken);
        }
    }

    private static async Task WriteJarWithEntriesAsync(
        string path,
        IReadOnlyList<string> classEntries,
        string mainClass,
        CancellationToken cancellationToken,
        bool includeVersion)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
        await using (var output = manifest.Open())
        {
            await output.WriteAsync(
                Encoding.UTF8.GetBytes(
                    $"Manifest-Version: 1.0\r\nMain-Class: {mainClass}\r\n\r\n"),
                cancellationToken);
        }

        foreach (var classEntry in classEntries)
        {
            var marker = archive.CreateEntry(classEntry);
            await using var output = marker.Open();
            await output.WriteAsync(
                new byte[] { 0xCA, 0xFE, 0xBA, 0xBE },
                cancellationToken);
        }

        if (includeVersion)
        {
            var version = archive.CreateEntry("version.json");
            await using var output = version.Open();
            await output.WriteAsync(
                Encoding.UTF8.GetBytes("{\"id\":\"1.20.1\"}"),
                cancellationToken);
        }
    }

    private static byte[] CreateModernSpigotBootstrapJarBytes(
        string minecraftVersion,
        byte payloadByte = 0x04)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(
                "META-INF/MANIFEST.MF",
                Encoding.UTF8.GetBytes(
                    "Manifest-Version: 1.0\r\n"
                    + "Main-Class: org.bukkit.craftbukkit.bootstrap.Main\r\n\r\n"));
            AddEntry("org/bukkit/craftbukkit/bootstrap/Main.class", [0xCA, 0xFE, 0xBA, 0xBE]);
            AddEntry(
                $"META-INF/versions/spigot-{minecraftVersion}-R0.1-SNAPSHOT.jar",
                [0x50, 0x4B, 0x03, payloadByte]);

            void AddEntry(string name, byte[] content)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] CreateLegacySpigotJarBytes()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(
                "META-INF/MANIFEST.MF",
                Encoding.UTF8.GetBytes(
                    "Manifest-Version: 1.0\r\n"
                    + "Main-Class: org.bukkit.craftbukkit.Main\r\n\r\n"));
            AddEntry("org/bukkit/craftbukkit/Main.class", [0xCA, 0xFE, 0xBA, 0xBE]);
            AddEntry("org/spigotmc/SpigotConfig.class", [0xCA, 0xFE, 0xBA, 0xBE]);
            AddEntry(
                "META-INF/maven/org.spigotmc/spigot/pom.properties",
                Encoding.UTF8.GetBytes("version=1.8.8-R0.1-SNAPSHOT\n"));

            void AddEntry(string name, byte[] content)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static OfficialServerCoreBuildInfo CreateVanilla125Build(
        Uri source,
        long size,
        string sha1)
        => new(
            CoreType.Vanilla,
            "Minecraft 原版",
            "1.2.5",
            "1.2.5",
            LoaderVersion: null,
            "1.2.5",
            OfficialServerInstallStrategy.DirectServerJar,
            IsStable: true,
            source,
            "server.jar",
            size,
            "SHA-1",
            sha1);

    private static byte[] CreateJarBytes(
        string classEntry,
        string mainClass,
        bool includeVersion)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
            using (var output = manifest.Open())
            {
                output.Write(Encoding.UTF8.GetBytes(
                    $"Manifest-Version: 1.0\r\nMain-Class: {mainClass}\r\n\r\n"));
            }

            var marker = archive.CreateEntry(classEntry);
            using (var output = marker.Open())
            {
                output.Write([0xCA, 0xFE, 0xBA, 0xBE]);
            }

            if (includeVersion)
            {
                var version = archive.CreateEntry("version.json");
                using var output = version.Open();
                output.Write(Encoding.UTF8.GetBytes("{\"id\":\"1.20.1\"}"));
            }
        }

        return stream.ToArray();
    }

    private static HttpResponseMessage CreateBytesResponse(byte[] bytes)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };

    private static void AssertNoTemporaryTrees(string serversRoot)
        => Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(serversRoot),
            path => Path.GetFileName(path).StartsWith(
                ".core-installing-",
                StringComparison.Ordinal));

    private sealed record WorkflowFixture(
        ApplicationPaths Paths,
        CoreServerCreationWorkflow Workflow);

    private sealed class FakeBackend(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        Func<string, string, CancellationToken, Task<CoreServerBackendInstallResult>> install,
        bool acceptsArgumentFiles,
        string? loaderVersion,
        bool requireMinecraftEvidence,
        object? sourcePlan,
        CoreServerInstallKind installKind) : ICoreServerCreationBackend
    {
        private readonly object _sourcePlan = sourcePlan ?? new object();

        public Task<IReadOnlyList<CoreServerBackendProduct>> GetProductsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CoreServerBackendProduct>>([product]);
        }

        public Task<IReadOnlyList<CoreServerBackendVersion>> GetVersionsAsync(
            CoreServerBackendProduct requestedProduct,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(product, requestedProduct);
            return Task.FromResult<IReadOnlyList<CoreServerBackendVersion>>([version]);
        }

        public Task<CoreServerInstallPlan> ResolveExactAsync(
            CoreServerBackendProduct requestedProduct,
            CoreServerBackendVersion requestedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(product, requestedProduct);
            Assert.Equal(version, requestedVersion);
            return Task.FromResult(new CoreServerInstallPlan(
                product,
                version,
                product.ExpectedCoreType,
                version.Version.MinecraftVersion,
                version.JavaMajorVersion,
                acceptsArgumentFiles
                    ? CoreServerInstallKind.OfficialLoaderInstaller
                    : installKind,
                RequiresJdk: installKind == CoreServerInstallKind.SpigotBuildTools,
                acceptsArgumentFiles,
                requireMinecraftEvidence,
                loaderVersion,
                _sourcePlan));
        }

        public Task<CoreServerBackendInstallResult> InstallAsync(
            CoreServerInstallPlan plan,
            string stagingDirectory,
            string javaExecutablePath,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
        {
            Assert.Same(_sourcePlan, plan.SourcePlan);
            return install(stagingDirectory, javaExecutablePath, cancellationToken);
        }
    }

    private sealed class FakeJavaResolver : IModrinthJavaRuntimeResolver
    {
        private readonly int _expectedMajorVersion;

        public FakeJavaResolver(string root, int expectedMajorVersion = 17)
        {
            _expectedMajorVersion = expectedMajorVersion;
            JavaPath = Path.Combine(root, "runtimes", "Java 中文 17", "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(JavaPath)!);
            File.WriteAllBytes(JavaPath, [0x4D, 0x5A]);
        }

        public string JavaPath { get; }

        public Task<string> ResolveAsync(
            int majorVersion,
            IProgress<double>? downloadProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_expectedMajorVersion, majorVersion);
            downloadProgress?.Report(1d);
            return Task.FromResult(JavaPath);
        }
    }

    private sealed class FakeJdkResolver : ICoreServerJdkResolver
    {
        private readonly int _expectedMajorVersion;

        public FakeJdkResolver(string root, int expectedMajorVersion)
        {
            _expectedMajorVersion = expectedMajorVersion;
            var bin = Path.Combine(root, "runtimes", $"JDK 中文 {expectedMajorVersion}", "bin");
            Directory.CreateDirectory(bin);
            JavaPath = Path.Combine(bin, "java.exe");
            JavacPath = Path.Combine(bin, "javac.exe");
            File.WriteAllBytes(JavaPath, [0x4D, 0x5A]);
            File.WriteAllBytes(JavacPath, [0x4D, 0x5A]);
        }

        public string JavaPath { get; }

        public string JavacPath { get; }

        public Task<CoreServerJavaDevelopmentKit> ResolveAsync(
            int majorVersion,
            IProgress<double>? downloadProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_expectedMajorVersion, majorVersion);
            downloadProgress?.Report(1d);
            return Task.FromResult(new CoreServerJavaDevelopmentKit(JavaPath, JavacPath));
        }
    }

    private sealed class UnexpectedJavaResolver : IModrinthJavaRuntimeResolver
    {
        public Task<string> ResolveAsync(
            int majorVersion,
            IProgress<double>? downloadProgress,
            CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException(
                "BuildTools plan 不得選取一般 JRE resolver。");
    }

    private sealed class FakeSpigotBuildToolsSource : ISpigotBuildToolsSource
    {
        private readonly SpigotBuildToolsVersionInfo _version;
        private readonly SpigotBuildPlan _plan;

        public FakeSpigotBuildToolsSource(
            CoreType coreType,
            string minecraftVersion,
            int javaMajor,
            SpigotBuildOutputVerificationKind verificationKind =
                SpigotBuildOutputVerificationKind.OfficialOutputSha256)
        {
            _version = new SpigotBuildToolsVersionInfo(
                minecraftVersion,
                javaMajor,
                IsSupported: true,
                UnsupportedReason: null,
                verificationKind);
            var outputSha256 = Convert.ToHexString(
                SHA256.HashData(CreateModernSpigotBootstrapJarBytes(minecraftVersion)))
                .ToLowerInvariant();
            _plan = new SpigotBuildPlan(
                coreType,
                coreType == CoreType.Spigot ? "Spigot" : "CraftBukkit (Bukkit)",
                minecraftVersion,
                javaMajor,
                "server.jar",
                verificationKind == SpigotBuildOutputVerificationKind.OfficialOutputSha256
                    ? outputSha256
                    : null,
                197,
                minecraftVersion,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildData"] = new string('a', 40),
                    ["Bukkit"] = new string('b', 40),
                    ["CraftBukkit"] = new string('c', 40),
                    ["Spigot"] = new string('d', 40)
                },
                SpigotBuildToolsProvider.ReviewedBuildTools,
                verificationKind,
                minecraftVersion);
        }

        public Task<IReadOnlyList<SpigotBuildToolsVersionInfo>> GetVersionsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<SpigotBuildToolsVersionInfo>>([_version]);
        }

        public Task<SpigotBuildPlanResolution> ResolvePlanAsync(
            CoreType coreType,
            string minecraftVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_plan.CoreType, coreType);
            Assert.Equal(_plan.MinecraftVersion, minecraftVersion);
            return Task.FromResult(new SpigotBuildPlanResolution(_plan, null));
        }

        public async Task<string> DownloadReviewedBuildToolsAsync(
            string destinationPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, [0x4D, 0x5A], cancellationToken);
            progress?.Report(1d);
            return destinationPath;
        }
    }

    private sealed class RecordingSpigotBuildToolsInstaller : ISpigotBuildToolsInstaller
    {
        public string JavaExecutablePath { get; private set; } = string.Empty;

        public string BuildToolsJarPath { get; private set; } = string.Empty;

        public string StagingRoot { get; private set; } = string.Empty;

        public string DestinationPath { get; private set; } = string.Empty;

        public async Task<SpigotBuildToolsBuildResult> BuildAsync(
            SpigotBuildPlan plan,
            string javaExecutablePath,
            string buildToolsJarPath,
            string stagingRoot,
            string destinationPath,
            IProgress<ModrinthLoaderBootstrapOutputLine>? output,
            CancellationToken cancellationToken)
        {
            JavaExecutablePath = javaExecutablePath;
            BuildToolsJarPath = buildToolsJarPath;
            StagingRoot = stagingRoot;
            DestinationPath = destinationPath;
            Assert.True(File.Exists(buildToolsJarPath));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(javaExecutablePath)!,
                "javac.exe")));
            Assert.Equal(
                Path.GetFullPath(stagingRoot),
                Path.GetFullPath(Path.GetDirectoryName(destinationPath)!));
            output?.Report(new(false, "Starting clone of Bukkit"));
            await File.WriteAllBytesAsync(
                destinationPath,
                CreateModernSpigotBootstrapJarBytes(plan.MinecraftVersion),
                cancellationToken);
            var outputSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(destinationPath, cancellationToken)))
                .ToLowerInvariant();
            return new SpigotBuildToolsBuildResult(
                plan,
                destinationPath,
                [],
                [],
                false,
                outputSha256);
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
