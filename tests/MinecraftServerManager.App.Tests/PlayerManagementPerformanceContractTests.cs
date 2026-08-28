using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class PlayerManagementPerformanceContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void PlayerList_ExplicitlyUsesRecyclingVirtualization()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var playerList = Assert.Single(document.Descendants(Presentation + "ListBox"), element =>
            (string?)element.Attribute("ItemsSource") == "{Binding SelectedServer.VisiblePlayers}");

        Assert.Equal("True", (string?)playerList.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)playerList.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("True", (string?)playerList.Attribute("ScrollViewer.CanContentScroll"));
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
