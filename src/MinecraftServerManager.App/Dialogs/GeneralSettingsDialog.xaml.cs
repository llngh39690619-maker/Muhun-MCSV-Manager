using System.ComponentModel;
using System.Windows;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

public partial class GeneralSettingsDialog : Window
{
    private readonly GeneralSettingsViewModel _viewModel;
    private readonly Func<Window, GeneralSettingsCloseChoice> _confirmUnsavedChanges;
    private bool _completed;
    private bool _closeFlowActive;

    public GeneralSettingsDialog(GeneralSettingsViewModel viewModel)
        : this(viewModel, ShowUnsavedChangesConfirmation)
    {
    }

    internal GeneralSettingsDialog(
        GeneralSettingsViewModel viewModel,
        Func<Window, GeneralSettingsCloseChoice> confirmUnsavedChanges)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _confirmUnsavedChanges = confirmUnsavedChanges
            ?? throw new ArgumentNullException(nameof(confirmUnsavedChanges));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += OnSaved;
        viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        if (_completed) return;
        Complete(result: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.RefreshUpdateCommand.CanExecute(null))
        {
            _viewModel.RefreshUpdateCommand.Execute(null);
        }
    }

    private async void OnCloseRequested(object? sender, EventArgs e)
        => await TryCloseAsync();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_completed)
        {
            DetachHandlers();
            return;
        }

        e.Cancel = true;
        if (_viewModel.IsBusy || _closeFlowActive) return;
        // DialogResult must not be assigned reentrantly from the same Closing event that was
        // cancelled. Resume the shared close flow after WPF has unwound this event; otherwise an
        // X-button close can remain visible with _completed already set.
        _ = Dispatcher.BeginInvoke(
            new Action(() => _ = TryCloseAsync()),
            System.Windows.Threading.DispatcherPriority.Normal);
    }

    private async Task TryCloseAsync()
    {
        if (_completed || _viewModel.IsBusy || _closeFlowActive) return;
        _closeFlowActive = true;
        try
        {
            if (!_viewModel.HasUnsavedChanges)
            {
                _viewModel.RestorePreview();
                Complete(result: false);
                return;
            }

            switch (_confirmUnsavedChanges(this))
            {
                case GeneralSettingsCloseChoice.SaveAndApply:
                    await _viewModel.SaveAndApplyAsync();
                    break;
                case GeneralSettingsCloseChoice.Discard:
                    _viewModel.RestorePreview();
                    Complete(result: false);
                    break;
                case GeneralSettingsCloseChoice.ContinueEditing:
                default:
                    break;
            }
        }
        finally
        {
            _closeFlowActive = false;
        }
    }

    private void Complete(bool result)
    {
        if (_completed) return;
        _completed = true;
        DetachHandlers();
        DialogResult = result;
    }

    private void DetachHandlers()
    {
        _viewModel.Saved -= OnSaved;
        _viewModel.CloseRequested -= OnCloseRequested;
    }

    private static GeneralSettingsCloseChoice ShowUnsavedChangesConfirmation(Window owner)
    {
        var dialog = new GeneralSettingsUnsavedChangesDialog { Owner = owner };
        _ = dialog.ShowDialog();
        return dialog.Choice;
    }
}
