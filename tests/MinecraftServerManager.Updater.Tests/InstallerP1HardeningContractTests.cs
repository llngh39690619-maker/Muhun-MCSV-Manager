using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater.Tests;

public sealed class InstallerP1HardeningContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string InstallerPath = Path.Combine(
        RepositoryRoot,
        "scripts",
        "Install-MuhunMcsv.ps1");

    [Fact]
    public void ElevatedInstallerExecutesVerifierOnlyFromProtectedCopiedStaging()
    {
        var source = File.ReadAllText(InstallerPath);
        var verifierFunction = ExtractFunction(source, "Invoke-TrustedReleaseVerifier");
        var cleanupFunction = ExtractFunction(source, "Remove-TrustedVerifierStaging");

        Assert.DoesNotContain(
            "& (Join-Path $source 'Test-MuhunMcsvRelease.ps1')",
            source,
            StringComparison.Ordinal);
        Assert.Contains(".verification-", source, StringComparison.Ordinal);
        Assert.Contains(
            "Set-AndAssertInstallExecutableTreeAcl",
            source,
            StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Copy($sourceVerifier, $trustedVerifier, $false)", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("Get-SignedReleaseManifestEntry", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("Get-Sha256Hex $trustedVerifier", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $trustedVerifier", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("$signature.TimeStamperCertificate", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("Get-CertificateSha256 $signature.SignerCertificate", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("& $trustedVerifier -ReleaseDirectory $Source", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("finally", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("Remove-TrustedVerifierStaging $StagingRoot $InstallRoot", verifierFunction, StringComparison.Ordinal);
        Assert.Contains("Assert-NoExistingReparsePoints $normalizedStage", cleanupFunction, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Delete($normalizedStage, $true)", cleanupFunction, StringComparison.Ordinal);

        var harden = source.IndexOf(
            "        Set-AndAssertInstallExecutableTreeAcl `",
            StringComparison.Ordinal);
        var invoke = source.IndexOf(
            "        Invoke-TrustedReleaseVerifier `",
            StringComparison.Ordinal);
        var dataMutation = source.IndexOf(
            "        New-Item -ItemType Directory -Path $data -Force",
            StringComparison.Ordinal);
        Assert.True(
            harden >= 0 && invoke > harden && dataMutation > invoke,
            "InstallRoot must be protected and the copied verifier must finish before data/service mutation.");
    }

    [Fact]
    public void InstallExecutableTreeUsesExactProtectedAclAndRecursivelyAttestsItBeforeStart()
    {
        var source = File.ReadAllText(InstallerPath);
        var setter = ExtractFunction(source, "Set-ExactProtectedPathAcl");
        var verifier = ExtractFunction(source, "Assert-ExactProtectedPathAcl");
        var tree = ExtractFunction(source, "Set-AndAssertInstallExecutableTreeAcl");
        var rights = ExtractFunction(source, "Get-InstallTreeServiceRights");

        Assert.Contains("SetAccessRuleProtection($true, $false)", setter, StringComparison.Ordinal);
        Assert.Contains("BuiltinAdministratorsSid", setter, StringComparison.Ordinal);
        Assert.Contains("$security.SetOwner($administratorsSid)", setter, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights]::FullControl", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights]::ReadAndExecute", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights]::Modify", source, StringComparison.Ordinal);

        Assert.Contains("AreAccessRulesProtected", verifier, StringComparison.Ordinal);
        Assert.Contains("GetOwner([Security.Principal.SecurityIdentifier])", verifier, StringComparison.Ordinal);
        Assert.Contains("GetAccessRules(", verifier, StringComparison.Ordinal);
        Assert.Contains("$actualRules.Count -ne $expected.Count", verifier, StringComparison.Ordinal);
        Assert.Contains("$rule.IsInherited", verifier, StringComparison.Ordinal);
        Assert.Contains("-not $expected.ContainsKey($sidValue)", verifier, StringComparison.Ordinal);
        Assert.Contains("$rule.FileSystemRights -ne", verifier, StringComparison.Ordinal);

        Assert.Contains("[Collections.Generic.Stack[string]]::new()", tree, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $currentDirectory -Force", tree, StringComparison.Ordinal);
        Assert.Contains("$normalizedExcludedRoots", tree, StringComparison.Ordinal);
        Assert.Contains("程式安裝可執行樹含有 reparse point", tree, StringComparison.Ordinal);
        Assert.Contains("# Re-enumerate after the writes", tree, StringComparison.Ordinal);
        Assert.Contains("Assert-ExactProtectedPathAcl", tree, StringComparison.Ordinal);
        Assert.Contains("return 'Modify'", rights, StringComparison.Ordinal);
        Assert.Contains("return 'None'", rights, StringComparison.Ordinal);
        Assert.Contains("return 'ReadAndExecute'", rights, StringComparison.Ordinal);

        Assert.DoesNotContain("AuthenticatedUserSid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuiltinUsersSid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Authenticated Users:(", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BUILTIN\\Users:(", source, StringComparison.OrdinalIgnoreCase);

        var activePointerWrite = source.IndexOf(
            "        Write-AtomicText $activePointerPath $manifest.version",
            StringComparison.Ordinal);
        var finalTreeAttestation = source.IndexOf(
            "        # Finalize and recursively attest the executable tree",
            activePointerWrite,
            StringComparison.Ordinal);
        var startService = source.IndexOf(
            "        Start-Service -Name $serviceName",
            finalTreeAttestation,
            StringComparison.Ordinal);
        Assert.True(
            activePointerWrite >= 0 && finalTreeAttestation > activePointerWrite && startService > finalTreeAttestation,
            "The complete tree, including active-version.v1, must be attested before service execution.");
        Assert.True(
            Regex.Matches(source, @"(?m)^\s*Set-AndAssertInstallExecutableTreeAcl\s*`?\s*$").Count >= 3,
            "Initial install, final commit, and upgrade rollback must all converge on the exact ACL policy.");
    }

    [Fact]
    public void CopiedMetadataIsAlsoPinnedToSignedReleaseManifest()
    {
        var source = File.ReadAllText(InstallerPath);
        var metadataCopy = source.IndexOf(
            "$installedMetadataDestination = Join-Path $stagingRoot 'installed-version.v1.json'",
            StringComparison.Ordinal);

        Assert.True(metadataCopy >= 0);
        var nearby = source.Substring(Math.Max(0, metadataCopy - 300), Math.Min(900, source.Length - Math.Max(0, metadataCopy - 300)));
        Assert.Contains("Get-SignedReleaseManifestEntry", nearby, StringComparison.Ordinal);
        Assert.Contains("$installedMetadataEntry.sha256", nearby, StringComparison.Ordinal);
        Assert.Contains("$installedMetadataEntry.sizeBytes", nearby, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string name)
    {
        var match = Regex.Match(
            source,
            $@"(?ms)^function {Regex.Escape(name)} \{{.*?(?=^function |\z)",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not locate PowerShell function {name}.");
        return match.Value;
    }

    private static string FindRepositoryRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "scripts", "Install-MuhunMcsv.ps1")))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
