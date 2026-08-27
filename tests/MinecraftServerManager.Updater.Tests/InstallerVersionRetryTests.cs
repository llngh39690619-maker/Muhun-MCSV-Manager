using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater.Tests;

public sealed class InstallerVersionRetryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string InstallerPath = Path.Combine(
        RepositoryRoot,
        "scripts",
        "Install-MuhunMcsv.ps1");

    [Fact]
    public void InstallerRetryContractsReuseOnlyExactPayloadAndQuarantineRecognizedPartialPayload()
    {
        var source = File.ReadAllText(InstallerPath);

        Assert.Contains("function Get-ExistingVersionPayloadState", source, StringComparison.Ordinal);
        Assert.Contains("return 'Exact'", source, StringComparison.Ordinal);
        Assert.Contains("'RecognizedPartial'", source, StringComparison.Ordinal);
        Assert.Contains("else { 'Unrecognized' }", source, StringComparison.Ordinal);
        Assert.Contains("function Move-ProvisionedVersionToQuarantine", source, StringComparison.Ordinal);
        Assert.Contains(".failed-install-", source, StringComparison.Ordinal);
        Assert.Contains("$targetWasActive", source, StringComparison.Ordinal);
        Assert.Contains("$serviceUsedTarget", source, StringComparison.Ordinal);
        Assert.Contains("if ($existingVersionState -eq 'Missing')", source, StringComparison.Ordinal);
        Assert.Contains("$serviceStopTimeoutSeconds = 120", source, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(source, @"Stop-Service -Name \$serviceName -Force -NoWait").Count);
        Assert.Equal(
            2,
            Regex.Matches(
                source,
                @"\[TimeSpan\]::FromSeconds\(\$serviceStopTimeoutSeconds\)").Count);
        Assert.DoesNotContain(
            "版本目錄已存在，為避免覆寫已驗證版本而停止",
            source,
            StringComparison.Ordinal);

        var stateCheck = source.IndexOf(
            "$existingVersionState = Get-ExistingVersionPayloadState",
            StringComparison.Ordinal);
        var quarantine = source.IndexOf(
            "$retryQuarantineRoot = Move-ProvisionedVersionToQuarantine",
            stateCheck,
            StringComparison.Ordinal);
        var staging = source.IndexOf(
            "New-Item -ItemType Directory -Path $stagingRoot",
            quarantine,
            StringComparison.Ordinal);
        Assert.True(
            stateCheck >= 0 && quarantine > stateCheck && staging > quarantine,
            "A retry must classify and isolate a recognized partial version before provisioning a new payload.");
    }

    [Fact]
    public void RealInstallerHelpersClassifyExactAndPartialPayloadsAndMoveOnlyDirectManagedChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = File.ReadAllText(InstallerPath);
        var functions = string.Join(
            Environment.NewLine,
            ExtractFunction(source, "Test-IsUnderRoot"),
            ExtractFunction(source, "Assert-NoExistingReparsePoints"),
            ExtractFunction(source, "Test-SafeRelativePath"),
            ExtractFunction(source, "Resolve-SafeSourceFile"),
            ExtractFunction(source, "Get-Sha256Hex"),
            ExtractFunction(source, "Get-ExistingVersionPayloadState"),
            ExtractFunction(source, "Move-ProvisionedVersionToQuarantine"));
        var probe = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            {{functions}}
            $root = Join-Path ([IO.Path]::GetTempPath()) ('muhun-version-retry-' + [Guid]::NewGuid().ToString('N'))
            $sourceRoot = Join-Path $root 'release'
            $versionsRoot = Join-Path $root 'versions'
            $versionRoot = Join-Path $versionsRoot '1.0.4'
            [IO.Directory]::CreateDirectory($sourceRoot) | Out-Null
            [IO.Directory]::CreateDirectory((Join-Path $sourceRoot 'nested')) | Out-Null
            [IO.Directory]::CreateDirectory((Join-Path $versionRoot 'nested')) | Out-Null
            try {
                [IO.File]::WriteAllText((Join-Path $sourceRoot 'one.bin'), 'one')
                [IO.File]::WriteAllText((Join-Path $sourceRoot 'nested\two.bin'), 'two')
                [IO.File]::WriteAllText((Join-Path $sourceRoot 'installed-version.v1.json'), '{"version":"1.0.4"}')
                Copy-Item -LiteralPath (Join-Path $sourceRoot 'one.bin') -Destination (Join-Path $versionRoot 'one.bin')
                Copy-Item -LiteralPath (Join-Path $sourceRoot 'nested\two.bin') -Destination (Join-Path $versionRoot 'nested\two.bin')
                Copy-Item -LiteralPath (Join-Path $sourceRoot 'installed-version.v1.json') -Destination (Join-Path $versionRoot 'installed-version.v1.json')
                $entries = @(
                    [pscustomobject]@{ path='one.bin'; sizeBytes=(Get-Item (Join-Path $sourceRoot 'one.bin')).Length; sha256=Get-Sha256Hex (Join-Path $sourceRoot 'one.bin') },
                    [pscustomobject]@{ path='nested/two.bin'; sizeBytes=(Get-Item (Join-Path $sourceRoot 'nested\two.bin')).Length; sha256=Get-Sha256Hex (Join-Path $sourceRoot 'nested\two.bin') }
                )
                $exact = Get-ExistingVersionPayloadState $versionRoot $sourceRoot $entries
                if ($exact -cne 'Exact') { throw "expected Exact, got $exact" }

                $unexpectedDirectory = Join-Path $versionRoot 'unexpected-empty'
                [IO.Directory]::CreateDirectory($unexpectedDirectory) | Out-Null
                $unexpected = Get-ExistingVersionPayloadState $versionRoot $sourceRoot $entries
                if ($unexpected -cne 'RecognizedPartial') { throw "expected extra directory rejection, got $unexpected" }
                [IO.Directory]::Delete($unexpectedDirectory, $false)

                Remove-Item -LiteralPath (Join-Path $versionRoot 'nested\two.bin') -Force
                $partial = Get-ExistingVersionPayloadState $versionRoot $sourceRoot $entries
                if ($partial -cne 'RecognizedPartial') { throw "expected RecognizedPartial, got $partial" }

                [IO.File]::WriteAllText((Join-Path $versionRoot 'installed-version.v1.json'), 'tampered')
                $unrecognized = Get-ExistingVersionPayloadState $versionRoot $sourceRoot $entries
                if ($unrecognized -cne 'Unrecognized') { throw "expected Unrecognized, got $unrecognized" }
                Copy-Item -LiteralPath (Join-Path $sourceRoot 'installed-version.v1.json') -Destination (Join-Path $versionRoot 'installed-version.v1.json') -Force

                $sentinel = Join-Path $versionsRoot 'keep'
                [IO.Directory]::CreateDirectory($sentinel) | Out-Null
                [IO.File]::WriteAllText((Join-Path $sentinel 'sentinel.txt'), 'keep')
                $quarantine = Move-ProvisionedVersionToQuarantine $versionRoot $versionsRoot '1.0.4'
                if (Test-Path -LiteralPath $versionRoot) { throw 'canonical target was not released' }
                if (-not (Test-Path -LiteralPath $quarantine -PathType Container)) { throw 'quarantine was not created' }
                if (-not (Test-Path -LiteralPath (Join-Path $sentinel 'sentinel.txt') -PathType Leaf)) { throw 'sibling was changed' }

                $nested = Join-Path $sentinel 'nested-version'
                [IO.Directory]::CreateDirectory($nested) | Out-Null
                $rejected = $false
                try { Move-ProvisionedVersionToQuarantine $nested $versionsRoot '1.0.4' | Out-Null }
                catch { $rejected = $_.Exception.Message -match '正下方' }
                if (-not $rejected) { throw 'nested non-version directory was accepted' }
            } finally {
                if (Test-Path -LiteralPath $root) { [IO.Directory]::Delete($root, $true) }
            }
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
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(probe));
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
        startInfo.ArgumentList.Add(encoded);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start installer retry probe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Installer retry probe failed. Exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
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
