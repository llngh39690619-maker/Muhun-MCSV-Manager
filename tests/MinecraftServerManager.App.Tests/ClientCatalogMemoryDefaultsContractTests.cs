using System.IO;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientCatalogMemoryDefaultsContractTests
{
    [Fact]
    public void CatalogInstallSnapshot_UsesGlobalMemoryDefaults()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "ClientWorkspaceViewModel.cs"));
        var method = ExtractMethod(
            source,
            "private async Task InstallSelectedCatalogPackAsync()",
            "private void OpenSelectedCurseForgeProject");

        Assert.Contains("var defaults = _getGlobalDefaults();", method, StringComparison.Ordinal);
        Assert.Contains("defaults.MemoryMode,", method, StringComparison.Ordinal);
        Assert.Contains("defaults.MinimumMemoryMb,", method, StringComparison.Ordinal);
        Assert.Contains("defaults.MaximumMemoryMb,", method, StringComparison.Ordinal);
        Assert.DoesNotContain("\n            MemoryMode,", method, StringComparison.Ordinal);
        Assert.DoesNotContain("\n            MinimumMemoryMb,", method, StringComparison.Ordinal);
        Assert.DoesNotContain("\n            MaximumMemoryMb,", method, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = normalized.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method marker: {startMarker}");
        var end = normalized.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing method boundary: {endMarker}");
        return normalized[start..end];
    }
}
