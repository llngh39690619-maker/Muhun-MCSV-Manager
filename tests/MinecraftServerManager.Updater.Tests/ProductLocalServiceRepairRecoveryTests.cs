using System.Text.Json;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductLocalServiceRepairRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-local-repair-recovery-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("1.0.8", ProductUpdateActivationState.Activating, false)]
    [InlineData("1.2.9-beta.3", ProductUpdateActivationState.HealthChecking, true)]
    public async Task NonTerminalActivation_IsRecoveredBeforeSameVersionAndDowngradeChecks(
        string initialServiceVersion,
        ProductUpdateActivationState journalState,
        bool cleanupSucceeds)
    {
        const string previousVersion = "1.0.8";
        const string targetVersion = "1.2.9-beta.3";
        var installRoot = Path.Combine(_root, "install");
        var releaseRoot = Path.Combine(_root, "release");
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(releaseRoot);
        File.WriteAllText(Path.Combine(installRoot, ".muhun-mcsv-install-root"), "muhun.mcsv.manager:1\n");
        var previousGui = CreateInstalledVersion(installRoot, previousVersion, "previous");
        var targetGui = CreateInstalledVersion(installRoot, targetVersion, "target");
        File.WriteAllText(Path.Combine(installRoot, "active-version.v1"), targetVersion + "\n");
        WriteJournal(installRoot, previousVersion, targetVersion, journalState);

        var targetBytes = File.ReadAllBytes(targetGui);
        var manifest = new ProductUpdateManifest(
            1,
            "muhun.mcsv.manager",
            targetVersion,
            "beta",
            "win-x64",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "muhun.release.test",
            "rsa-pss-sha256",
            new ProductUpdatePackage(
                "https://updates.example.com/mcsv/test.zip",
                1,
                new string('a', 64)),
            ProductFormalUpdateManifestValidator.GuiEntryPoint,
            [
                new ProductUpdateFile(
                    ProductFormalUpdateManifestValidator.GuiEntryPoint,
                    targetBytes.LongLength,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(targetBytes))),
            ]);
        var verified = new VerifiedProductLocalRelease(
            releaseRoot,
            manifest,
            new ProductFormalActivationLayout(
                releaseRoot,
                targetVersion,
                targetGui,
                targetGui,
                targetGui));

        var serviceVersion = initialServiceVersion;
        var launchedVersions = new List<string>();
        var cleanupScheduled = false;
        var failures = new List<Exception>();
        var health = new RecordingHealthController(version =>
        {
            launchedVersions.Add(version);
            serviceVersion = version;
        });
        ProductManagedInstallation Resolve(string _)
        {
            var active = File.ReadAllText(Path.Combine(installRoot, "active-version.v1")).Trim();
            return new ProductManagedInstallation(
                installRoot,
                dataRoot,
                active,
                serviceVersion,
                Path.Combine(
                    installRoot,
                    "versions",
                    serviceVersion,
                    "service-win-x64",
                    "Muhun MCSV Service.exe"));
        }

        var exitCode = await ProductLocalServiceRepairApplication.RunAsync(
            ["--repair-product-service", "--release-root", releaseRoot],
            administratorProbe: () => true,
            installationResolver: Resolve,
            healthControllerFactory: _ => health,
            trustPolicy: new ProductLocalRepairTrustPolicy(
                new string('a', 64),
                new string('b', 64),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "updates.example.com" }),
            requireRunningFromReleaseUpdater: false,
            failureObserver: failures.Add,
            verifiedReleaseFactory: (_, _, _, _) => Task.FromResult(verified),
            stagingResolver: (release, install) => new ProductRepairStagingIdentity(
                install,
                Path.Combine(install, "launcher"),
                release,
                targetVersion,
                new string('a', 32)),
            stagingContentValidator: _ => { },
            stagingCleanupScheduler: _ =>
            {
                cleanupScheduled = true;
                return cleanupSucceeds;
            },
            stagedReleaseBindingValidator: (_, _, _) => { });

        Assert.Equal(0, exitCode);
        Assert.Equal([previousVersion, targetVersion], launchedVersions);
        Assert.Equal(targetVersion, serviceVersion);
        Assert.Equal(
            targetVersion,
            File.ReadAllText(Path.Combine(installRoot, "active-version.v1")).Trim());
        var journal = ProductUpdateActivator.ReadActivationJournal(installRoot);
        Assert.NotNull(journal);
        Assert.Equal(ProductUpdateActivationState.Committed, journal.State);
        Assert.True(cleanupScheduled);
        Assert.Equal(cleanupSucceeds ? 0 : 1, failures.Count);
        if (!cleanupSucceeds)
        {
            Assert.IsType<IOException>(failures[0]);
        }
    }

    private static string CreateInstalledVersion(string installRoot, string version, string content)
    {
        var versionRoot = Path.Combine(installRoot, "versions", version);
        var gui = Path.Combine(versionRoot, "gui-win-x64", "Muhun MCSV Manager.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(gui)!);
        File.WriteAllText(gui, content);
        ProductInstalledVersionMetadataStore.Write(
            versionRoot,
            new ProductInstalledVersionMetadata(
                1,
                "muhun.mcsv.manager",
                version,
                ProductFormalUpdateManifestValidator.GuiEntryPoint));
        return gui;
    }

    private static void WriteJournal(
        string installRoot,
        string previousVersion,
        string targetVersion,
        ProductUpdateActivationState state)
    {
        var activationRoot = Path.Combine(installRoot, ProductUpdateActivator.ActivationStateDirectoryName);
        Directory.CreateDirectory(activationRoot);
        var journal = new ProductUpdateActivationJournal(
            1,
            Guid.NewGuid(),
            previousVersion,
            targetVersion,
            state,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(activationRoot, "activation-journal.v1.json"),
            JsonSerializer.Serialize(
                journal,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingHealthController(Action<string> launched) : IProductUpdateHealthController
    {
        public Task LaunchAsync(string executablePath, CancellationToken cancellationToken)
        {
            var versionRoot = Directory.GetParent(Directory.GetParent(executablePath)!.FullName)!.FullName;
            launched(Path.GetFileName(versionRoot));
            return Task.CompletedTask;
        }

        public Task<bool> WaitForHealthyAsync(
            string version,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
