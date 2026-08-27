using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftServerManager.App.Controls;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

public partial class RemoteAccessDialog : Window
{
    private readonly RemoteAccessSettingsViewModel _viewModel;

    internal RemoteAccessDialog(RemoteAccessSettingsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    internal event EventHandler? OpenWebConsoleRequested;

    private void OnOpenWebConsoleClick(object sender, RoutedEventArgs e)
        => OpenWebConsoleRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnWindowDeactivated(object? sender, EventArgs e)
        => HideRevealedPasswords();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        HideRevealedPasswords();
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            return;
        }

        CloudflareNamedTunnelTokenInput.Clear();
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }

    private void OnCloudflareNamedTunnelTokenPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.CloudflareNamedTunnelToken = passwordBox.Password;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteAccessSettingsViewModel.CloudflareNamedTunnelToken)
            && string.IsNullOrEmpty(_viewModel.CloudflareNamedTunnelToken)
            && CloudflareNamedTunnelTokenInput.Password.Length > 0)
        {
            CloudflareNamedTunnelTokenInput.Clear();
        }
    }

    private void HideRevealedPasswords()
    {
        _viewModel.HideRevealedSecrets();
        foreach (var passwordBox in FindVisualChildren<RevealPasswordBox>(this))
        {
            passwordBox.HidePassword();
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
