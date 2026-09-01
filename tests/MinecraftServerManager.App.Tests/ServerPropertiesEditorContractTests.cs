using System.IO;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class ServerPropertiesEditorContractTests
{
    [Fact]
    public void RawPropertiesPanel_UsesDedicatedServiceCapableEditorBoundary()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource("MainWindow.xaml"));
        var reload = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Command") == "{Binding ReloadPropertiesCommand}");
        var editor = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "TextBox" &&
            ((string?)element.Attribute("Text"))?.Contains(
                "SelectedServer.ServerPropertiesText",
                StringComparison.Ordinal) == true);
        var editorPanel = reload.Ancestors().First(element =>
            element.Name.LocalName == "StackPanel" &&
            element.Descendants().Contains(editor));

        Assert.Equal(
            "{Binding CanEditSelectedServerProperties}",
            (string?)editor.Attribute("IsEnabled"));
        Assert.Null(editorPanel.Attribute("IsEnabled"));
        Assert.Equal(
            "{Binding SavePropertiesCommand}",
            (string?)Assert.Single(editorPanel.Descendants(), element =>
                element.Name.LocalName == "Button" &&
                (string?)element.Attribute("Command") == "{Binding SavePropertiesCommand}")
                .Attribute("Command"));
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute("IsEnabled") ==
            "{Binding CanEditSelectedLocalConfiguration}");
    }

    [Fact]
    public void ServiceSave_DoesNotReloadAndOverwriteEditedTextBeforeUsingReadyRevision()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "MainWindowViewModel.cs"));
        var saveStart = source.IndexOf("private async Task SavePropertiesAsync()", StringComparison.Ordinal);
        var saveEnd = source.IndexOf("private async Task DownloadSelectedJavaAsync()", saveStart, StringComparison.Ordinal);
        var saveMethod = source[saveStart..saveEnd];

        Assert.Contains("_serviceServerPropertiesRevisions.TryGetValue", saveMethod, StringComparison.Ordinal);
        Assert.Contains("main.vm.error.propertiesNotLoaded", saveMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("ReloadPropertiesQuietlyAsync", saveMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceReload_RemainsAvailableWithoutAReadyRevisionWhileEditAndSaveFailClosed()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "MainWindowViewModel.cs"));
        var reloadStart = source.IndexOf(
            "public bool CanReloadSelectedServerProperties",
            StringComparison.Ordinal);
        var editStart = source.IndexOf(
            "public bool CanEditSelectedServerProperties",
            reloadStart,
            StringComparison.Ordinal);
        var saveStart = source.IndexOf(
            "public bool CanSaveSelectedServerProperties",
            editStart,
            StringComparison.Ordinal);
        var browseStart = source.IndexOf(
            "public bool CanBrowseSelectedServerFiles",
            saveStart,
            StringComparison.Ordinal);
        var reloadPredicate = source[reloadStart..editStart];
        var editPredicate = source[editStart..saveStart];
        var savePredicate = source[saveStart..browseStart];

        Assert.DoesNotContain("_serviceServerPropertiesRevisions", reloadPredicate, StringComparison.Ordinal);
        Assert.Contains("_serviceServerPropertiesRevisions.ContainsKey", editPredicate, StringComparison.Ordinal);
        Assert.Contains("CanEditSelectedServerProperties", savePredicate, StringComparison.Ordinal);
        Assert.Contains("ServerState.Stopped", savePredicate, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionConflict_HasStableLocalizedReloadGuidance()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "MainWindowViewModel.cs"));
        var english = File.ReadAllText(TestRepositoryPaths.SourceProject(
            "MinecraftServerManager.Contracts",
            "Localization",
            "MainWindowViewModel.en-US.v1.json"));
        var chinese = File.ReadAllText(TestRepositoryPaths.SourceProject(
            "MinecraftServerManager.Contracts",
            "Localization",
            "MainWindowViewModel.zh-TW.v1.json"));

        Assert.Contains("\"server.properties_changed\"", source, StringComparison.Ordinal);
        Assert.Contains("main.vm.service.propertiesChanged", source, StringComparison.Ordinal);
        Assert.Contains("Reload it before saving again", english, StringComparison.Ordinal);
        Assert.Contains("請重新讀取後再儲存", chinese, StringComparison.Ordinal);
    }
}
