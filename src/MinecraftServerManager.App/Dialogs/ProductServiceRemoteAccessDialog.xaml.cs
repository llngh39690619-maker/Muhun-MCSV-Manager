using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using MinecraftServerManager.App.Controls;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

public partial class ProductServiceRemoteAccessDialog : Window
{
    private readonly ProductServiceRemoteAccessViewModel _viewModel;
    private bool _initialized;

    internal ProductServiceRemoteAccessDialog(ProductServiceRemoteAccessViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await _viewModel.InitializeAsync();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _viewModel.HideRevealedPins();
        HidePasswordControls();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.ClearRevealedSecrets();
        HidePasswordControls();
        _viewModel.Dispose();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void HidePasswordControls()
    {
        foreach (var control in FindVisualChildren<RevealPasswordBox>(this))
        {
            control.HidePassword();
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
