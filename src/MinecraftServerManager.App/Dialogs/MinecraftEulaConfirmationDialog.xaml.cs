using System.Windows;
using System.Windows.Navigation;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Dialogs;

internal partial class MinecraftEulaConfirmationDialog : Window
{
    private MinecraftEulaConfirmationDialog(string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        InitializeComponent();
        DescriptionText.Text = LocalizationService.Current.Get(
            "main.vm.confirm.minecraftEula",
            serverName,
            MinecraftEulaLinkOpener.OfficialEulaUri);
    }

    internal bool Accepted { get; private set; }

    internal static bool Show(Window? owner, string serverName)
    {
        var dialog = new MinecraftEulaConfirmationDialog(serverName);
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _ = dialog.ShowDialog();
        return dialog.Accepted;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _ = CancelButton.Focus();
    }

    private void OnMinecraftEulaLinkRequestNavigate(
        object sender,
        RequestNavigateEventArgs e)
    {
        _ = MinecraftEulaLinkOpener.TryOpen(this);
        e.Handled = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
    }

    private void OnAgreeClick(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
    }
}
