using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.App.Tests;

public sealed class ConsoleDiagnosticOutputContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void MainWindow_UsesStableTabKeysAndHidesDiagnosticsForAnUncheckedServer()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var tabControl = Assert.Single(document.Descendants(Presentation + "TabControl"));
        Assert.Equal("Tag", (string?)tabControl.Attribute("SelectedValuePath"));
        Assert.Equal(
            "{Binding SelectedWorkspaceTabKey, Mode=TwoWay}",
            (string?)tabControl.Attribute("SelectedValue"));

        var tabs = tabControl.Elements(Presentation + "TabItem").ToArray();
        Assert.Equal(tabs.Length, tabs.Select(tab => (string?)tab.Attribute("Tag")).Distinct().Count());
        var consoleIndex = Array.FindIndex(tabs, tab => (string?)tab.Attribute("Tag") == "Console");
        var diagnosticIndex = Array.FindIndex(tabs, tab => (string?)tab.Attribute("Tag") == "Diagnostics");
        Assert.Equal(consoleIndex + 1, diagnosticIndex);

        var diagnostics = tabs[diagnosticIndex];
        Assert.Equal(
            "{Binding SelectedServer.SeparateDiagnosticOutput, Converter={StaticResource BoolToVisibility}}",
            (string?)diagnostics.Attribute("Visibility"));
        Assert.Equal(
            "{Binding SelectedServer.DiagnosticsTabHeader}",
            (string?)diagnostics.Attribute("Header"));
    }

    [Fact]
    public void DiagnosticPane_IsVirtualizedAutoScrollingAndHasAnEmptyState()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var template = Assert.Single(document.Descendants(Presentation + "DataTemplate"), element =>
            (string?)element.Attribute(x + "Key") == "DiagnosticPaneTemplate");
        var list = Assert.Single(template.Descendants(Presentation + "ListBox"));

        Assert.Equal("{Binding DiagnosticLines}", (string?)list.Attribute("ItemsSource"));
        Assert.Equal("True", (string?)list.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)list.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("True", (string?)list.Attribute("ScrollViewer.CanContentScroll"));
        Assert.Equal("OnConsoleListLoaded", (string?)list.Attribute("Loaded"));
        Assert.Equal("OnConsoleListUnloaded", (string?)list.Attribute("Unloaded"));
        Assert.Equal("OnConsoleListDataContextChanged", (string?)list.Attribute("DataContextChanged"));
        Assert.Contains(template.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{DynamicResource L10n.main.diagnostics.empty}");
    }

    [Fact]
    public void ServerSettings_ExposeImmediatePerServerDiagnosticSeparation()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var checkbox = Assert.Single(document.Descendants(Presentation + "CheckBox"), element =>
            (string?)element.Attribute("Content")
            == "{DynamicResource L10n.main.settings.separateDiagnostics}");

        Assert.Equal(
            "{Binding SelectedServer.SeparateDiagnosticOutput, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            (string?)checkbox.Attribute("IsChecked"));
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text")
            == "{DynamicResource L10n.main.settings.separateDiagnosticsHint}");
        Assert.Contains(
            "自動儲存",
            ProductLocalizationCatalog.GetDocument("zh-TW")
                .Strings["main.settings.separateDiagnosticsHint"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void SplitDiagnostics_OnlyMaterializeWhenBothDisplayedServersOptIn()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var bindings = document.Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.True(bindings.Count(value => value.Contains(
            "IsSplitDiagnosticOutputVisible",
            StringComparison.Ordinal)) >= 3);
    }

    [Fact]
    public void JavaRuntimeTab_RemainsBrowsableForServiceOwnedServersAndHidesLocalMutationControls()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var javaTab = Assert.Single(document.Descendants(Presentation + "TabItem"), element =>
            (string?)element.Attribute("Tag") == MainWindowViewModel.JavaRuntimeWorkspaceTabKey);

        Assert.Null(javaTab.Attribute("IsEnabled"));
        Assert.Contains(javaTab.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{DynamicResource L10n.service.readOnly.java}"
            && (string?)element.Attribute("Visibility")
            == "{Binding IsProductServiceRuntime, Converter={StaticResource BoolToVisibility}}");

        var localControls = Assert.Single(javaTab.Descendants(Presentation + "StackPanel"), element =>
            (string?)element.Attribute("Visibility")
            == "{Binding IsProductServiceRuntime, Converter={StaticResource InverseBoolToVisibility}}");
        Assert.Contains(localControls.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding DownloadJavaCommand}");
        Assert.Contains(localControls.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding RefreshJavaCommand}");
    }

    [Fact]
    public void AddonsTab_UsesServiceFileBrowseCapabilityWithoutUnlockingLocalConfiguration()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var addonsTab = Assert.Single(document.Descendants(Presentation + "TabItem"), element =>
            (string?)element.Attribute("Tag") == MainWindowViewModel.AddonsWorkspaceTabKey);

        Assert.Equal(
            "{Binding CanBrowseSelectedServerFiles}",
            (string?)addonsTab.Attribute("IsEnabled"));
        var updateBackups = Assert.Single(addonsTab.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding OpenModpackUpdateBackupsCommand}");
        Assert.Equal(
            "{Binding CanEditSelectedLocalConfiguration}",
            (string?)updateBackups.Attribute("IsEnabled"));
    }

    private static string GetAppSourcePath(
        string relativePath,
        [CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "MinecraftServerManager.App",
            relativePath));
}
