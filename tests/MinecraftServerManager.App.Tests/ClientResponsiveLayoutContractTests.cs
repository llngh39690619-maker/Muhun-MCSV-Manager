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
        var embeddedDownloadDocument = XDocument.Load(TestRepositoryPaths.AppSource(
            "Dialogs",
            "ClientContentDownloadCenterWindow.xaml"));
        XNamespace controls = "clr-namespace:MinecraftServerManager.App.Controls";

        var panels = document.Descendants(controls + "ResponsiveWrapPanel")
            .Concat(embeddedDownloadDocument.Descendants(controls + "ResponsiveWrapPanel"))
            .ToArray();

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
        var navigation = Assert.Single(
            document.Descendants(presentation + "ScrollViewer"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogDiscoveryNavigation");
        var navigationHost = Assert.Single(
            document.Descendants(presentation + "Border"),
            element => (string?)element.Attribute(xaml + "Name") == "ClientWorkspaceNavigation");

        Assert.Equal(
            ["*", "14", "356"],
            body.Element(presentation + "Grid.ColumnDefinitions")!
                .Elements(presentation + "ColumnDefinition")
                .Select(column => (string)column.Attribute("Width")!)
                .ToArray());
        Assert.Null(body.Attribute("Visibility"));
        Assert.Equal(
            "{Binding IsBrowsableCatalogSource, Converter={StaticResource BoolToVisibility}}",
            (string?)results.Attribute("Visibility"));
        Assert.Equal(
            "{Binding IsCatalogDetailOpen, Converter={StaticResource BoolToVisibility}}",
            (string?)details.Attribute("Visibility"));
        Assert.Equal("2", (string?)details.Attribute("Grid.Column"));
        Assert.Equal("Stretch", (string?)details.Attribute("VerticalAlignment"));
        var catalogScroll = Assert.Single(
            body.Elements(presentation + "ScrollViewer"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogPageScrollViewer");
        Assert.Equal("0", (string?)catalogScroll.Attribute("Grid.Column"));
        Assert.Contains(
            details.Descendants(presentation + "ScrollViewer"),
            element => (string?)element.Attribute("VerticalScrollBarVisibility") == "Auto");
        Assert.Contains(body, filters.Ancestors());

        Assert.Equal(
            "{Binding IsCatalogPage, Converter={StaticResource BoolToVisibility}}",
            (string?)navigation.Attribute("Visibility"));
        var instanceList = Assert.Single(
            navigation.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogDiscoveryInstanceList");
        Assert.Equal("{Binding Instances}", (string?)instanceList.Attribute("ItemsSource"));
        Assert.Equal("{Binding SelectedInstance}", (string?)instanceList.Attribute("SelectedItem"));
        var navigationSource = navigation.ToString();
        Assert.Contains("{Binding NewInstanceCommand}", navigationSource, StringComparison.Ordinal);
        Assert.Contains("{Binding OpenCatalogCommand}", navigationSource, StringComparison.Ordinal);
        Assert.Contains("{Binding OpenContentDownloadCommand}", navigationSource, StringComparison.Ordinal);
        Assert.Contains("MinecraftClientContentKind.Mod", navigationSource, StringComparison.Ordinal);
        Assert.Contains("MinecraftClientContentKind.ResourcePack", navigationSource, StringComparison.Ordinal);
        Assert.Contains("MinecraftClientContentKind.ShaderPack", navigationSource, StringComparison.Ordinal);
        Assert.Contains("{Binding OpenClientSettingsCommand}", navigationSource, StringComparison.Ordinal);
        Assert.Contains("{Binding OpenClientJavaSettingsCommand}", navigationSource, StringComparison.Ordinal);
        Assert.Contains("{Binding BrowseAllCatalogCommand}", navigationSource, StringComparison.Ordinal);
        Assert.Contains("{Binding ToggleDiscoveryDownloadQueueCommand}", navigationSource, StringComparison.Ordinal);
        Assert.Contains("{Binding DiscoveryDownloadJobCount}", navigationSource, StringComparison.Ordinal);
        Assert.Contains("{Binding HasDiscoveryDownloadJobs", navigationSource, StringComparison.Ordinal);
        Assert.Empty(document.Descendants(presentation + "ColumnDefinition.Style"));
        var navigationHostSource = navigationHost.ToString();
        Assert.Contains("Property=\"Width\" Value=\"202\"", navigationHostSource, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding IsCatalogPage}\" Value=\"True\"", navigationHostSource, StringComparison.Ordinal);
        Assert.Contains("Property=\"Width\" Value=\"246\"", navigationHostSource, StringComparison.Ordinal);

        Assert.Contains(
            filters.Descendants(presentation + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains(
                "CatalogSearchText",
                StringComparison.Ordinal) == true);
        var searchButton = Assert.Single(
            filters.Descendants(presentation + "Button"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogSearchButton");
        Assert.Equal("{Binding SearchCatalogCommand}", (string?)searchButton.Attribute("Command"));
        Assert.Equal(
            "Auto",
            (string?)searchButton.Parent!
                .Element(presentation + "Grid.ColumnDefinitions")!
                .Elements(presentation + "ColumnDefinition")
                .Last()
                .Attribute("Width"));
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
        Assert.Equal(
            ["150", "150", "174"],
            new[] { "CatalogGameVersionFilter", "CatalogLoaderFilter", "CatalogSortFilter" }
                .Select(name => Assert.Single(
                    filters.Descendants(presentation + "StackPanel"),
                    element => (string?)element.Attribute(xaml + "Name") == name))
                .Select(element => (string)element.Attribute("Width")!)
                .ToArray());

        var resultList = Assert.Single(
            results.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogResultsList");
        var resultPanel = Assert.Single(resultList.Descendants(controls + "ResponsiveWrapPanel"));
        Assert.Equal("280", (string?)resultPanel.Attribute("MinItemWidth"));
        Assert.Equal("138", (string?)resultPanel.Attribute("ItemHeight"));
        Assert.Equal("2", (string?)resultPanel.Attribute("MaximumColumns"));
        Assert.Contains(
            resultList.Descendants(presentation + "Image"),
            element => (string?)element.Attribute(infrastructure + "LocalImageThumbnail.SourcePath")
                       == "{Binding CardImagePath}");
        Assert.Contains(
            details.Descendants(presentation + "ComboBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding CatalogVersions}");
        Assert.Empty(details.Descendants(presentation + "Slider"));
        Assert.Contains("SelectedCatalogVersion.PackVersionDisplay", details.ToString(), StringComparison.Ordinal);
        Assert.Contains("SelectedCatalogVersion.GameVersionDisplay", details.ToString(), StringComparison.Ordinal);
        Assert.Contains("SelectedCatalogVersion.LoaderDisplay", details.ToString(), StringComparison.Ordinal);

        var pagination = Assert.Single(
            results.Descendants(presentation + "StackPanel"),
            element => (string?)element.Attribute(xaml + "Name") == "CatalogPagination");
        Assert.Equal(
            "{Binding ShowsCatalogPagination, Converter={StaticResource BoolToVisibility}}",
            (string?)pagination.Attribute("Visibility"));
        Assert.Contains(
            pagination.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding PreviousCatalogPageCommand}");
        Assert.Contains(
            pagination.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding NextCatalogPageCommand}");
        var pageItems = Assert.Single(pagination.Descendants(presentation + "ItemsControl"));
        Assert.Equal("{Binding CatalogPaginationItems}", (string?)pageItems.Attribute("ItemsSource"));
        var pageButton = Assert.Single(
            pageItems.Descendants(presentation + "Button"),
            element => ((string?)element.Attribute("Command"))?.Contains(
                "GoToCatalogPageCommand",
                StringComparison.Ordinal) == true);
        Assert.Equal("{Binding PageNumber}", (string?)pageButton.Attribute("CommandParameter"));
        Assert.Equal("{Binding CanNavigate}", (string?)pageButton.Attribute("IsEnabled"));
        Assert.Contains("Binding IsCurrent", pageButton.ToString(), StringComparison.Ordinal);

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
