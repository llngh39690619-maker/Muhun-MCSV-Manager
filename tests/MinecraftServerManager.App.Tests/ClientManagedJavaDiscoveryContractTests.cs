using System.IO;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientManagedJavaDiscoveryContractTests
{
    [Fact]
    public void ResolveJava_OnlySearchesDirectManagedTemurinDirectoriesAndNeverStaging()
    {
        var source = File.ReadAllText(
                TestRepositoryPaths.AppSource("ViewModels", "ClientWorkspaceViewModel.cs"))
            .ReplaceLineEndings("\n");
        var methodStart = source.IndexOf(
            "private async Task<string> ResolveJavaAsync(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "ResolveJavaAsync must remain present.");
        var methodEnd = source.IndexOf(
            "\n    private ",
            methodStart + 1,
            StringComparison.Ordinal);
        var method = source[methodStart..(methodEnd < 0 ? source.Length : methodEnd)];

        Assert.Matches(
            new Regex(
                "Directory\\.EnumerateDirectories\\(\\s*_paths\\.ClientRuntimes,"
                + "\\s*\"temurin-\\*\",\\s*SearchOption\\.TopDirectoryOnly\\)",
                RegexOptions.CultureInvariant),
            method);
        Assert.Contains(
            "Path.Combine(directory, \"bin\", \"java.exe\")",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SearchOption.AllDirectories", method, StringComparison.Ordinal);
        Assert.DoesNotContain(".staging", method, StringComparison.OrdinalIgnoreCase);
    }
}
