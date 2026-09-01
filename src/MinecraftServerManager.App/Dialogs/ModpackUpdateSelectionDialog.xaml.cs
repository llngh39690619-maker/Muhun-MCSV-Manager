using System.Windows;
using System.Windows.Navigation;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Dialogs;

public partial class ModpackUpdateSelectionDialog : Window
{
    public ModpackUpdateSelectionDialog(
        ServerInstance instance,
        IReadOnlyList<OnlineModpackVersion> availableVersions)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(availableVersions);
        InitializeComponent();
        ServerName = instance.Name;
        SourceDisplay = instance.ModpackSource switch
        {
            ModpackSourceKind.Ftb => "FTB",
            ModpackSourceKind.Modrinth => "Modrinth",
            ModpackSourceKind.CurseForge => "CurseForge",
            _ => LocalizationService.Current.Get("common.unknown")
        };
        CurrentVersionDisplay = instance.ModpackVersionName
                                ?? instance.ModpackVersionId
            ?? LocalizationService.Current.Get("common.unknown");
        AvailableVersions = availableVersions;
        RequiresMinecraftEulaAcceptance = instance.ModpackSource == ModpackSourceKind.Ftb;
        DataContext = this;
    }

    public string ServerName { get; }

    public string SourceDisplay { get; }

    public string CurrentVersionDisplay { get; }

    public IReadOnlyList<OnlineModpackVersion> AvailableVersions { get; }

    public bool RequiresMinecraftEulaAcceptance { get; }

    public OnlineModpackVersion? SelectedVersion => VersionPicker.SelectedItem as OnlineModpackVersion;

    public bool MinecraftEulaAccepted
        => RequiresMinecraftEulaAcceptance && MinecraftEulaAcceptanceCheckBox.IsChecked == true;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is null)
        {
            DarkMessageBox.Show(
                this,
                LocalizationService.Current.Get("modpackUpdate.validation.selectVersion"),
                LocalizationService.Current.Get("modpackUpdate.window.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (AcknowledgementCheckBox.IsChecked != true)
        {
            DarkMessageBox.Show(
                this,
                LocalizationService.Current.Get("modpackUpdate.validation.acknowledge"),
                LocalizationService.Current.Get("modpackUpdate.window.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (RequiresMinecraftEulaAcceptance && !MinecraftEulaAccepted)
        {
            DarkMessageBox.Show(
                this,
                LocalizationService.Current.Get("online.validation.minecraftEulaRequired"),
                LocalizationService.Current.Get("modpackUpdate.window.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void OnMinecraftEulaLinkRequestNavigate(
        object sender,
        RequestNavigateEventArgs e)
    {
        _ = MinecraftEulaLinkOpener.TryOpen(this);
        e.Handled = true;
    }
}
