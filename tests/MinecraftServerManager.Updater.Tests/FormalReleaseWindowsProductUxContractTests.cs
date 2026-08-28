using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater.Tests;

public sealed class FormalReleaseWindowsProductUxContractTests
{
    private const string PublisherSha256 =
        "1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void FirstRunGuideBootstrapsTrustInPinnedFailClosedOrder()
    {
        var release = ReadScript("New-MuhunMcsvRelease.ps1");
        var verifier = ReadScript("Test-MuhunMcsvRelease.ps1");
        var start = release.IndexOf("$gettingStartedLines = @(", StringComparison.Ordinal);
        var end = release.IndexOf("$authenticodeFiles =", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var guide = release[start..end];

        Assert.Contains(PublisherSha256, guide, StringComparison.Ordinal);
        Assert.Contains("GitHub Release 的獨立正式公告核對", guide, StringComparison.Ordinal);
        Assert.Contains("Read-Host", guide, StringComparison.Ordinal);
        Assert.Contains("Windows 開始功能表的「X MCSV」", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("-ExecutionPolicy Bypass", guide, StringComparison.OrdinalIgnoreCase);

        AssertOrdered(
            guide,
            "Get-FileHash -LiteralPath $publisherCertificatePath -Algorithm SHA256",
            "Write-Host \"publisher.cer SHA-256:",
            "Read-Host",
            "certutil.exe\" -addstore -f Root",
            "if ($LASTEXITCODE -ne 0)",
            "certutil.exe\" -addstore -f TrustedPublisher",
            "Set-ExecutionPolicy -Scope Process -ExecutionPolicy AllSigned -Force",
            "Get-AuthenticodeSignature",
            "Test-MuhunMcsvRelease.ps1",
            "Install-MuhunMcsv.ps1");

        Assert.Contains(PublisherSha256, verifier, StringComparison.Ordinal);
        Assert.Contains("does not preserve the required trust-bootstrap execution order", verifier, StringComparison.Ordinal);
        Assert.Contains("(?i)-ExecutionPolicy\\s+Bypass", verifier, StringComparison.Ordinal);
        Assert.Contains(
            "$manifest.publisherCertificateSha256 -cne $expectedPublisherCertificateSha256",
            verifier,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SignedUninstallerIsHashBoundIntoBothReleaseAndVersionPackage()
    {
        var release = ReadScript("New-MuhunMcsvRelease.ps1");
        var verifier = ReadScript("Test-MuhunMcsvRelease.ps1");
        var installer = ReadScript("Install-MuhunMcsv.ps1");

        Assert.Contains("$versionToolsRoot = Join-Path $outputRoot 'tools'", release, StringComparison.Ordinal);
        Assert.Contains("tools/Uninstall-MuhunMcsv.ps1", release, StringComparison.Ordinal);
        Assert.True(
            release.IndexOf("$versionUninstallerPath", StringComparison.Ordinal) <
            release.IndexOf("$packageSourceFiles =", StringComparison.Ordinal));

        foreach (var source in new[] { verifier, installer })
        {
            Assert.Contains("tools/Uninstall-MuhunMcsv.ps1", source, StringComparison.Ordinal);
        }
        Assert.Contains("Resolve-ReleaseFile -RelativePath 'Uninstall-MuhunMcsv.ps1'", verifier, StringComparison.Ordinal);
        Assert.Contains("Resolve-ReleaseFile -RelativePath 'tools/Uninstall-MuhunMcsv.ps1'", verifier, StringComparison.Ordinal);
        Assert.Contains("Resolve-SafeSourceFile $Source 'Uninstall-MuhunMcsv.ps1'", installer, StringComparison.Ordinal);
        Assert.Contains("Resolve-SafeSourceFile $Source 'tools/Uninstall-MuhunMcsv.ps1'", installer, StringComparison.Ordinal);
        Assert.Contains("$installedUninstallerRelativePath = 'tools\\Uninstall-MuhunMcsv.ps1'", installer, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $UninstallerPath", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void AppsAndFeaturesRegistrationCommitsAndRollsBackWithInstallation()
    {
        var installer = ReadScript("Install-MuhunMcsv.ps1");
        var uninstaller = ReadScript("Uninstall-MuhunMcsv.ps1");
        var register = ExtractFunction(installer, "Set-ArpRegistrationTransactionally");
        var restore = ExtractFunction(installer, "Restore-ArpRegistrationTransaction");

        Assert.Contains("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\MuhunMCSV", installer, StringComparison.Ordinal);
        Assert.Contains("RegistryView]::Registry64", register, StringComparison.Ordinal);
        Assert.Contains("'DisplayName', 'X MCSV'", register, StringComparison.Ordinal);
        Assert.Contains("'DisplayVersion', $Version", register, StringComparison.Ordinal);
        Assert.Contains("'InstallLocation', $InstallRoot", register, StringComparison.Ordinal);
        Assert.Contains("-NoProfile -ExecutionPolicy AllSigned -File", register, StringComparison.Ordinal);
        Assert.Contains("'ProductId', $productId", register, StringComparison.Ordinal);
        Assert.Contains("'PublisherCertificateSha256'", register, StringComparison.Ordinal);
        Assert.Contains("foreach ($valueName in @($key.GetValueNames()))", restore, StringComparison.Ordinal);
        Assert.Contains("$baseKey.DeleteSubKey($arpRegistrySubKey, $false)", restore, StringComparison.Ordinal);

        AssertOrdered(
            installer,
            "Wait-ProductActivationReady `",
            "Set-ArpRegistrationTransactionally `",
            "Complete-StableLauncherTransaction `",
            "$installationApplied = $true");
        var catchIndex = installer.IndexOf("} catch {\n    $installationFailure", StringComparison.Ordinal);
        var restoreIndex = installer.IndexOf("Restore-ArpRegistrationTransaction", catchIndex, StringComparison.Ordinal);
        var rethrowIndex = installer.IndexOf("throw $installationFailure", catchIndex, StringComparison.Ordinal);
        Assert.True(catchIndex >= 0 && restoreIndex > catchIndex && rethrowIndex > restoreIndex);

        Assert.Contains("Assert-TrustedUninstallerSelf", uninstaller, StringComparison.Ordinal);
        Assert.Contains("Assert-OwnedArpRegistration", uninstaller, StringComparison.Ordinal);
        Assert.Contains("Remove-OwnedArpRegistration -InstallRoot $install", uninstaller, StringComparison.Ordinal);
        Assert.Contains("$baseKey.DeleteSubKey($arpRegistrySubKey, $false)", uninstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteSubKeyTree", uninstaller, StringComparison.Ordinal);
    }

    [Fact]
    public void StartMenuMigratesToXNameOnlyAfterSuccessfulCommit()
    {
        var installer = ReadScript("Install-MuhunMcsv.ps1");
        var uninstaller = ReadScript("Uninstall-MuhunMcsv.ps1");
        Assert.Contains("$startMenuShortcutName = 'X MCSV.lnk'", installer, StringComparison.Ordinal);
        Assert.Contains("$legacyStartMenuShortcutName = 'Muhun MCSV Manager.lnk'", installer, StringComparison.Ordinal);

        var commit = installer.IndexOf("$installationApplied = $true", StringComparison.Ordinal);
        var legacyRemoval = installer.IndexOf(
            "Remove-Item -LiteralPath $legacyStartMenuShortcutPath -Force",
            commit,
            StringComparison.Ordinal);
        Assert.True(commit >= 0 && legacyRemoval > commit);
        var rollback = installer.IndexOf("} catch {\n    $installationFailure", StringComparison.Ordinal);
        var rollbackEnd = installer.IndexOf("throw $installationFailure", rollback, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$legacyStartMenuShortcutPath",
            installer[rollback..rollbackEnd],
            StringComparison.Ordinal);

        Assert.Contains("$startMenuShortcutName = 'X MCSV.lnk'", uninstaller, StringComparison.Ordinal);
        Assert.Contains("$legacyStartMenuShortcutName = 'Muhun MCSV Manager.lnk'", uninstaller, StringComparison.Ordinal);
        Assert.Contains("X MCSV 程式與 Windows Service 已移除", uninstaller, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalBuildUsesOnlyExplicitPhysicalToolingRootWithoutReparseFallback()
    {
        var formal = ReadScript("Build-MuhunMcsvFormalRelease.ps1");
        var physicalRoot = ExtractFunction(formal, "Assert-PhysicalToolingRoot");

        Assert.Contains("[string]$ToolingRoot", formal, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::IsPathFullyQualified($ToolingRoot)", formal, StringComparison.Ordinal);
        Assert.Contains("$dotnet = Join-Path $resolvedToolingRoot 'dotnet10\\dotnet.exe'", formal, StringComparison.Ordinal);
        Assert.Contains(
            "$androidBuildToolsRoot = Join-Path $resolvedToolingRoot 'android-sdk\\build-tools\\36.0.0'",
            formal,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Command dotnet", formal, StringComparison.Ordinal);

        Assert.Contains("Assert-NoReparseAncestors -Path $resolvedToolingRoot", physicalRoot, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparseAncestors -Path $dotnet", physicalRoot, StringComparison.Ordinal);
        Assert.Contains("physical non-reparse directory", physicalRoot, StringComparison.Ordinal);
        Assert.Contains("Android build-tools root escaped the explicit ToolingRoot", physicalRoot, StringComparison.Ordinal);
        Assert.Contains("Assert-PhysicalToolingRoot\nAssert-FormalSourceIdentity", formal, StringComparison.Ordinal);

        foreach (var pinnedHash in new[]
        {
            "babf3122e515ddb954c5ac4669e085ce990536c035e3072de30127bddd6e3608",
            "549dd0028b0314a5112d6b56e2de7800e713f297da4508b513a735546e52ce38",
            "3716d9311e55d2b0918a2fd9d54ba9e406c5f6abeea700b287f11259bc163dec"
        })
        {
            Assert.Contains(pinnedHash, formal, StringComparison.Ordinal);
        }
    }

    private static void AssertOrdered(string source, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Missing or out-of-order contract text: {value}");
            previous = current;
        }
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

    private static string ReadScript(string name) => File.ReadAllText(
        Path.Combine(RepositoryRoot, "scripts", name));

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
