using System.Windows;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

internal partial class RemoteWebConsoleDialog : Window
{
    private readonly RemoteWebConsoleViewModel _viewModel;

    internal RemoteWebConsoleDialog(RemoteWebConsoleViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => _viewModel.Dispose();
}
