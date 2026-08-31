using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

public partial class ClientContentDownloadCenterWindow : Window
{
    private const double LoadMoreThreshold = 280d;
    private bool _closeCommandSynchronized;

    public ClientContentDownloadCenterWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeCommandSynchronized)
        {
            return;
        }

        _closeCommandSynchronized = true;
        if (Tag is ICommand command && command.CanExecute(null))
        {
            command.Execute(null);
        }
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

    private void OnContentDownloadTabSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, ContentDownloadTabs) ||
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
