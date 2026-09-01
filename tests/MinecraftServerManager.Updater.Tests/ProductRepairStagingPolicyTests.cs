using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductRepairStagingPolicyTests : IDisposable
{
    private const string Version = "1.2.9-beta.4";
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-repair-staging-policy-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveBoundary_AcceptsOnlyExactManagedLauncherChild()
    {
        var (installRoot, launcherRoot, stagingRoot) = CreateBoundary();

        var identity = ProductRepairStagingPolicy.ResolveBoundary(stagingRoot, installRoot);

        Assert.Equal(Path.GetFullPath(installRoot), identity.InstallRoot);
        Assert.Equal(Path.GetFullPath(launcherRoot), identity.LauncherRoot);
        Assert.Equal(Path.GetFullPath(stagingRoot), identity.StagingRoot);
        Assert.Equal(Version, identity.Version);
        Assert.Equal(Nonce, identity.Nonce);
    }

    [Fact]
    public void ResolveBoundary_RejectsInstallRootVersionsAndNestedLocations()
    {
        var (installRoot, launcherRoot, _) = CreateBoundary();
        var directInstallChild = Path.Combine(
            installRoot,
            $".repair-staging-{Version}-{Nonce}");
        var versionsChild = Path.Combine(
            installRoot,
            "versions",
            $".repair-staging-{Version}-{Nonce}");
        var nested = Path.Combine(
            launcherRoot,
            "nested",
            $".repair-staging-{Version}-{Nonce}");
        Directory.CreateDirectory(directInstallChild);
        Directory.CreateDirectory(versionsChild);
        Directory.CreateDirectory(nested);

        Assert.Throws<InvalidDataException>(() =>
            ProductRepairStagingPolicy.ResolveBoundary(directInstallChild, installRoot));
        Assert.Throws<InvalidDataException>(() =>
            ProductRepairStagingPolicy.ResolveBoundary(versionsChild, installRoot));
        Assert.Throws<InvalidDataException>(() =>
            ProductRepairStagingPolicy.ResolveBoundary(nested, installRoot));
    }

    [Theory]
    [InlineData(".repair-staging-1.2.9-beta.4-abc")]
    [InlineData("repair-staging-1.2.9-beta.4-0123456789abcdef0123456789abcdef")]
    [InlineData(".repair-staging-01.2.9-0123456789abcdef0123456789abcdef")]
    [InlineData(".repair-staging-1.2.9-beta.4-0123456789abcdef0123456789abcdeg")]
    public void ResolveBoundary_RejectsMalformedStagingName(string name)
    {
        var (installRoot, launcherRoot, _) = CreateBoundary();
        var candidate = Path.Combine(launcherRoot, name);
        Directory.CreateDirectory(candidate);

        Assert.ThrowsAny<Exception>(() =>
            ProductRepairStagingPolicy.ResolveBoundary(candidate, installRoot));
    }

    [Fact]
    public void SecurityDescriptor_AllowsObservedInstalledRootAclAndIgnoresInheritOnlyCreatorOwner()
    {
        var security = new DirectorySecurity();
        // Representative copy of the ACL observed on an installed Program Files product root.
        // Machine-specific user/group SIDs are replaced while preserving every ACE/flag/right.
        security.SetSecurityDescriptorSddlForm(
            "O:BAG:S-1-5-21-1-2-3-1001D:AI" +
            "(A;OICI;0x1200a9;;;S-1-5-21-1-2-3-1001)" +
            "(A;;0x1200a9;;;S-1-5-80-845188951-1487190974-227400769-3990947874-1136434444)" +
            "(A;ID;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)" +
            "(A;CIIOID;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)" +
            "(A;ID;FA;;;SY)" +
            "(A;OICIIOID;GA;;;SY)" +
            "(A;ID;FA;;;BA)" +
            "(A;OICIIOID;GA;;;BA)" +
            "(A;ID;0x1200a9;;;BU)" +
            "(A;OICIIOID;GXGR;;;BU)" +
            "(A;OICIIOID;GA;;;CO)" +
            "(A;ID;0x1200a9;;;AC)" +
            "(A;OICIIOID;GXGR;;;AC)" +
            "(A;ID;0x1200a9;;;S-1-15-2-2)" +
            "(A;OICIIOID;GXGR;;;S-1-15-2-2)");

        ProductRepairStagingPolicy.ValidateSecurityDescriptor(security);
    }

    [Fact]
    public void SecurityDescriptor_RejectsOrdinaryUserWriteAndUntrustedOwner()
    {
        var weakAcl = new DirectorySecurity();
        weakAcl.SetSecurityDescriptorSddlForm(
            "O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GA;;;BU)");
        var weakOwner = new DirectorySecurity();
        weakOwner.SetSecurityDescriptorSddlForm(
            "O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        weakOwner.SetOwner(new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001"));

        Assert.Throws<UnauthorizedAccessException>(() =>
            ProductRepairStagingPolicy.ValidateSecurityDescriptor(weakAcl));
        Assert.Throws<UnauthorizedAccessException>(() =>
            ProductRepairStagingPolicy.ValidateSecurityDescriptor(weakOwner));
    }

    [Fact]
    public void ValidateProtectedTree_RejectsFileWithMoreThanOneHardLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (installRoot, _, stagingRoot) = CreateBoundary();
        var payload = Path.Combine(stagingRoot, "payload.bin");
        var alias = Path.Combine(stagingRoot, "payload-alias.bin");
        File.WriteAllText(payload, "signed bytes");
        Assert.True(CreateHardLinkW(alias, payload, IntPtr.Zero));
        var identity = ProductRepairStagingPolicy.ResolveBoundary(stagingRoot, installRoot);

        Assert.Equal(2u, ProductRepairStagingPolicy.ReadHardLinkCount(payload));
        Assert.Throws<InvalidDataException>(() =>
            ProductRepairStagingPolicy.ValidateProtectedTree(
                identity,
                securityValidator: (_, _) => { }));
    }

    [Fact]
    public void ValidateVerifiedRelease_BindsFolderVersionAndSignedUpdater()
    {
        var (installRoot, _, stagingRoot) = CreateBoundary();
        var identity = ProductRepairStagingPolicy.ResolveBoundary(stagingRoot, installRoot);
        var updater = Path.Combine(
            stagingRoot,
            ProductFormalUpdateManifestValidator.UpdaterEntryPoint.Replace(
                '/',
                Path.DirectorySeparatorChar));
        var manifest = CreateManifest(Version, includeUpdater: true);
        var release = new VerifiedProductLocalRelease(
            stagingRoot,
            manifest,
            new ProductFormalActivationLayout(stagingRoot, Version, updater, updater, updater));

        ProductRepairStagingPolicy.ValidateVerifiedRelease(
            identity,
            release,
            requireRunningFromReleaseUpdater: false);

        var wrongVersion = release with { UpdateManifest = CreateManifest("1.2.9-beta.2", true) };
        Assert.Throws<InvalidDataException>(() =>
            ProductRepairStagingPolicy.ValidateVerifiedRelease(
                identity,
                wrongVersion,
                requireRunningFromReleaseUpdater: false));
        var missingUpdater = release with { UpdateManifest = CreateManifest(Version, false) };
        Assert.Throws<InvalidDataException>(() =>
            ProductRepairStagingPolicy.ValidateVerifiedRelease(
                identity,
                missingUpdater,
                requireRunningFromReleaseUpdater: false));
    }

    [Fact]
    public void Cleanup_SchedulesLeavesThenDirectoriesAndNeverInstallOrVersionsRoot()
    {
        var (installRoot, _, stagingRoot) = CreateBoundary();
        var nested = Path.Combine(stagingRoot, "a", "b");
        Directory.CreateDirectory(nested);
        var payload = Path.Combine(nested, "payload.bin");
        File.WriteAllText(payload, "payload");
        var identity = ProductRepairStagingPolicy.ResolveBoundary(stagingRoot, installRoot);
        var scheduled = new List<string>();

        var result = ProductRepairStagingCleanup.Schedule(
            identity,
            path =>
            {
                scheduled.Add(Path.GetFullPath(path));
                return true;
            },
            securityValidator: (_, _) => { });

        Assert.True(result);
        Assert.Equal(Path.GetFullPath(payload), scheduled[0]);
        Assert.Equal(Path.GetFullPath(stagingRoot), scheduled[^1]);
        Assert.DoesNotContain(Path.GetFullPath(installRoot), scheduled);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(installRoot, "versions")), scheduled);
        Assert.True(scheduled.IndexOf(Path.GetFullPath(nested)) < scheduled.IndexOf(Path.GetFullPath(stagingRoot)));
    }

    [Fact]
    public void Cleanup_RejectsForgedBroadIdentityBeforeSchedulingAnything()
    {
        var (installRoot, launcherRoot, _) = CreateBoundary();
        var scheduled = new List<string>();
        var forged = new ProductRepairStagingIdentity(
            installRoot,
            launcherRoot,
            installRoot,
            Version,
            Nonce);

        Assert.Throws<InvalidDataException>(() =>
            ProductRepairStagingCleanup.Schedule(forged, path =>
            {
                scheduled.Add(path);
                return true;
            }));
        Assert.Empty(scheduled);
    }

    [Fact]
    public void Cleanup_WeakAclTreeIsNotTraversedAndCannotReportCompleteCleanup()
    {
        var (installRoot, _, stagingRoot) = CreateBoundary();
        var nested = Path.Combine(stagingRoot, "untrusted", "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "outside-looking-name.bin"), "payload");
        var identity = ProductRepairStagingPolicy.ResolveBoundary(stagingRoot, installRoot);
        var scheduled = new List<string>();

        var result = ProductRepairStagingCleanup.Schedule(
            identity,
            path =>
            {
                scheduled.Add(Path.GetFullPath(path));
                return true;
            },
            securityValidator: (_, _) => throw new UnauthorizedAccessException("weak ACL"));

        Assert.False(result);
        Assert.Equal([Path.GetFullPath(stagingRoot)], scheduled);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup on Windows CI.
        }
    }

    private (string InstallRoot, string LauncherRoot, string StagingRoot) CreateBoundary()
    {
        var installRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var launcherRoot = Path.Combine(installRoot, "launcher");
        var stagingRoot = Path.Combine(
            launcherRoot,
            $".repair-staging-{Version}-{Nonce}");
        Directory.CreateDirectory(stagingRoot);
        File.WriteAllText(
            Path.Combine(installRoot, ".muhun-mcsv-install-root"),
            "muhun.mcsv.manager:1\n");
        return (installRoot, launcherRoot, stagingRoot);
    }

    private static ProductUpdateManifest CreateManifest(string version, bool includeUpdater)
    {
        var files = includeUpdater
            ? new[]
            {
                new ProductUpdateFile(
                    ProductFormalUpdateManifestValidator.UpdaterEntryPoint,
                    1,
                    new string('a', 64)),
            }
            : new[]
            {
                new ProductUpdateFile(
                    ProductFormalUpdateManifestValidator.GuiEntryPoint,
                    1,
                    new string('b', 64)),
            };
        return new ProductUpdateManifest(
            1,
            "muhun.mcsv.manager",
            version,
            "beta",
            "win-x64",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "muhun.release.test",
            "rsa-pss-sha256",
            new ProductUpdatePackage(
                "https://updates.example.com/mcsv/test.zip",
                1,
                new string('c', 64)),
            ProductFormalUpdateManifestValidator.GuiEntryPoint,
            files);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
