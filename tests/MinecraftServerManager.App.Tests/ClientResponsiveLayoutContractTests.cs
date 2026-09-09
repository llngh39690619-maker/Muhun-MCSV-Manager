using System.IO;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientResponsiveLayoutContractTests
{
    [Fact]
    public void ClientWorkspace_UsesTheTestedControlsPanelForEveryResponsiveSurface()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        XNamespace controls = "clr-namespace:MinecraftServerManager.App.Controls";

        var panels = document.Descendants(controls + "ResponsiveWrapPanel").ToArray();

        Assert.Equal(3, panels.Length);
        Assert.All(panels, panel => Assert.NotNull(panel.Attribute("MinItemWidth")));
        Assert.All(panels, panel => Assert.Null(panel.Attribute("MinimumItemWidth")));
        Assert.False(File.Exists(TestRepositoryPaths.AppSource(
            "Infrastructure",
            "ResponsiveWrapPanel.cs")));
    }

    [Fact]
    public void ClientCatalog_UsesTheApprovedContentDiscoveryLayout()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace controls = "clr-namespace:MinecraftServerManager.App.Controls";
        XNamespace infrastructure = "clr-namespace:MinecraftServerManager.App.Infrastructure";

        var catalog = Assert.Single(
            document.Descendants(presentation + "Grid"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogPageLayout");
        var filters = Assert.Single(
            catalog.Descendants(presentation + "Border"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogDiscoveryFilters");
        var body = Assert.Single(
            catalog.Descendants(presentation + "Grid"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogDiscoveryBody");
        var results = Assert.Single(
            body.Descendants(presentation + "Border"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogDiscoveryResultsPanel");
        var details = Assert.Single(
            body.Descendants(presentation + "Border"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogDiscoveryDetailPanel");

        Assert.Equal(
            ["*", "14", "356"],
            body.Element(presentation + "Grid.ColumnDefinitions")!
                .Elements(presentation + "ColumnDefinition")
                .Select(column => (string)column.Attribute("Width")!)
                .ToArray());
        Assert.Equal(
            "{Binding IsBrowsableCatalogSource, Converter={StaticResource BoolToVisibility}}",
            (string?)body.Attribute("Visibility"));
        Assert.Equal(
            "{Binding IsCatalogDetailOpen, Converter={StaticResource BoolToVisibility}}",
            (string?)details.Attribute("Visibility"));

        Assert.Contains(
            filters.Descendants(presentation + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains(
                "CatalogSearchText",
                StringComparison.Ordinal) == true);
        Assert.Equal(
            ["modrinth", "curseforge", "ftb"],
            filters.Descendants(presentation + "Button")
                .Where(element => (string?)element.Attribute("Command")
                                  == "{Binding SelectCatalogSourceCommand}")
                .Select(element => (string)element.Attribute("CommandParameter")!)
                .ToArray());
        Assert.Contains(
            filters.Descendants(presentation + "ComboBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding CatalogGameVersions}");
        Assert.Contains(
            filters.Descendants(presentation + "ComboBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding CatalogLoaders}");
        Assert.Contains(
            filters.Descendants(presentation + "ComboBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding CatalogSortOptions}");

        var resultList = Assert.Single(
            results.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogResultsList");
        var resultPanel = Assert.Single(resultList.Descendants(controls + "ResponsiveWrapPanel"));
        Assert.Equal("330", (string?)resultPanel.Attribute("MinItemWidth"));
        Assert.Equal("162", (string?)resultPanel.Attribute("ItemHeight"));
        Assert.Equal("2", (string?)resultPanel.Attribute("MaximumColumns"));
        Assert.Contains(
            resultList.Descendants(presentation + "Image"),
            element => (string?)element.Attribute(infrastructure + "LocalImageThumbnail.SourcePath")
                       == "{Binding CardImagePath}");
        Assert.Contains(
            details.Descendants(presentation + "ComboBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding CatalogVersions}");

        Assert.NotNull(document.Descendants(presentation + "Border").SingleOrDefault(
            element => (string?)element.Attribute(xaml + "Name") == "ClientLauncherHero"));
    }

    [Fact]
    public void CatalogPreview_FailsClosedUnlessDownloadedArtworkIsDecodedBeforeCapture()
    {
        var app = File.ReadAllText(TestRepositoryPaths.AppSource("App.xaml.cs"));
        var thumbnail = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Infrastructure",
            "LocalImageThumbnail.cs"));

        Assert.Contains("WaitForThumbnailRenderingAsync(applicationWindow)", app, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource(TimeSpan.FromSeconds(15))", app, StringComparison.Ordinal);
        Assert.Contains("LoadForDiagnosticsAsync(image, timeout.Token)", app, StringComparison.Ordinal);
        Assert.Contains("decodedCount == 0", app, StringComparison.Ordinal);
        Assert.Contains("image.Source = source", thumbnail, StringComparison.Ordinal);

        var waitIndex = app.IndexOf(
            "await WaitForThumbnailRenderingAsync(applicationWindow)",
            StringComparison.Ordinal);
        var renderIndex = app.IndexOf(
            "RenderPreview(applicationWindow, renderClientCatalogPreviewPath)",
            waitIndex,
            StringComparison.Ordinal);
        Assert.True(waitIndex >= 0);
        Assert.True(renderIndex > waitIndex);
    }
}
