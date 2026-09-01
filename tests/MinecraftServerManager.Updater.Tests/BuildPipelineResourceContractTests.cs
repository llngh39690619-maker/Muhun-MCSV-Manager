using System.Text.RegularExpressions;

namespace MinecraftServerManager.Updater.Tests;

public sealed class BuildPipelineResourceContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DotnetPipelinesBuildOnceTestWithoutBuildAndRestoreRidOnce()
    {
        var formal = ReadScript("Build-MuhunMcsvFormalRelease.ps1");
        var developer = ReadScript("build-and-publish.ps1");

        foreach (var source in new[] { formal, developer })
        {
            Assert.Contains("[int]$MaxBuildConcurrency = 4", source, StringComparison.Ordinal);
            Assert.Contains("\"-m:$MaxBuildConcurrency\"", source, StringComparison.Ordinal);
            Assert.Contains("'--no-build'", source, StringComparison.Ordinal);
            Assert.Contains("'--no-restore'", source, StringComparison.Ordinal);
            Assert.Contains("--runtime", source, StringComparison.Ordinal);
            Assert.Contains("BelowNormal", source, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(
            new Regex(@"Invoke-Dotnet\s+@\('restore',\s*\$testProject", RegexOptions.CultureInvariant),
            formal);
        Assert.Contains("foreach ($publishRestoreProject", formal, StringComparison.Ordinal);
        Assert.Contains("'restore', $publishRestoreProject", formal, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Matches(formal, @"'restore', \$solution", RegexOptions.CultureInvariant).Count);
        Assert.Contains("& $dotnet build $solution", developer, StringComparison.Ordinal);
        Assert.Contains("& $dotnet restore $appProject --runtime win-x64", developer, StringComparison.Ordinal);
        Assert.DoesNotContain("& $dotnet restore $solution --runtime win-x64", developer, StringComparison.Ordinal);
        Assert.Contains("--no-restore", developer, StringComparison.Ordinal);
    }

    [Fact]
    public void HeavyPipelinesShareOneNonNestedMutexAndUseLowPriorityChildren()
    {
        var formal = ReadScript("Build-MuhunMcsvFormalRelease.ps1");
        var developer = ReadScript("build-and-publish.ps1");
        var android = ReadScript("Build-MuhunMcsvAndroid.ps1");
        var isolated = ReadScript("Invoke-IsolatedDesktopProcess.ps1");
        const string mutexName = "Local\\Muhun.Mcsv.HeavyBuild.v1";

        foreach (var source in new[] { formal, developer, android })
        {
            Assert.Contains(mutexName, source, StringComparison.Ordinal);
            Assert.Contains("WaitOne(0)", source, StringComparison.Ordinal);
            Assert.Contains("AbandonedMutexException", source, StringComparison.Ordinal);
            Assert.Contains("ReleaseMutex()", source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(mutexName, isolated, StringComparison.Ordinal);
        Assert.Contains("BelowNormalPriorityClass = 0x00004000", isolated, StringComparison.Ordinal);
        Assert.Contains("CreateNoWindow | BelowNormalPriorityClass", isolated, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPipelineKeepsQualityGatesWithoutGlobalClean()
    {
        var source = ReadScript("Build-MuhunMcsvAndroid.ps1");

        Assert.Contains("'--max-workers=2'", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(?m)^\s*'clean',?\s*$", RegexOptions.CultureInvariant), source);
        Assert.Contains("'testDebugUnitTest'", source, StringComparison.Ordinal);
        Assert.Contains("'lintRelease'", source, StringComparison.Ordinal);
        Assert.Contains("'assembleRelease'", source, StringComparison.Ordinal);
        Assert.Contains("app\\build\\outputs\\apk\\release\\app-release.apk", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Delete($expectedOutput)", source, StringComparison.Ordinal);
        Assert.Contains("APK is missing required v2/v3/v4 release signatures", source, StringComparison.Ordinal);
        Assert.Contains("versionCode='$VersionCode'", source, StringComparison.Ordinal);
    }

    private static string ReadScript(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", name));

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
