using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

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
    public void PreviewTypography_UsesReadableCompactTabsSelectorsAndActions()
    {
        var document = LoadWindow();
        var styles = document.Descendants(Presentation + "Style").ToArray();

        XElement FindStyle(string key) => styles.Single(style => string.Equals(
            (string?)style.Attribute(Xaml + "Key"),
            key,
            StringComparison.Ordinal));

        static string SetterValue(XElement style, string property) =>
            (string?)style
                .Elements(Presentation + "Setter")
                .Single(setter => string.Equals(
                    (string?)setter.Attribute("Property"),
                    property,
                    StringComparison.Ordinal))
                .Attribute("Value") ?? string.Empty;

        var tabStyle = FindStyle("DownloadCenterTabHeaderButton");
        Assert.Equal("{StaticResource {x:Type Button}}", (string?)tabStyle.Attribute("BasedOn"));
        Assert.Equal("15", SetterValue(tabStyle, "FontSize"));
        Assert.Equal("0", SetterValue(tabStyle, "MinHeight"));
        Assert.Equal("8,3", SetterValue(tabStyle, "Padding"));

        var actionStyle = FindStyle("DownloadCenterActionButton");
        Assert.Equal("{StaticResource {x:Type Button}}", (string?)actionStyle.Attribute("BasedOn"));
        Assert.Equal("14", SetterValue(actionStyle, "FontSize"));
        Assert.Equal("38", SetterValue(actionStyle, "MinHeight"));

        var primaryStyle = FindStyle("DownloadCenterPrimaryActionButton");
        Assert.Equal("14", SetterValue(primaryStyle, "FontSize"));
        Assert.Equal("38", SetterValue(primaryStyle, "MinHeight"));
        Assert.Equal("142", SetterValue(primaryStyle, "MinWidth"));

        var tabs = FindNamedElement(document, "TabControl", "ContentDownloadTabs");
        Assert.All(
            tabs.Descendants(Presentation + "Button"),
            button =>
            {
                Assert.Equal(
                    "{StaticResource DownloadCenterTabHeaderButton}",
                    (string?)button.Attribute("Style"));
                Assert.Equal(
                    "{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type TabItem}}}",
                    (string?)button.Attribute("Foreground"));
                Assert.StartsWith(
                    "{DynamicResource L10n.client.vm.content.kind.",
                    (string?)button.Attribute("AutomationProperties.Name"),
                    StringComparison.Ordinal);
            });

        var versionSelector = FindNamedElement(
            document,
            "Border",
            "FixedContentDownloadVersionSelector");
        var versionCombo = Assert.Single(versionSelector.Descendants(Presentation + "ComboBox"));
        Assert.Equal("14", (string?)versionCombo.Attribute("FontSize"));
        Assert.Equal("38", (string?)versionCombo.Attribute("MinHeight"));
    }

    [Theory]
    [InlineData(MinecraftClientContentKind.ResourcePack, 1)]
    [InlineData(MinecraftClientContentKind.ShaderPack, 2)]
    public async Task InitialOpen_PreservesTheContentKindRequestedByTheCard(
        MinecraftClientContentKind requestedKind,
        int expectedTabIndex)
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        ClientWorkspaceViewModel? workspace = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(directory.Path);
                paths.EnsureCreated();
                workspace = new ClientWorkspaceViewModel(
                    paths,
                    static () => new NewMinecraftClientDefaultsSettings());
                SetPrivateProperty(workspace, nameof(ClientWorkspaceViewModel.IsContentDownloadOpen), true);
                SetPrivateField(workspace, "_contentDownloadTargetInstanceId", Guid.NewGuid());
                SetPrivateProperty(workspace, nameof(ClientWorkspaceViewModel.ContentDownloadKind), requestedKind);

                var window = new ClientContentDownloadCenterWindow
                {
                    DataContext = workspace,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20_000,
                    Top = -20_000,
                    ShowInTaskbar = false,
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();

                    var tabs = Assert.IsType<TabControl>(window.FindName("ContentDownloadTabs"));
                    Assert.True(window.IsLoaded);
                    Assert.Equal(requestedKind, workspace.ContentDownloadKind);
                    Assert.Equal(expectedTabIndex, tabs.SelectedIndex);

                    var nextKind = requestedKind == MinecraftClientContentKind.ResourcePack
                        ? MinecraftClientContentKind.ShaderPack
                        : MinecraftClientContentKind.ResourcePack;
                    SetPrivateProperty(
                        workspace,
                        nameof(ClientWorkspaceViewModel.ContentDownloadKind),
                        nextKind);
                    Assert.Equal(nextKind, workspace.ContentDownloadKind);
                    Assert.Equal(
                        nextKind == MinecraftClientContentKind.ResourcePack ? 1 : 2,
                        tabs.SelectedIndex);

                    tabs.SelectedIndex = 0;
                    Assert.Equal(MinecraftClientContentKind.Mod, workspace.ContentDownloadKind);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            if (workspace is not null)
            {
                await workspace.DisposeAsync();
            }
        }
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
        var detailsLayout = Assert.IsType<XElement>(detailsScrollViewer.Parent);
        Assert.Same(detailsPanel, detailsLayout.Parent);
        Assert.Equal("Auto", (string?)detailsScrollViewer.Attribute("VerticalScrollBarVisibility"));

        var versionSelector = FindNamedElement(
            document,
            "Border",
            "FixedContentDownloadVersionSelector");
        var detailActions = FindNamedElement(
            document,
            "Border",
            "FixedContentDownloadDetailActions");
        Assert.Same(detailsLayout, versionSelector.Parent);
        Assert.Same(detailsLayout, detailActions.Parent);
        Assert.Equal("0", (string?)versionSelector.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)detailsScrollViewer.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)detailActions.Attribute("Grid.Row"));
        Assert.DoesNotContain(
            versionSelector.Ancestors(),
            ancestor => ancestor.Name == Presentation + "ScrollViewer");
        Assert.DoesNotContain(
            detailActions.Ancestors(),
            ancestor => ancestor.Name == Presentation + "ScrollViewer");
        AssertButtonCommand(detailActions, "{Binding OpenSelectedContentProjectPageCommand}");
        AssertButtonCommand(detailActions, "{Binding InstallContentDownloadCommand}");
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

        Assert.Equal(["Auto", "Auto", "*", "Auto"], rows);

        var queue = FindNamedElement(document, "Border", "ContentDownloadQueuePanel");
        var fixedBar = FindNamedElement(document, "Border", "FixedContentDownloadBar");
        var mainSplit = FindNamedElement(document, "Grid", "ContentDownloadMainSplit");
        Assert.Null(queue.Attribute("Grid.Row"));
        Assert.Equal("0", (string?)queue.Attribute("Grid.Column"));
        Assert.Equal("20", (string?)queue.Attribute("Panel.ZIndex"));
        Assert.Equal("Bottom", (string?)queue.Attribute("VerticalAlignment"));
        Assert.Equal("240", (string?)queue.Attribute("MaxHeight"));
        Assert.Equal("150", (string?)queue.Attribute("MinHeight"));
        Assert.Equal("True", (string?)queue.Attribute("ClipToBounds"));
        Assert.Equal(
            "{Binding IsContentDownloadQueueExpanded, Converter={StaticResource BoolToVisibility}}",
            (string?)queue.Attribute("Visibility"));
        Assert.Equal("3", (string?)fixedBar.Attribute("Grid.Row"));
        Assert.Same(mainSplit, queue.Parent);
        Assert.Same(root, fixedBar.Parent);
        Assert.DoesNotContain(queue.Ancestors(), ancestor => ancestor.Name == Presentation + "ScrollViewer");
        Assert.DoesNotContain(fixedBar.Ancestors(), ancestor => ancestor.Name == Presentation + "ScrollViewer");

        var queueList = FindNamedElement(document, "ListBox", "ContentDownloadQueueList");
        Assert.Same(queue, queueList.Ancestors().First(ancestor => ancestor.Name == Presentation + "Border"));
        Assert.Equal("Auto", (string?)queueList.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
        AssertButtonCommand(fixedBar, "{Binding ToggleContentDownloadQueueCommand}");
        AssertButtonCommand(fixedBar, "{Binding ClearCompletedContentDownloadJobsCommand}");

        var progress = Assert.Single(fixedBar.Descendants(Presentation + "ProgressBar"));
        Assert.Equal(
            "{Binding ContentDownloadQueueProgressValue, Mode=OneWay}",
            (string?)progress.Attribute("Value"));
        Assert.Equal(
            "{Binding IsContentDownloadQueueProgressIndeterminate, Mode=OneWay}",
            (string?)progress.Attribute("IsIndeterminate"));
    }

    [Theory]
    [InlineData(1280d, 720d, 1)]
    [InlineData(1280d, 720d, 10)]
    [InlineData(1024d, 576d, 1)]
    [InlineData(1024d, 576d, 10)]
    public async Task FixedAreasAndQueueDrawer_KeepTheirLayoutAtSupportedWindowSizes(
        double width,
        double height,
        int jobCount)
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        ClientWorkspaceViewModel? workspace = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(directory.Path);
                paths.EnsureCreated();
                workspace = new ClientWorkspaceViewModel(
                    paths,
                    static () => new NewMinecraftClientDefaultsSettings());
                SetPrivateProperty(workspace, nameof(ClientWorkspaceViewModel.IsContentDownloadOpen), true);
                SetPrivateField(workspace, "_contentDownloadTargetInstanceId", Guid.NewGuid());

                for (var index = 0; index < jobCount; index++)
                {
                    var job = new ClientContentInstallJobViewModel(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        $"Minecraft {index + 1}",
                        $"project-{index + 1}",
                        $"Content project {index + 1}",
                        $"version-{index + 1}",
                        $"1.{index + 1}.0",
                        "Downloading",
                        CancellationToken.None);
                    job.Report("download", "Downloading", (index + 1d) / (jobCount + 1d));
                    workspace.ContentDownloadJobs.Add(job);
                }

                workspace.IsContentDownloadQueueExpanded = false;
                var window = new ClientContentDownloadCenterWindow
                {
                    DataContext = workspace,
                    Width = width,
                    Height = height,
                    SizeToContent = SizeToContent.Manual,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20_000,
                    Top = -20_000,
                    ShowInTaskbar = false,
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();

                    var root = FindFrameworkElement(window, "ContentDownloadRoot");
                    var mainSplit = FindFrameworkElement(window, "ContentDownloadMainSplit");
                    var detailsPanel = FindFrameworkElement(window, "ContentDownloadDetailsPanel");
                    var versionSelector = FindFrameworkElement(window, "FixedContentDownloadVersionSelector");
                    var detailsScroll = FindFrameworkElement(window, "ContentDownloadDetailsScrollViewer");
                    var detailActions = FindFrameworkElement(window, "FixedContentDownloadDetailActions");
                    var queuePanel = FindFrameworkElement(window, "ContentDownloadQueuePanel");
                    var queueList = FindFrameworkElement(window, "ContentDownloadQueueList");
                    var fixedBar = FindFrameworkElement(window, "FixedContentDownloadBar");

                    var mainBefore = BoundsWithin(mainSplit, root);
                    var fixedBarBefore = BoundsWithin(fixedBar, root);
                    Assert.Equal(Visibility.Collapsed, queuePanel.Visibility);
                    Assert.True(detailsScroll.ActualHeight > 1d);
                    AssertFixedDetailRegions(
                        root,
                        detailsPanel,
                        versionSelector,
                        detailsScroll,
                        detailActions);

                    workspace.IsContentDownloadQueueExpanded = true;
                    window.UpdateLayout();

                    Assert.Equal(Visibility.Visible, queuePanel.Visibility);
                    Assert.InRange(queuePanel.ActualHeight, 149d, 240.5d);
                    Assert.True(queueList.ActualHeight > 1d);

                    var mainAfter = BoundsWithin(mainSplit, root);
                    var fixedBarAfter = BoundsWithin(fixedBar, root);
                    var queueBounds = BoundsWithin(queuePanel, root);
                    var detailsBounds = BoundsWithin(detailsPanel, root);

                    AssertRectNearlyEqual(mainBefore, mainAfter);
                    AssertRectNearlyEqual(fixedBarBefore, fixedBarAfter);
                    Assert.True(queueBounds.Left >= mainAfter.Left - 0.5d);
                    Assert.True(queueBounds.Right <= detailsBounds.Left + 0.5d);
                    Assert.True(queueBounds.Bottom <= mainAfter.Bottom + 0.5d);
                    Assert.True(mainAfter.Bottom <= fixedBarAfter.Top + 0.5d);
                    Assert.True(fixedBarAfter.Bottom <= root.ActualHeight + 0.5d);
                    AssertFixedDetailRegions(
                        root,
                        detailsPanel,
                        versionSelector,
                        detailsScroll,
                        detailActions);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            if (workspace is not null)
            {
                await workspace.DisposeAsync();
            }
        }
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

    private static FrameworkElement FindFrameworkElement(FrameworkElement root, string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(root.FindName(name));

    private static Rect BoundsWithin(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void AssertFixedDetailRegions(
        FrameworkElement root,
        FrameworkElement detailsPanel,
        FrameworkElement versionSelector,
        FrameworkElement detailsScroll,
        FrameworkElement detailActions)
    {
        var panelBounds = BoundsWithin(detailsPanel, root);
        var versionBounds = BoundsWithin(versionSelector, root);
        var scrollBounds = BoundsWithin(detailsScroll, root);
        var actionsBounds = BoundsWithin(detailActions, root);

        Assert.True(versionBounds.Top >= panelBounds.Top - 0.5d);
        Assert.True(versionBounds.Bottom <= panelBounds.Bottom + 0.5d);
        Assert.True(scrollBounds.Top >= versionBounds.Bottom - 0.5d);
        Assert.True(scrollBounds.Bottom <= actionsBounds.Top + 0.5d);
        Assert.True(actionsBounds.Top >= panelBounds.Top - 0.5d);
        Assert.True(actionsBounds.Bottom <= panelBounds.Bottom + 0.5d);
        Assert.True(detailsScroll.ActualHeight > 1d);
    }

    private static void AssertRectNearlyEqual(Rect expected, Rect actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0d, 0.5d);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0d, 0.5d);
        Assert.InRange(Math.Abs(expected.Width - actual.Width), 0d, 0.5d);
        Assert.InRange(Math.Abs(expected.Height - actual.Height), 0d, 0.5d);
    }

    private static void SetPrivateProperty<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

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
