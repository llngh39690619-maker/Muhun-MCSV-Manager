using System.IO;

namespace MinecraftServerManager.App.Tests;

public sealed class PendingProductServiceProjectionContractTests
{
    [Fact]
    public void EveryAuthoritativeProjectionRestoresPendingRowsControlChannel()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.App",
            "ViewModels",
            "MainWindowViewModel.cs"));
        var methodStart = source.IndexOf(
            "private void ApplyProductServiceSnapshot(",
            StringComparison.Ordinal);
        var nextMethod = source.IndexOf(
            "private static void ApplyProductServiceStatus(",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && nextMethod > methodStart);
        var method = source[methodStart..nextMethod];

        Assert.Contains(
            "pending.IsControlChannelAvailable = false;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!_pendingProductServiceImports.TryAdd(model.Id, 0))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "snapshot.IsComplete &&",
            method,
            StringComparison.Ordinal);
        Assert.Contains(
            "\n            server.IsControlChannelAvailable = IsProductServiceConnected;\n" +
            "            ApplyProductServiceStatus(server, projection.Status);",
            method.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
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
