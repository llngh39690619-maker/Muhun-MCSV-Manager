using System.IO;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientContentDownloadCenterWindowContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Window_IsAResizableSixteenByNineDarkModelessSurface()
    {
        var document = LoadWindow();
        var window = Assert.IsType<XElement>(document.Root);

        Assert.Equal("1280", (string?)window.Attribute("Width"));
        Assert.Equal("720", (string?)window.Attribute("Height"));
        Assert.Equal("1024", (string?)window.Attribute("MinWidth"));
        Assert.Equal("576", (string?)window.Attribute("MinHeight"));
        Assert.Equal("CanResizeWithGrip", (string?)window.Attribute("ResizeMode"));
        Assert.Equal("{StaticResource AppWindowStyle}", (string?)window.Attribute("Style"));
        Assert.Equal("False", (string?)window.Attribute("ShowInTaskbar"));
        Assert.Equal("{Binding CloseContentDownloadCommand}", (string?)window.Attribute("Tag"));
        Assert.Equal("OnWindowClosing", (string?)window.Attribute("Closing"));

        var codeBehind = File.ReadAllText(WindowCodeBehindPath());
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("static ClientContentDownloadCenterWindow", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialOpen_IsTrackedAsAContentBrowseLifetimeTask()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "ClientWorkspaceViewModel.cs"));

        Assert.Contains(
            "parameter => _contentDownloadBrowseTask =\r\n                RunGuardedAsync(() => OpenContentDownloadAsync(parameter))",
            source.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "InstallContentDownloadCommand.NotifyCanExecuteChanged();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Header_ExposesAllThreeContentKindsThroughTheFixedTargetCommand()
    {
        var tabs = FindNamedElement(LoadWindow(), "TabControl", "ContentDownloadTabs");
        var tabItems = tabs.Elements(Presentation + "TabItem").ToArray();

        Assert.Equal(3, tabItems.Length);
        Assert.Equal(
            ["Mod", "ResourcePack", "ShaderPack"],
            tabItems
                .Select(tab => Assert.Single(tab.Descendants(Presentation + "Button")))
                .Select(button => (string?)button.Attribute("CommandParameter") ?? string.Empty)
                .ToArray());
        Assert.All(
            tabItems.Select(tab => Assert.Single(tab.Descendants(Presentation + "Button"))),
            button => Assert.Equal(
                "{Binding SelectContentDownloadKindCommand}",
                (string?)button.Attribute("Command")));
        Assert.Equal(
            [
                "{Binding IsModContentDownload, Mode=OneWay}",
                "{Binding IsResourcePackContentDownload, Mode=OneWay}",
                "{Binding IsShaderPackContentDownload, Mode=OneWay}",
            ],
            tabItems.Select(tab => (string?)tab.Attribute("IsSelected") ?? string.Empty).ToArray());
        Assert.Equal(
            "OnContentDownloadTabSelectionChanged",
            (string?)tabs.Attribute("SelectionChanged"));
    }

    [Fact]
    public void MainSurface_HasAResultListAndAnIndependentScrollableDetailColumn()
    {
        var document = LoadWindow();
        var mainSplit = FindNamedElement(document, "Grid", "ContentDownloadMainSplit");
        var columns = mainSplit
            .Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width") ?? string.Empty)
            .ToArray();

        Assert.Equal(["3*", "10", "2*"], columns);

        var results = FindNamedElement(document, "ListBox", "ContentDownloadResultsList");
        Assert.Equal("{Binding ContentDownloadResults}", (string?)results.Attribute("ItemsSource"));
        Assert.Equal(
            "{Binding SelectedContentDownloadProject}",
            (string?)results.Attribute("SelectedItem"));
        Assert.Equal("Auto", (string?)results.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
        Assert.Equal("OnResultsScrollChanged", (string?)results.Attribute("ScrollViewer.ScrollChanged"));
        Assert.Equal(
            "{Binding LoadMoreContentDownloadCommand}",
            (string?)results.Attribute("Tag"));
        Assert.Equal("True", (string?)results.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.DoesNotContain(results.Ancestors(), ancestor => ancestor.Name == Presentation + "ScrollViewer");

        var detailsPanel = FindNamedElement(document, "Border", "ContentDownloadDetailsPanel");
        Assert.Equal("2", (string?)detailsPanel.Attribute("Grid.Column"));
        var detailsScrollViewer = FindNamedElement(
            document,
            "ScrollViewer",
            "ContentDownloadDetailsScrollViewer");
        Assert.Same(detailsPanel, detailsScrollViewer.Parent);
        Assert.Equal("Auto", (string?)detailsScrollViewer.Attribute("VerticalScrollBarVisibility"));
    }

    [Fact]
    public void QueueAndInstallBar_RemainOutsideAllScrollableContent()
    {
        var document = LoadWindow();
        var root = FindNamedElement(document, "Grid", "ContentDownloadRoot");
        var rows = root
            .Element(Presentation + "Grid.RowDefinitions")!
            .Elements(Presentation + "RowDefinition")
            .Select(row => (string?)row.Attribute("Height") ?? string.Empty)
            .ToArray();

        Assert.Equal(["Auto", "Auto", "*", "Auto", "Auto"], rows);

        var queue = FindNamedElement(document, "Border", "ContentDownloadQueuePanel");
        var fixedBar = FindNamedElement(document, "Border", "FixedContentDownloadBar");
        Assert.Equal("3", (string?)queue.Attribute("Grid.Row"));
        Assert.Equal("4", (string?)fixedBar.Attribute("Grid.Row"));
        Assert.Same(root, queue.Parent);
        Assert.Same(root, fixedBar.Parent);
        Assert.DoesNotContain(queue.Ancestors(), ancestor => ancestor.Name == Presentation + "ScrollViewer");
        Assert.DoesNotContain(fixedBar.Ancestors(), ancestor => ancestor.Name == Presentation + "ScrollViewer");

        AssertButtonCommand(fixedBar, "{Binding OpenSelectedContentProjectPageCommand}");
        AssertButtonCommand(fixedBar, "{Binding OpenContentFallbackCommand}");
        AssertButtonCommand(fixedBar, "{Binding InstallContentDownloadCommand}");

        var progress = Assert.Single(fixedBar.Descendants(Presentation + "ProgressBar"));
        Assert.Equal(
            "{Binding ContentDownloadQueueProgressValue, Mode=OneWay}",
            (string?)progress.Attribute("Value"));
        Assert.Equal(
            "{Binding IsContentDownloadQueueProgressIndeterminate, Mode=OneWay}",
            (string?)progress.Attribute("IsIndeterminate"));
    }

    [Fact]
    public void ToolbarDetailsAndQueue_ExposeTheRequiredBindings()
    {
        var source = File.ReadAllText(WindowXamlPath());
        var requiredBindings = new[]
        {
            "ContentDownloadSearchText",
            "ContentDownloadLoaders",
            "SelectedContentDownloadLoader",
            "ContentDownloadCategories",
            "SelectedContentDownloadCategory",
            "ContentDownloadSortOptions",
            "SelectedContentDownloadSort",
            "SearchContentDownloadCommand",
            "ContentDownloadVersions",
            "SelectedContentDownloadVersion",
            "ContentDownloadDependencies",
            "ContentDownloadFallbacks",
            "ContentDownloadStatusText",
            "ContentDownloadTargetSummary",
            "HasMoreContentDownloadResults",
            "ContentDownloadJobs",
            "HasContentDownloadJobs",
            "IsContentDownloadQueueExpanded",
            "ToggleContentDownloadQueueCommand",
            "ClearCompletedContentDownloadJobsCommand",
            "ContentDownloadQueueSummary",
            "IsContentDownloadBusy"
        };

        Assert.All(
            requiredBindings,
            binding => Assert.Contains($"Binding {binding}", source, StringComparison.Ordinal));

        var codeBehind = File.ReadAllText(WindowCodeBehindPath());
        Assert.Contains("LoadMoreThreshold", codeBehind, StringComparison.Ordinal);
        Assert.Contains("e.ExtentHeight - e.VerticalOffset - e.ViewportHeight", codeBehind, StringComparison.Ordinal);
        Assert.Contains("command.CanExecute(null)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("command.Execute(null)", codeBehind, StringComparison.Ordinal);
    }

    private static void AssertButtonCommand(XElement parent, string command) =>
        Assert.Contains(
            parent.Descendants(Presentation + "Button"),
            button => string.Equals((string?)button.Attribute("Command"), command, StringComparison.Ordinal));

    private static XElement FindNamedElement(XDocument document, string localName, string name) =>
        document
            .Descendants(Presentation + localName)
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                name,
                StringComparison.Ordinal));

    private static XDocument LoadWindow() => XDocument.Load(WindowXamlPath());

    private static string WindowXamlPath() => TestRepositoryPaths.AppSource(
        "Dialogs",
        "ClientContentDownloadCenterWindow.xaml");

    private static string WindowCodeBehindPath() => TestRepositoryPaths.AppSource(
        "Dialogs",
        "ClientContentDownloadCenterWindow.xaml.cs");
}
