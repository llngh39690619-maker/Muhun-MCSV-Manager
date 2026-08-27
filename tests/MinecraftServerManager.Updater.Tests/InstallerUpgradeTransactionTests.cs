using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater.Tests;

public sealed class InstallerUpgradeTransactionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string InstallerPath = Path.Combine(
        RepositoryRoot,
        "scripts",
        "Install-MuhunMcsv.ps1");

    [Fact]
    public void InstallerContractsRequireAtomicSamePublisherLauncherReplacementAndRollback()
    {
        var source = File.ReadAllText(InstallerPath);

        Assert.Contains("function Assert-SafeSignedStableLauncher", source, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $Path", source, StringComparison.Ordinal);
        Assert.Contains("Get-CertificateSha256 $signature.SignerCertificate", source, StringComparison.Ordinal);
        Assert.Contains("Assert-NoExistingReparsePoints $Path $Label", source, StringComparison.Ordinal);
        Assert.Contains("$item -isnot [IO.FileInfo]", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Replace($temporaryPath, $DestinationPath, $backupPath, $true)", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Replace($Mutation.BackupPath, $DestinationPath, $displacedPath, $true)", source, StringComparison.Ordinal);
        Assert.Contains("Install-StableLauncherTransactionally", source, StringComparison.Ordinal);
        Assert.Contains("Restore-StableLauncherTransaction", source, StringComparison.Ordinal);
        Assert.Contains("Complete-StableLauncherTransaction", source, StringComparison.Ordinal);

        var replace = source.IndexOf(
            "Install-StableLauncherTransactionally `",
            StringComparison.Ordinal);
        var ready = source.IndexOf(
            "[void](Wait-ProductActivationReady `",
            replace,
            StringComparison.Ordinal);
        var complete = source.IndexOf(
            "Complete-StableLauncherTransaction `",
            ready,
            StringComparison.Ordinal);
        var commit = source.IndexOf("$installationApplied = $true", complete, StringComparison.Ordinal);
        var rollback = source.IndexOf(
            "Restore-StableLauncherTransaction `",
            commit,
            StringComparison.Ordinal);
        Assert.True(
            replace >= 0 && ready > replace && complete > ready && commit > complete && rollback > commit,
            "The stable launcher must remain rollback-capable until the core install commits.");
    }

    [Fact]
    public void InstallerContractsSnapshotAndRestoreTheCompleteExistingServiceDefinition()
    {
        var source = File.ReadAllText(InstallerPath);

        Assert.Contains("QueryServiceConfig2W", source, StringComparison.Ordinal);
        Assert.Contains("ChangeServiceConfig2W", source, StringComparison.Ordinal);
        Assert.Contains("ServiceStart = 0x0010", source, StringComparison.Ordinal);
        Assert.Contains("ServiceConfigFailureActions = 2", source, StringComparison.Ordinal);
        Assert.Contains("ServiceConfigFailureActionsFlag = 4", source, StringComparison.Ordinal);
        Assert.Contains("GetRequiredRestoreAccess(snapshot)", source, StringComparison.Ordinal);
        Assert.Contains("Math.Max(managedActions.Length, 1)", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.RebootMessage ?? string.Empty", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.Command ?? string.Empty", source, StringComparison.Ordinal);
        Assert.Contains("FailureConfiguration = Get-ServiceFailureConfigurationSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("DisplayName = [string]$Definition.DisplayName", source, StringComparison.Ordinal);
        Assert.Contains("Description = if ($null -eq $Definition.Description)", source, StringComparison.Ordinal);
        Assert.Contains("StartArgument = Get-ServiceStartArgument", source, StringComparison.Ordinal);
        Assert.Contains("Account = [string]$Definition.StartName", source, StringComparison.Ordinal);
        Assert.Contains("Restore-ServiceFailureConfigurationSnapshot $Snapshot.FailureConfiguration", source, StringComparison.Ordinal);
        Assert.Contains("Service failure actions/failure flag 無法完整回復", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-Sc description $serviceName $Snapshot.Description", source, StringComparison.Ordinal);
        Assert.Contains("'DisplayName=' $Snapshot.DisplayName", source, StringComparison.Ordinal);
        Assert.Contains("'obj=' $Snapshot.Account", source, StringComparison.Ordinal);
        Assert.Contains("'start=' $Snapshot.StartArgument", source, StringComparison.Ordinal);
        Assert.Contains("Restore-ServiceRollbackSnapshot $previousServiceSnapshot", source, StringComparison.Ordinal);

        var rollbackFunction = ExtractFunction(source, "Restore-ServiceRollbackSnapshot");
        Assert.DoesNotContain("'delayed-auto'", rollbackFunction, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceFailureInteropCompilesWithoutOpeningTheServiceManager()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = File.ReadAllText(InstallerPath);
        var interopFunction = Regex.Match(
            source,
            @"(?ms)^function Initialize-ServiceFailureConfigurationInterop \{.*?^\}\r?\n(?=\r?\nfunction Get-ServiceFailureConfigurationSnapshot)",
            RegexOptions.CultureInvariant);
        Assert.True(interopFunction.Success, "Could not extract the Service failure interop function.");
        var probe = interopFunction.Value +
            Environment.NewLine +
            "Initialize-ServiceFailureConfigurationInterop\n" +
            "if ($null -eq ('Muhun.Mcsv.Installer.ServiceFailureConfiguration' -as [type])) { exit 2 }\n";
        RunPowerShellFileProbe(probe);
    }

    [Fact]
    public void ServiceFailureInteropRequestsRestartAccessAndRejectsInvalidSnapshotsBeforeMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = File.ReadAllText(InstallerPath);
        var interopFunction = Regex.Match(
            source,
            @"(?ms)^function Initialize-ServiceFailureConfigurationInterop \{.*?^\}\r?\n(?=\r?\nfunction Get-ServiceFailureConfigurationSnapshot)",
            RegexOptions.CultureInvariant);
        Assert.True(interopFunction.Success, "Could not extract the Service failure interop function.");
        var probe = interopFunction.Value +
            Environment.NewLine +
            "Initialize-ServiceFailureConfigurationInterop\n" +
            """
            $type = [Muhun.Mcsv.Installer.ServiceFailureConfiguration]
            $flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
            $method = $type.GetMethod('GetRequiredRestoreAccess', $flags)
            if ($null -eq $method) { throw 'required access helper missing' }

            function Get-RequiredAccess([object]$Snapshot) {
                return [uint32]$method.Invoke($null, [object[]]@($Snapshot))
            }

            $nullActions = [Muhun.Mcsv.Installer.ServiceFailureConfigurationSnapshot]::new()
            $nullActions.Actions = $null
            if ((Get-RequiredAccess $nullActions) -ne 0x0002) {
                throw 'a null action list requested excessive access'
            }

            $emptyActions = [Muhun.Mcsv.Installer.ServiceFailureConfigurationSnapshot]::new()
            $emptyActions.Actions = [Muhun.Mcsv.Installer.ServiceFailureAction[]]@()
            if ((Get-RequiredAccess $emptyActions) -ne 0x0002) {
                throw 'an empty action list requested excessive access'
            }

            $restart = [Muhun.Mcsv.Installer.ServiceFailureConfigurationSnapshot]::new()
            $restart.Actions = [Muhun.Mcsv.Installer.ServiceFailureAction[]]@(
                [Muhun.Mcsv.Installer.ServiceFailureAction]@{ Type = 1; DelayMilliseconds = 5000 })
            if ((Get-RequiredAccess $restart) -ne 0x0012) {
                throw 'a restart action did not request SERVICE_CHANGE_CONFIG and SERVICE_START'
            }

            foreach ($invalidActions in @(
                [Muhun.Mcsv.Installer.ServiceFailureAction[]]@($null),
                [Muhun.Mcsv.Installer.ServiceFailureAction[]]@(
                    [Muhun.Mcsv.Installer.ServiceFailureAction]@{ Type = 99; DelayMilliseconds = 0 })
            )) {
                $invalid = [Muhun.Mcsv.Installer.ServiceFailureConfigurationSnapshot]::new()
                $invalid.Actions = $invalidActions
                try {
                    [void](Get-RequiredAccess $invalid)
                    throw 'invalid action snapshot was accepted'
                } catch [Reflection.TargetInvocationException] {
                    if ($null -eq $_.Exception.InnerException -or
                        $_.Exception.InnerException -isnot [InvalidOperationException]) {
                        throw
                    }
                }
            }
            """;
        RunPowerShellFileProbe(probe);
    }

    [Fact]
    public void LauncherTransactionReplacesRollsBackAndCommitsWithoutAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = File.ReadAllText(InstallerPath);
        var functions = string.Join(
            Environment.NewLine,
            ExtractFunction(source, "Install-StableLauncherTransactionally"),
            ExtractFunction(source, "Restore-StableLauncherTransaction"),
            ExtractFunction(source, "Complete-StableLauncherTransaction"));
        var probe = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            function Assert-NoExistingReparsePoints { param([string]$Path, [string]$Label) }
            function Assert-SafeSignedStableLauncher {
                param([string]$Path, [string]$PublisherCertificateSha256, [string]$Label)
                if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is not a file" }
                $item = Get-Item -LiteralPath $Path -Force
                if ($item -isnot [IO.FileInfo] -or
                    ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "$Label is unsafe"
                }
                return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            {{functions}}
            $root = Join-Path ([IO.Path]::GetTempPath()) ('muhun-launcher-probe-' + [Guid]::NewGuid().ToString('N'))
            [IO.Directory]::CreateDirectory($root) | Out-Null
            try {
                $sourcePath = Join-Path $root 'new.exe'
                $destinationPath = Join-Path $root 'stable.exe'
                [IO.File]::WriteAllText($sourcePath, 'new-launcher')
                [IO.File]::WriteAllText($destinationPath, 'old-launcher')
                $mutation = [pscustomobject]@{ Created=$false; Replaced=$false; BackupPath=$null; PreviousSha256=$null }
                Install-StableLauncherTransactionally $sourcePath $destinationPath ('a' * 64) $mutation
                if (-not $mutation.Replaced -or $mutation.Created) { throw 'replace mutation was not recorded' }
                if ([IO.File]::ReadAllText($destinationPath) -cne 'new-launcher') { throw 'new launcher was not installed' }
                if (-not (Test-Path -LiteralPath $mutation.BackupPath -PathType Leaf)) { throw 'rollback backup missing' }
                Restore-StableLauncherTransaction $destinationPath ('a' * 64) $mutation
                if ([IO.File]::ReadAllText($destinationPath) -cne 'old-launcher') { throw 'old launcher was not restored' }

                $committed = [pscustomobject]@{ Created=$false; Replaced=$false; BackupPath=$null; PreviousSha256=$null }
                Install-StableLauncherTransactionally $sourcePath $destinationPath ('a' * 64) $committed
                $committedBackup = $committed.BackupPath
                Complete-StableLauncherTransaction ('a' * 64) $committed
                if ([IO.File]::ReadAllText($destinationPath) -cne 'new-launcher') { throw 'committed launcher changed' }
                if (Test-Path -LiteralPath $committedBackup) { throw 'committed backup was retained' }

                $unsafeDestination = Join-Path $root 'unsafe.exe'
                [IO.Directory]::CreateDirectory($unsafeDestination) | Out-Null
                $unsafe = [pscustomobject]@{ Created=$false; Replaced=$false; BackupPath=$null; PreviousSha256=$null }
                try {
                    Install-StableLauncherTransactionally $sourcePath $unsafeDestination ('a' * 64) $unsafe
                    throw 'unsafe destination was accepted'
                } catch {
                    if ($_.Exception.Message -eq 'unsafe destination was accepted') { throw }
                }
                if ($unsafe.Created -or $unsafe.Replaced) { throw 'unsafe mutation was recorded' }
            } finally {
                [IO.Directory]::Delete($root, $true)
            }
            """;

        RunPowerShellProbe(probe);
    }

    [Fact]
    public void ServiceSnapshotAndRestorePreservePriorValuesWithoutScmMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = File.ReadAllText(InstallerPath);
        var functions = string.Join(
            Environment.NewLine,
            ExtractFunction(source, "Get-ServiceStartArgument"),
            ExtractFunction(source, "New-ServiceRollbackSnapshot"),
            ExtractFunction(source, "Restore-ServiceRollbackSnapshot"));
        var probe = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $serviceName = 'MuhunMCSV-Probe'
            $script:scCalls = [Collections.Generic.List[object]]::new()
            $script:restoredFailure = $null
            function Get-ServiceDelayedAutoStart { return $true }
            function Get-ServiceFailureConfigurationSnapshot {
                return [pscustomobject]@{ Reset=42; Flag=$false; Actions=@('run/77') }
            }
            function Restore-ServiceFailureConfigurationSnapshot { param($Snapshot); $script:restoredFailure = $Snapshot }
            function Invoke-Sc {
                param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
                $script:scCalls.Add(@($Arguments))
            }
            function Get-ServiceSecurityDescriptor { return 'D:(A;;RP;;;SY)' }
            function Get-ServiceSidType { return 'restricted' }
            {{functions}}
            $definition = [pscustomobject]@{
                PathName='"C:\Old\service.exe" "--old"'
                DisplayName='Prior display'
                Description='Prior description'
                StartName='NT SERVICE\MuhunMCSV'
                StartMode='Auto'
            }
            $snapshot = New-ServiceRollbackSnapshot $definition 'D:(A;;RP;;;SY)' 'restricted' $true 39123
            if ($snapshot.BinaryPath -cne $definition.PathName -or
                $snapshot.DisplayName -cne 'Prior display' -or
                $snapshot.Description -cne 'Prior description' -or
                $snapshot.StartArgument -cne 'delayed-auto' -or
                $snapshot.Account -cne 'NT SERVICE\MuhunMCSV' -or
                -not $snapshot.WasRunning -or $snapshot.RestPort -ne 39123) {
                throw 'service snapshot lost a prior value'
            }
            Restore-ServiceRollbackSnapshot $snapshot
            $config = @($script:scCalls[0])
            $expectedConfig = @('config',$serviceName,'binPath=',$definition.PathName,'start=','delayed-auto','DisplayName=','Prior display','obj=','NT SERVICE\MuhunMCSV')
            if (($config -join [char]31) -cne ($expectedConfig -join [char]31)) { throw "config mismatch: $($config -join '|')" }
            $description = @($script:scCalls[1])
            if (($description -join '|') -cne "description|$serviceName|Prior description") { throw 'description mismatch' }
            if ($script:restoredFailure -ne $snapshot.FailureConfiguration) { throw 'failure policy mismatch' }
            if ((@($script:scCalls[2]) -join '|') -cne "sdset|$serviceName|D:(A;;RP;;;SY)") { throw 'SDDL mismatch' }
            if ((@($script:scCalls[3]) -join '|') -cne "sidtype|$serviceName|restricted") { throw 'SID type mismatch' }
            """;

        RunPowerShellProbe(probe);
    }

    private static string ExtractFunction(string source, string name)
    {
        var match = Regex.Match(
            source,
            $@"(?ms)^function {Regex.Escape(name)} \{{.*?^\}}\r?$",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not extract installer function {name}.");
        return match.Value;
    }

    private static void RunPowerShellProbe(string probe)
    {
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
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the PowerShell probe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"PowerShell probe failed with exit code {process.ExitCode}. stdout={stdout}; stderr={stderr}");
    }

    private static void RunPowerShellFileProbe(string probe)
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"muhun-installer-probe-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, probe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
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
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the PowerShell file probe.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"PowerShell file probe failed with exit code {process.ExitCode}. stdout={stdout}; stderr={stderr}");
        }
        finally
        {
            File.Delete(scriptPath);
        }
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
