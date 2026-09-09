using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.App.Views;

namespace MinecraftServerManager.App.Dialogs;

public partial class ClientContentDownloadCenterWindow : UserControl
{
    private const double LoadMoreThreshold = 280d;

    public ClientContentDownloadCenterWindow()
    {
        InitializeComponent();
    }

    private void OnResultsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0d)
        {
            return;
        }

        var remaining = Math.Max(0d, e.ExtentHeight - e.VerticalOffset - e.ViewportHeight);
        if (remaining > LoadMoreThreshold || sender is not FrameworkElement { Tag: ICommand command })
        {
            return;
        }

        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnScrollableRegionPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled
            || e.Delta == 0
            || sender is not DependencyObject scope
            || e.OriginalSource is not DependencyObject source
            || !ClientWorkspaceView.TryRouteMouseWheel(source, e.Delta, scope))
        {
            return;
        }

        e.Handled = true;
    }

    private void OnContentDownloadTabSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        // TabControl selects its first item while the view is being materialized, before
        // the one-way IsSelected bindings restore the content kind requested by the card.
        // Treat only selections made after the embedded view has loaded as user navigation.
        if (!IsLoaded ||
            !ReferenceEquals(sender, ContentDownloadTabs) ||
            DataContext is not ClientWorkspaceViewModel workspace)
        {
            return;
        }

        var parameter = ContentDownloadTabs.SelectedIndex switch
        {
            0 => "Mod",
            1 => "ResourcePack",
            2 => "ShaderPack",
            _ => null,
        };
        if (parameter is not null && workspace.SelectContentDownloadKindCommand.CanExecute(parameter))
        {
            workspace.SelectContentDownloadKindCommand.Execute(parameter);
        }
    }
}
