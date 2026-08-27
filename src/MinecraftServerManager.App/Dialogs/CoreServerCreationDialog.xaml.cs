using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Dialogs;

public partial class CoreServerCreationDialog : Window
{
    private readonly CoreServerCreationViewModel _viewModel;
    private readonly Func<CoreServerCreationRequest, BackgroundJobSubmissionResult>? _backgroundSubmitter;
    private bool _initialized;
    private bool _completed;

    public CoreServerCreationDialog(ICoreServerCreationWorkflow workflow)
        : this(new CoreServerCreationViewModel(workflow))
    {
    }

    public CoreServerCreationDialog(CoreServerCreationViewModel viewModel)
        : this(viewModel, backgroundSubmitter: null)
    {
    }

    internal CoreServerCreationDialog(
        CoreServerCreationViewModel viewModel,
        Func<CoreServerCreationRequest, BackgroundJobSubmissionResult>? backgroundSubmitter)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        _backgroundSubmitter = backgroundSubmitter;
        DataContext = viewModel;
        viewModel.Created += OnCreated;
    }

    public ServerInstance? CreatedServer => _viewModel.CreatedServer;

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _viewModel.InitializeAsync();
    }

    private async void OnCoreSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CoreList.SelectedItem is not CoreServerProduct selected)
        {
            return;
        }

        await _viewModel.SelectCoreAsync(selected);
        if (_viewModel.SelectedCore != selected)
        {
            CoreList.SelectedItem = _viewModel.SelectedCore;
        }
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (_backgroundSubmitter is null)
        {
            await _viewModel.CreateAsync();
            return;
        }

        if (!_viewModel.TryBuildCreationRequest(out var request))
        {
            return;
        }

        BackgroundJobSubmissionResult submission;
        try
        {
            submission = _backgroundSubmitter(request);
        }
        catch (Exception exception)
        {
            _viewModel.SetBackgroundSubmissionError(
                LocalizationService.Current.Get("jobs.error.addCoreDetail", exception.Message));
            return;
        }

        if (!submission.Accepted)
        {
            _viewModel.SetBackgroundSubmissionError(
                submission.Error ?? LocalizationService.Current.Get("jobs.error.addCore"));
            return;
        }

        _completed = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            _viewModel.CancelCurrentOperation();
            return;
        }

        _completed = true;
        DialogResult = false;
    }

    private void OnCreated(object? sender, EventArgs e)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        DialogResult = true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_completed && _viewModel.IsBusy)
        {
            e.Cancel = true;
            _viewModel.CancelCurrentOperation();
            return;
        }

        _viewModel.Created -= OnCreated;
        _viewModel.Dispose();
        _completed = true;
    }
}
