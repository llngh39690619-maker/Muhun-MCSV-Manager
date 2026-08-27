using System.ComponentModel;
using System.Windows;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

public partial class ProductProviderManagementDialog : Window
{
    private readonly ProductProviderManagementViewModel _viewModel;
    private bool _loaded;

    internal ProductProviderManagementDialog(ProductProviderManagementViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await _viewModel.InitializeAsync();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.Dispose();
}
