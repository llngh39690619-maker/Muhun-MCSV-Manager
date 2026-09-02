using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater.Tests;

public sealed class UninstallerRemovalSafetyContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void PersistentDataDeletionHasItsOwnPermanentDeletionAuthorizationBoundary()
    {
        var source = ReadUninstaller();
        const string programAuthorization =
            "if ($PSCmdlet.ShouldProcess($install, '解除安裝 X MCSV 與 Windows Service'))";
        const string dataAuthorization =
            "if ($RemoveData -and $null -ne $data -and $PSCmdlet.ShouldProcess(";
        const string dataDeletion = "Remove-Item -LiteralPath $data -Recurse -Force";

        var programAuthorizationIndex = source.IndexOf(programAuthorization, StringComparison.Ordinal);
        var installDeletionIndex = source.IndexOf(
            "Remove-ManagedProgramPayload -InstallRoot $install",
            StringComparison.Ordinal);
        var dataAuthorizationIndex = source.IndexOf(dataAuthorization, StringComparison.Ordinal);
        var dataRevalidationIndex = source.IndexOf(
            "[void](Resolve-GuardedRoot $data '.muhun-mcsv-data-root')",
            dataAuthorizationIndex,
            StringComparison.Ordinal);
        var dataDeletionIndex = source.IndexOf(dataDeletion, StringComparison.Ordinal);

        Assert.True(programAuthorizationIndex >= 0, "Program/service removal must have a ShouldProcess boundary.");
        Assert.True(
            installDeletionIndex > programAuthorizationIndex,
            "The managed executable payload may be removed only after its program/service authorization.");
        Assert.True(
            dataAuthorizationIndex > installDeletionIndex,
            "Persistent data must use a second authorization outside the program-removal operation.");
        Assert.True(
            dataRevalidationIndex > dataAuthorizationIndex && dataDeletionIndex > dataRevalidationIndex,
            "The separately authorized data root must be marker-validated immediately before deletion.");
        Assert.Single(Regex.Matches(source, Regex.Escape(dataDeletion)).Cast<Match>());
        Assert.Contains("永久刪除所有伺服器", source, StringComparison.Ordinal);
        Assert.Contains("此動作無法復原", source, StringComparison.Ordinal);
        Assert.Contains("SupportsShouldProcess = $true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceDeletionDisposesEveryControllerAndWaitsForScmRemoval()
    {
        var source = ReadUninstaller();
        var removeFunction = ExtractFunction(source, "Remove-WindowsServiceAndWait");
        var waitFunction = ExtractFunction(source, "Wait-WindowsServiceAbsent");

        var stopIndex = removeFunction.IndexOf("WaitForStatus('Stopped'", StringComparison.Ordinal);
        var disposeIndex = removeFunction.IndexOf("$service.Dispose()", StringComparison.Ordinal);
        var scDeleteIndex = removeFunction.IndexOf("sc.exe\" delete $Name", StringComparison.Ordinal);
        var waitIndex = removeFunction.IndexOf(
            "Wait-WindowsServiceAbsent -Name $Name -TimeoutSeconds 30",
            StringComparison.Ordinal);

        Assert.True(stopIndex >= 0 && disposeIndex > stopIndex && scDeleteIndex > disposeIndex);
        Assert.True(waitIndex > scDeleteIndex, "The uninstaller must not return before SCM forgets the service.");
        Assert.Contains("$deleteExitCode -ne 1060", removeFunction, StringComparison.Ordinal);
        Assert.Contains("$deleteExitCode -ne 1072", removeFunction, StringComparison.Ordinal);
        Assert.Contains("ERROR_SERVICE_MARKED_FOR_DELETE (1072)", removeFunction, StringComparison.Ordinal);

        Assert.Contains("[ValidateRange(1, 120)][int]$TimeoutSeconds = 30", waitFunction, StringComparison.Ordinal);
        Assert.Contains("[Diagnostics.Stopwatch]::StartNew()", waitFunction, StringComparison.Ordinal);
        Assert.Contains("sc.exe\" query $Name", waitFunction, StringComparison.Ordinal);
        Assert.Contains("$queryExitCode -eq 1060", waitFunction, StringComparison.Ordinal);
        Assert.Contains("$queryExitCode -ne 1072", waitFunction, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds 250", waitFunction, StringComparison.Ordinal);
        Assert.Contains("仍未完全移除", waitFunction, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedDataRootsAreBoundToTrustedActiveVersionChannel()
    {
        var source = ReadUninstaller();

        Assert.Contains("$activeVersionPath = Join-Path $install 'active-version.v1'", source, StringComparison.Ordinal);
        Assert.Contains("$activeChannel = if ($activeVersion.Contains('-', [StringComparison]::Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("if ($channel -ne $activeChannel -or", source, StringComparison.Ordinal);
        Assert.Contains("Join-Path $install \"service\\$activeChannel\"", source, StringComparison.Ordinal);
        Assert.Contains("Join-Path $install \"exchange\\$activeChannel\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("service\\beta", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exchange\\beta", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadUninstaller()
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "Uninstall-MuhunMcsv.ps1"));
    }

    private static string ExtractFunction(string source, string name)
    {
        var match = Regex.Match(
            source,
            $@"(?ms)^function {Regex.Escape(name)} \{{.*?^\}}\r?$",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not locate PowerShell function {name}.");
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
