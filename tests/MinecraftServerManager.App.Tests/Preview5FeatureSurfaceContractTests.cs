using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.App.Tests;

/// <summary>
/// Keeps the Preview 5 entry points discoverable in the UI. The implementation services have
/// deeper behavioral tests; these contracts protect the user-visible routes and explanations.
/// </summary>
public sealed class Preview5FeatureSurfaceContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void GeneralSettings_ExposeThemesWindowSizeFontAndFutureServerDefaults()
    {
        var document = LoadDialog("GeneralSettingsDialog.xaml");
        var mainWindow = LoadMainWindow();
        Assert.Equal(
            "{Binding WindowWidth, Mode=OneWay}",
            (string?)mainWindow.Root?.Attribute("Width"));
        Assert.Equal(
            "{Binding WindowHeight, Mode=OneWay}",
            (string?)mainWindow.Root?.Attribute("Height"));

        foreach (var itemsSource in new[]
                 {
                     "{Binding Themes}",
                     "{Binding WindowSizeOptions}",
                 })
        {
            Assert.Contains(
                document.Descendants(Presentation + "ComboBox"),
                element => (string?)element.Attribute("ItemsSource") == itemsSource);
        }

        foreach (var valueBinding in new[]
                 {
                     "{Binding WindowWidth, UpdateSourceTrigger=PropertyChanged}",
                     "{Binding WindowHeight, UpdateSourceTrigger=PropertyChanged}",
                 })
        {
            Assert.Contains(
                document.Descendants(Presentation + "TextBox"),
                element => (string?)element.Attribute("Text") == valueBinding);
        }

        var fontSlider = Assert.Single(
            document.Descendants(Presentation + "Slider"),
            element => (string?)element.Attribute("Value") == "{Binding FontSize}");
        Assert.Equal("11", (string?)fontSlider.Attribute("Minimum"));
        Assert.Equal("20", (string?)fontSlider.Attribute("Maximum"));

        foreach (var valueBinding in new[]
                 {
                     "{Binding DefaultMinimumMemoryMb}",
                     "{Binding DefaultMaximumMemoryMb}",
                 })
        {
            var slider = Assert.Single(
                document.Descendants(Presentation + "Slider"),
                element => (string?)element.Attribute("Value") == valueBinding);
            Assert.Equal(
                "{Binding DefaultMemorySliderMaximumMb}",
                (string?)slider.Attribute("Maximum"));
        }

        foreach (var defaultBinding in new[]
                 {
                     "{Binding SeparateDiagnosticOutput}",
                     "{Binding AutoRestart}",
                     "{Binding EnableHangWatchdog}",
                     "{Binding EnableAutomaticRecoveryPoints}",
                 })
        {
            Assert.Contains(
                document.Descendants(Presentation + "CheckBox"),
                element => (string?)element.Attribute("IsChecked") == defaultBinding);
        }

        Assert.DoesNotContain(
            document.Descendants().Attributes(),
            attribute => attribute.Value.Contains("DefaultMemoryMode", StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{DynamicResource L10n.settings.globalMemory}");
        Assert.Contains(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.common.close}"
                       && (string?)element.Attribute("Command") == "{Binding CloseCommand}");
        Assert.Contains(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.common.apply}"
                       && (string?)element.Attribute("Command") == "{Binding ApplyCommand}");
        Assert.DoesNotContain(
            document.Descendants().Attributes(),
            attribute => attribute.Value.Contains("BackgroundImagePath", StringComparison.Ordinal)
                         || attribute.Value.Contains("IconImagePath", StringComparison.Ordinal));

        var confirmation = LoadDialog("GeneralSettingsUnsavedChangesDialog.xaml");
        Assert.Equal(
            "{StaticResource AppWindowStyle}",
            (string?)confirmation.Root?.Attribute("Style"));
        var confirmationButtons = confirmation.Descendants(Presentation + "Button").ToArray();
        Assert.Equal(2, confirmationButtons.Length);
        foreach (var label in new[]
                 {
                     "{DynamicResource L10n.common.cancel}",
                     "{DynamicResource L10n.common.save}",
                 })
        {
            Assert.Contains(
                confirmationButtons,
                element => (string?)element.Attribute("Content") == label);
        }
        Assert.Equal("420", (string?)confirmation.Root?.Attribute("Width"));
        Assert.Equal("200", (string?)confirmation.Root?.Attribute("Height"));
        Assert.Equal("NoResize", (string?)confirmation.Root?.Attribute("ResizeMode"));
        Assert.Equal("CenterOwner", (string?)confirmation.Root?.Attribute("WindowStartupLocation"));
    }

    [Fact]
    public void PerServerMemoryCard_OffersDefaultAutomaticAndManualWithDynamicSliders()
    {
        var document = LoadMainWindow();
        var selectors = document.Descendants(Presentation + "RadioButton")
            .Where(element => (string?)element.Attribute("GroupName") == "ServerMemoryMode")
            .ToArray();

        Assert.Equal(3, selectors.Length);
        Assert.Contains(
            selectors,
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.memory.unspecified}"
                       && (string?)element.Attribute("IsChecked")
                       == "{Binding SelectedServer.IsMemoryUsingDefault}");
        Assert.Contains(
            selectors,
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.memory.automatic}"
                       && (string?)element.Attribute("IsChecked")
                       == "{Binding SelectedServer.IsMemoryAutomatic}");
        Assert.Contains(
            selectors,
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.memory.manual}"
                       && (string?)element.Attribute("IsChecked")
                       == "{Binding SelectedServer.IsMemoryManual}");

        foreach (var valueBinding in new[]
                 {
                     "{Binding SelectedServer.MinimumMemorySliderMb}",
                     "{Binding SelectedServer.MaximumMemorySliderMb}",
                 })
        {
            var slider = Assert.Single(
                document.Descendants(Presentation + "Slider"),
                element => (string?)element.Attribute("Value") == valueBinding);
            Assert.Equal(
                "{Binding SelectedServer.MemorySliderMaximumMb}",
                (string?)slider.Attribute("Maximum"));
        }

        Assert.Contains(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{Binding SelectedServer.MemoryConfigurationHint}");
        Assert.Contains(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.memory.recalculate}"
                       && (string?)element.Attribute("Command")
                       == "{Binding SelectedServer.RecalculateAutomaticMemoryCommand}"
                       && (string?)element.Attribute("Visibility")
                       == "{Binding SelectedServer.IsMemoryAutomatic, Converter={StaticResource BoolToVisibility}}");
        Assert.Contains(
            document.Descendants(Presentation + "ProgressBar"),
            element => (string?)element.Attribute("Visibility")
                       == "{Binding SelectedServer.IsAutomaticMemoryRecommendationRunning, Converter={StaticResource BoolToVisibility}}");
    }

    [Fact]
    public void ModpackUpdateCardAndConfirmation_ExplainBackupAndPreservationContract()
    {
        var mainWindow = LoadMainWindow();
        Assert.Contains(
            mainWindow.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{Binding SelectedServer.ModpackSourceDisplay}");
        Assert.Contains(
            mainWindow.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.modpackUpdate.start}"
                       && (string?)element.Attribute("Command")
                       == "{Binding UpdateSelectedModpackCommand}");
        Assert.Contains(
            mainWindow.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.modpackUpdate.openBackups}"
                       && (string?)element.Attribute("Command")
                       == "{Binding OpenModpackUpdateBackupsCommand}");

        var dialog = LoadDialog("ModpackUpdateSelectionDialog.xaml");
        Assert.Contains(
            dialog.Descendants(Presentation + "ComboBox"),
            element => (string?)element.Attribute("ItemsSource")
                       == "{Binding AvailableVersions}");
        Assert.Contains(
            dialog.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{DynamicResource L10n.modpackUpdate.preserved}");
        var preserved = ProductLocalizationCatalog.GetDocument("zh-TW")
            .Strings["modpackUpdate.preserved"];
        Assert.Contains("世界、玩家資料", preserved, StringComparison.Ordinal);
        Assert.Contains("不包含 Server 核心", preserved, StringComparison.Ordinal);
        Assert.Contains(
            dialog.Descendants(Presentation + "CheckBox"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.modpackUpdate.acknowledge}");
        Assert.Contains(
            dialog.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content") == "{DynamicResource L10n.modpackUpdate.confirm}"
                       && (string?)element.Attribute("Click") == "OnConfirmClick");
    }

    [Fact]
    public void RemoteManagement_UsesOneMainEntryAndKeepsTheLegacyConsoleReachable()
    {
        var mainWindow = LoadMainWindow();
        var remoteEntry = Assert.Single(
            mainWindow.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.remoteManagement}"
                       && (string?)element.Attribute("Command")
                       == "{Binding OpenRemoteManagementCommand}");
        Assert.Equal(
            "{DynamicResource L10n.main.remoteManagement.automation}",
            (string?)remoteEntry.Attribute("AutomationProperties.Name"));
        Assert.DoesNotContain(
            mainWindow.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Command") is
                "{Binding OpenRemoteAccessCommand}" or "{Binding OpenRemoteWebConsoleCommand}");

        var webConsole = LoadDialog("RemoteWebConsoleDialog.xaml");
        Assert.Contains(
            webConsole.Descendants(Presentation + "TextBox"),
            element => (string?)element.Attribute("Text")
                       == "{Binding PublicUrl, Mode=OneWay}"
                       && (string?)element.Attribute("IsReadOnly") == "True");
        var logList = Assert.Single(
            webConsole.Descendants(Presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding Logs}");
        Assert.Equal("True", (string?)logList.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)logList.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.DoesNotContain(
            webConsole.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding StartCommand}");
        Assert.Contains(
            webConsole.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.console.stopWeb}"
                       && (string?)element.Attribute("Command") == "{Binding StopCommand}");
        Assert.Contains(
            webConsole.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.console.reconnect}"
                       && (string?)element.Attribute("Command") == "{Binding ReconnectCommand}");
        Assert.Contains(
            webConsole.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{DynamicResource L10n.remote.console.lifecycleHint}");

        var remoteSettings = LoadDialog("RemoteAccessDialog.xaml");
        Assert.Contains(
            remoteSettings.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.main.webConsole}"
                       && (string?)element.Attribute("Click") == "OnOpenWebConsoleClick");
        Assert.Contains(
            remoteSettings.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.legacy.ui.installCloudflared}"
                       && (string?)element.Attribute("Command")
                       == "{Binding InstallCloudflaredCommand}"
                       && (string?)element.Attribute("Visibility")
                       == "{Binding IsCloudflareMode, Converter={StaticResource BoolToVisibility}}");
        Assert.Contains(
            remoteSettings.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{Binding CloudflaredInstallStatus, Mode=OneWay}");
    }

    private static XDocument LoadMainWindow() =>
        XDocument.Load(GetAppSourcePath("MainWindow.xaml"));

    private static XDocument LoadDialog(string name) =>
        XDocument.Load(GetAppSourcePath(Path.Combine("Dialogs", name)));

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
