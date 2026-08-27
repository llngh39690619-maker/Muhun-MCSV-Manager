using System.Windows;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

public partial class ServerAppearanceSettingsDialog : Window
{
    public ServerAppearanceSettingsDialog(MainWindowViewModel ownerViewModel)
    {
        ArgumentNullException.ThrowIfNull(ownerViewModel);
        InitializeComponent();
        DataContext = ownerViewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
