using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

public partial class AppearanceSettingsDialog : Window
{
    private readonly AppearanceSettingsViewModel _viewModel;
    private bool _completed;

    public AppearanceSettingsDialog(AppearanceSettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Saved += OnSaved;
        viewModel.Cancelled += OnCancelled;
    }

    private void OnChooseBackgroundClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
                Title = LocalizationService.Current.Get("appearance.dialog.chooseImage"),
                Filter = LocalizationService.Current.Get("appearance.dialog.imageFilter"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (picker.ShowDialog(this) == true)
        {
            _viewModel.TryImportBackgroundImage(picker.FileName);
        }
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        if (_completed) return;
        _completed = true;
        DialogResult = true;
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        if (_completed) return;
        _completed = true;
        DialogResult = false;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            return;
        }

        if (!_completed)
        {
            _completed = true;
            _viewModel.Cancel();
        }

        _viewModel.Saved -= OnSaved;
        _viewModel.Cancelled -= OnCancelled;
    }
}
