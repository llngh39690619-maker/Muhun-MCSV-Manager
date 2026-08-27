using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater.Tests;

public sealed class InstallerOwnedParentRollbackTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void InstallerUsesNonceOwnershipAndNonRecursiveImmediateParentCleanup()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "Install-MuhunMcsv.ps1"));
        var cleanup = ExtractFunction(source, "Remove-OwnedEmptyImmediateParentDirectory");

        Assert.Contains("RandomNumberGenerator]::Fill", source, StringComparison.Ordinal);
        Assert.Contains("[IO.FileMode]::CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("Dictionary[string, object]", source, StringComparison.Ordinal);
        Assert.Contains("CreatedByAttempt = $false", source, StringComparison.Ordinal);
        Assert.Contains("MarkerCreated = $false", source, StringComparison.Ordinal);
        Assert.Contains("New-Item -ItemType Directory -Path $exclusiveParentPath -ErrorAction Stop", source, StringComparison.Ordinal);
        Assert.Contains("Undo-PartialImmediateParentOwnership $record", source, StringComparison.Ordinal);
        Assert.Contains("Complete-OwnedImmediateParentDirectories $ownedParentDirectories", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Delete([string]$Record.ParentPath, $false)", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", cleanup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Recurse", cleanup, StringComparison.OrdinalIgnoreCase);

        var lineLeadingMemberCall = Regex.Match(
            source,
            @"(?m)^\s+\.[A-Za-z_][A-Za-z0-9_]*\s*\(",
            RegexOptions.CultureInvariant);
        Assert.False(
            lineLeadingMemberCall.Success,
            $"PowerShell can parse a line-leading member call as a command: " +
            $"{lineLeadingMemberCall.Value}");

        var addInstall = source.IndexOf(
            "Add-OwnedImmediateParentDirectory $install $ownedParentDirectories",
            StringComparison.Ordinal);
        var createInstall = source.IndexOf(
            "New-Item -ItemType Directory -Path $install -Force",
            addInstall,
            StringComparison.Ordinal);
        var completeMarkers = source.IndexOf(
            "Complete-OwnedImmediateParentDirectories $ownedParentDirectories",
            createInstall,
            StringComparison.Ordinal);
        var commit = source.IndexOf(
            "$installationApplied = $true",
            completeMarkers,
            StringComparison.Ordinal);
        Assert.True(
            addInstall >= 0 && createInstall > addInstall && completeMarkers > createInstall && commit > completeMarkers,
            "Parent ownership must be recorded before root creation and removed before install commit.");
    }

    [Fact]
    public void RealInstallerFunctionsDeleteOnlyOwnedEmptyImmediateParents()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "Install-MuhunMcsv.ps1"));
        var root = Path.Combine(Path.GetTempPath(), $"muhun-parent-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var probe = """
                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                $root = $env:MCSV_PARENT_TEST_ROOT

                $parseTokens = $null
                $parseErrors = $null
                $installerAst = [Management.Automation.Language.Parser]::ParseFile(
                    $env:MCSV_INSTALLER_SOURCE,
                    [ref]$parseTokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw "installer parse failed: $($parseErrors -join '; ')"
                }

                $requiredFunctionNames = @(
                    'Assert-NoExistingReparsePoints',
                    'New-ParentOwnershipNonce',
                    'Assert-ImmediateParentOwnershipBoundary',
                    'Test-OwnedParentMarker',
                    'Undo-PartialImmediateParentOwnership',
                    'Add-OwnedImmediateParentDirectory',
                    'Remove-OwnedParentMarker',
                    'Complete-OwnedImmediateParentDirectories',
                    'Remove-OwnedEmptyImmediateParentDirectory'
                )
                foreach ($functionName in $requiredFunctionNames) {
                    $functionAsts = @($installerAst.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -ceq $functionName
                    }, $true))
                    if ($functionAsts.Count -ne 1) {
                        throw "expected one real $functionName function; found $($functionAsts.Count)"
                    }

                    Invoke-Expression $functionAsts[0].Extent.Text
                }

                $realOwnedParentCalls = @($installerAst.FindAll({
                    param($node)
                    if ($node -isnot [Management.Automation.Language.CommandAst] -or
                        $node.GetCommandName() -cne 'Add-OwnedImmediateParentDirectory' -or
                        $node.CommandElements.Count -ne 3) {
                        return $false
                    }

                    $childArgument = $node.CommandElements[1].Extent.Text
                    $recordsArgument = $node.CommandElements[2].Extent.Text
                    return (($childArgument -ceq '$install' -or $childArgument -ceq '$data') -and
                        $recordsArgument -ceq '$ownedParentDirectories')
                }, $true))
                if ($realOwnedParentCalls.Count -ne 2) {
                    throw "expected the two real installer ownership calls; found $($realOwnedParentCalls.Count)"
                }

                function Assert-Probe {
                    param([bool]$Condition, [string]$Message)
                    if (-not $Condition) { throw $Message }
                }

                # Execute the exact two command extents from the production install path. This
                # catches function-body parse ambiguities that are otherwise reported at the call.
                $install = Join-Path $root 'real-program-files\MCSV'
                $data = Join-Path $root 'real-program-data\MCSV'
                $ownedParentDirectories = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                foreach ($realOwnedParentCall in $realOwnedParentCalls) {
                    Invoke-Expression $realOwnedParentCall.Extent.Text
                }
                Assert-Probe ($ownedParentDirectories.Count -eq 2) `
                    'real installer ownership calls did not record both immediate parents'
                foreach ($realRecord in @($ownedParentDirectories.Values)) {
                    Assert-Probe (Remove-OwnedEmptyImmediateParentDirectory $realRecord) `
                        'real installer ownership call parent was not safely removed'
                }

                # A parent created and owned by this transaction is deleted once its child is gone.
                $ownedChild = Join-Path $root 'owned-empty\MCSV'
                $ownedRecords = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                Add-OwnedImmediateParentDirectory $ownedChild $ownedRecords
                [IO.Directory]::CreateDirectory($ownedChild) | Out-Null
                [IO.Directory]::Delete($ownedChild, $false)
                $ownedParent = [IO.Path]::GetDirectoryName($ownedChild)
                Assert-Probe ($ownedRecords.Count -eq 1) 'owned parent was not recorded'
                Assert-Probe (Remove-OwnedEmptyImmediateParentDirectory @($ownedRecords.Values)[0]) `
                    'owned empty parent was not deleted'
                Assert-Probe (-not (Test-Path -LiteralPath $ownedParent)) `
                    'owned empty parent still exists'

                # If recording fails after the exclusive create, partial cleanup removes the
                # still-empty directory without requiring a rollback ownership record.
                $recordFailureChild = Join-Path $root 'record-add-failure\MCSV'
                $recordFailureBacking = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                $recordFailureRecords = [Collections.ObjectModel.ReadOnlyDictionary[string, object]]::new(
                    $recordFailureBacking)
                try {
                    Add-OwnedImmediateParentDirectory $recordFailureChild $recordFailureRecords
                    throw 'read-only ownership record unexpectedly accepted Add'
                } catch [Management.Automation.MethodInvocationException] {
                } catch [Management.Automation.RuntimeException] {
                }
                Assert-Probe (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($recordFailureChild)))) `
                    'record Add failure left an untracked empty parent'

                # A parent that existed before registration is never claimed or deleted.
                $existingParent = Join-Path $root 'preexisting'
                [IO.Directory]::CreateDirectory($existingParent) | Out-Null
                $existingRecords = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                Add-OwnedImmediateParentDirectory (Join-Path $existingParent 'MCSV') $existingRecords
                Assert-Probe ($existingRecords.Count -eq 0) 'preexisting parent was incorrectly claimed'
                Assert-Probe (Test-Path -LiteralPath $existingParent -PathType Container) `
                    'preexisting parent was deleted'

                # Shared immediate parents are represented by one case-insensitive ownership record.
                $sharedRecords = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                $sharedFirst = Join-Path $root 'shared\Install'
                $sharedSecond = Join-Path $root 'SHARED\Data'
                Add-OwnedImmediateParentDirectory $sharedFirst $sharedRecords
                Add-OwnedImmediateParentDirectory $sharedSecond $sharedRecords
                Assert-Probe ($sharedRecords.Count -eq 1) 'shared parent ownership was not deduplicated'
                Assert-Probe (Remove-OwnedEmptyImmediateParentDirectory @($sharedRecords.Values)[0]) `
                    'deduplicated shared parent was not deleted'

                # A wrong nonce or a missing marker fails closed and preserves the directory.
                $wrongChild = Join-Path $root 'wrong-nonce\MCSV'
                $wrongRecords = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                Add-OwnedImmediateParentDirectory $wrongChild $wrongRecords
                $wrongRecord = @($wrongRecords.Values)[0]
                [IO.File]::WriteAllText($wrongRecord.MarkerPath, 'not-the-recorded-nonce')
                Assert-Probe (-not (Remove-OwnedEmptyImmediateParentDirectory $wrongRecord)) `
                    'wrong nonce was accepted'
                Assert-Probe (Test-Path -LiteralPath $wrongRecord.ParentPath -PathType Container) `
                    'wrong-nonce parent was deleted'

                $missingChild = Join-Path $root 'missing-marker\MCSV'
                $missingRecords = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                Add-OwnedImmediateParentDirectory $missingChild $missingRecords
                $missingRecord = @($missingRecords.Values)[0]
                [IO.File]::Delete($missingRecord.MarkerPath)
                Assert-Probe (-not (Remove-OwnedEmptyImmediateParentDirectory $missingRecord)) `
                    'missing marker was accepted'
                Assert-Probe (Test-Path -LiteralPath $missingRecord.ParentPath -PathType Container) `
                    'missing-marker parent was deleted'

                # Foreign content preserves the parent and the content; only our marker is removed.
                $foreignChild = Join-Path $root 'foreign-content\MCSV'
                $foreignRecords = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                Add-OwnedImmediateParentDirectory $foreignChild $foreignRecords
                $foreignRecord = @($foreignRecords.Values)[0]
                $sentinel = Join-Path $foreignRecord.ParentPath 'foreign.sentinel'
                [IO.File]::WriteAllText($sentinel, 'must-survive')
                Assert-Probe (-not (Remove-OwnedEmptyImmediateParentDirectory $foreignRecord)) `
                    'foreign-content parent was reported deleted'
                Assert-Probe ((Test-Path -LiteralPath $sentinel -PathType Leaf) -and
                    ([IO.File]::ReadAllText($sentinel) -ceq 'must-survive')) `
                    'foreign sentinel was changed or deleted'
                Assert-Probe (-not (Test-Path -LiteralPath $foreignRecord.MarkerPath)) `
                    'owned marker was not removed from preserved foreign parent'

                # A successful install removes its temporary marker without removing the parent/child.
                $successChild = Join-Path $root 'successful-install\MCSV'
                $successRecords = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                Add-OwnedImmediateParentDirectory $successChild $successRecords
                [IO.Directory]::CreateDirectory($successChild) | Out-Null
                $successRecord = @($successRecords.Values)[0]
                Complete-OwnedImmediateParentDirectories $successRecords
                Assert-Probe (-not (Test-Path -LiteralPath $successRecord.MarkerPath)) `
                    'successful install marker remains'
                Assert-Probe ((Test-Path -LiteralPath $successRecord.ParentPath -PathType Container) -and
                    (Test-Path -LiteralPath $successChild -PathType Container)) `
                    'successful install parent or child was removed'

                # Replace a recorded parent with a junction. Cleanup must not traverse or alter its target.
                $junctionChild = Join-Path $root 'junction-parent\MCSV'
                $junctionRecords = [Collections.Generic.Dictionary[string, object]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
                Add-OwnedImmediateParentDirectory $junctionChild $junctionRecords
                $junctionRecord = @($junctionRecords.Values)[0]
                [IO.File]::Delete($junctionRecord.MarkerPath)
                [IO.Directory]::Delete($junctionRecord.ParentPath, $false)
                $junctionTarget = Join-Path $root 'junction-target'
                [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
                $junctionSentinel = Join-Path $junctionTarget 'target.sentinel'
                [IO.File]::WriteAllText($junctionSentinel, 'target-must-survive')
                $junctionOutput = & "$env:SystemRoot\System32\cmd.exe" /d /c `
                    "mklink /J `"$($junctionRecord.ParentPath)`" `"$junctionTarget`"" 2>&1
                if ($LASTEXITCODE -eq 0) {
                    Assert-Probe (-not (Remove-OwnedEmptyImmediateParentDirectory $junctionRecord)) `
                        'reparse parent was accepted'
                    Assert-Probe ((Test-Path -LiteralPath $junctionSentinel -PathType Leaf) -and
                        ([IO.File]::ReadAllText($junctionSentinel) -ceq 'target-must-survive')) `
                        'junction target was changed or deleted'
                    [IO.Directory]::Delete($junctionRecord.ParentPath, $false)
                } else {
                    Write-Output "JUNCTION-SKIP: $($junctionOutput -join ' ')"
                }

                Write-Output 'OWNED-PARENT-PROBE-PASSED'
                exit 0
                """;

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(probe)));
            startInfo.Environment["MCSV_PARENT_TEST_ROOT"] = root;
            startInfo.Environment["MCSV_INSTALLER_SOURCE"] = Path.Combine(
                RepositoryRoot,
                "scripts",
                "Install-MuhunMcsv.ps1");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start owned-parent rollback probe.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"Owned-parent rollback probe failed. Exit={process.ExitCode}; " +
                $"stdout={standardOutput}; stderr={standardError}");
            Assert.Contains("OWNED-PARENT-PROBE-PASSED", standardOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string ExtractFunction(string source, string functionName)
    {
        var match = Regex.Match(
            source,
            $@"(?ms)^function {Regex.Escape(functionName)} \{{.*?^\}}\r?$",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not locate the real {functionName} function.");
        return match.Value;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MinecraftServerManager.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
