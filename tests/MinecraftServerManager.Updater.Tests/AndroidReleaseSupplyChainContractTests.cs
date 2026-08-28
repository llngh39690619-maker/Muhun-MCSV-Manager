using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater.Tests;

public sealed class AndroidReleaseSupplyChainContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void AndroidMetadataAndSignedManifestBindPositiveImmutableVersionCode()
    {
        var build = ReadScript("Build-MuhunMcsvAndroid.ps1");
        var formal = ReadScript("Build-MuhunMcsvFormalRelease.ps1");
        var release = ReadScript("New-MuhunMcsvRelease.ps1");
        var verifier = ReadScript("Test-MuhunMcsvRelease.ps1");

        foreach (var source in new[] { build, formal, release, verifier })
        {
            Assert.Contains("android-release.v3.json", source, StringComparison.Ordinal);
            Assert.DoesNotContain("android-release.v2.json", source, StringComparison.Ordinal);
            Assert.Contains("versionCode", source, StringComparison.Ordinal);
            Assert.Contains("1.1.0", source, StringComparison.Ordinal);
            Assert.Contains("10", source, StringComparison.Ordinal);
        }

        Assert.Contains("schemaVersion = 3", build, StringComparison.Ordinal);
        Assert.Contains("versionCode = $VersionCode", build, StringComparison.Ordinal);
        Assert.Contains("versionCode='$VersionCode'", build, StringComparison.Ordinal);
        Assert.Contains("VersionCode must be between 1 and 999999999", build, StringComparison.Ordinal);

        Assert.Contains("[ValidateRange(1, 999999999)]", formal, StringComparison.Ordinal);
        Assert.Contains("-AndroidVersionCode $AndroidVersionCode", formal, StringComparison.Ordinal);
        Assert.Contains("-not ($metadata.versionCode -is [long])", formal, StringComparison.Ordinal);

        Assert.Contains("[ValidateRange(1, 999999999)]", release, StringComparison.Ordinal);
        Assert.Contains("versionCode = [int]$AndroidVersionCode", release, StringComparison.Ordinal);
        Assert.Contains("metadataSizeBytes", release, StringComparison.Ordinal);
        Assert.Contains("metadataSha256", release, StringComparison.Ordinal);

        Assert.Contains("Signed Android release manifest entry", verifier, StringComparison.Ordinal);
        Assert.Contains("-not ($android.versionCode -is [long])", verifier, StringComparison.Ordinal);
        Assert.Contains("$android.versionCode -ne 10", verifier, StringComparison.Ordinal);
        Assert.Contains("versionCode='$($android.versionCode)'", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidToolsArePinnedAndReceiptIsVerifiedAcrossTheReleasePipeline()
    {
        var scripts = new[]
        {
            ReadScript("Build-MuhunMcsvAndroid.ps1"),
            ReadScript("Build-MuhunMcsvFormalRelease.ps1"),
            ReadScript("New-MuhunMcsvRelease.ps1"),
            ReadScript("Test-MuhunMcsvRelease.ps1")
        };
        var pinnedHashes = new[]
        {
            "babf3122e515ddb954c5ac4669e085ce990536c035e3072de30127bddd6e3608",
            "549dd0028b0314a5112d6b56e2de7800e713f297da4508b513a735546e52ce38",
            "3716d9311e55d2b0918a2fd9d54ba9e406c5f6abeea700b287f11259bc163dec"
        };

        foreach (var source in scripts)
        {
            Assert.Contains("android-toolchain.v1.json", source, StringComparison.Ordinal);
            Assert.Contains("36.0.0", source, StringComparison.Ordinal);
            Assert.Contains("lib/apksigner.jar", source, StringComparison.Ordinal);
            Assert.Contains("Assert-NoReparseAncestors", source, StringComparison.Ordinal);
            foreach (var hash in pinnedHashes)
            {
                Assert.Contains(hash, source, StringComparison.Ordinal);
            }
        }

        Assert.Contains("toolchainReceiptFileName", scripts[0], StringComparison.Ordinal);
        Assert.Contains("Assert-AndroidStagingContract", scripts[1], StringComparison.Ordinal);
        Assert.Contains("Assert-AndroidToolchainReceipt", scripts[2], StringComparison.Ordinal);
        Assert.Contains("Assert-AndroidToolchainReceipt", scripts[3], StringComparison.Ordinal);
        Assert.Contains("toolchainReceiptPath", scripts[2], StringComparison.Ordinal);
        Assert.Contains("toolchainReceiptSha256", scripts[2], StringComparison.Ordinal);
    }

    [Fact]
    public void SigningSecretsRequireFailClosedOwnerAndAclValidation()
    {
        var source = ReadScript("New-MuhunMcsvRelease.ps1");
        foreach (var fileName in new[]
        {
            "muhun-mcsv-release-signing.pfx",
            "pfx-password.dpapi",
            "provider-signing-private-key.pem",
            "provider-key-password.dpapi"
        })
        {
            Assert.Contains(fileName, source, StringComparison.Ordinal);
        }

        Assert.Contains("function Get-SidValueFailClosed", source, StringComparison.Ordinal);
        Assert.Contains("contains an identity that cannot be translated to a SID", source, StringComparison.Ordinal);
        Assert.Contains("GetOwner([Security.Principal.SecurityIdentifier])", source, StringComparison.Ordinal);
        Assert.Contains("AreAccessRulesProtected", source, StringComparison.Ordinal);
        Assert.Contains("unauthorized or broad principal", source, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparseAncestors -Path $normalizedPath", source, StringComparison.Ordinal);

        var sidFunction = Regex.Match(
            source,
            @"(?ms)^function Get-SidValueFailClosed \{.*?^\}",
            RegexOptions.CultureInvariant);
        Assert.True(sidFunction.Success, "Could not locate fail-closed SID translation function.");
        Assert.Contains("catch", sidFunction.Value, StringComparison.Ordinal);
        Assert.Contains("throw", sidFunction.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("continue", sidFunction.Value, StringComparison.Ordinal);
    }

    private static string ReadScript(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", name));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MinecraftServerManager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
