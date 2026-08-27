using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class GeneralSettingsDialogLifecycleTests
{
    [Fact]
    public void CloseButton_WithNoEdits_ClosesDirectlyWithoutConfirmation()
    {
        WpfStaTestHost.Run(() =>
        {
            var restoreCount = 0;
            var confirmationCount = 0;
            var viewModel = CreateViewModel(
                restorePreview: () => restoreCount++);
            var dialog = new GeneralSettingsDialog(
                viewModel,
                _ =>
                {
                    confirmationCount++;
                    return GeneralSettingsCloseChoice.ContinueEditing;
                });

            var result = ShowAndAct(dialog, () => viewModel.CloseCommand.Execute(null));

            Assert.False(result);
            Assert.Equal(0, confirmationCount);
            Assert.Equal(1, restoreCount);
        });
    }

    [Fact]
    public void WindowClose_WithEditsAndDiscard_RestoresPreviewAndCloses()
    {
        WpfStaTestHost.Run(() =>
        {
            var restoreCount = 0;
            var confirmationCount = 0;
            var viewModel = CreateViewModel(
                restorePreview: () => restoreCount++);
            viewModel.FontSize = 15.5;
            var dialog = new GeneralSettingsDialog(
                viewModel,
                _ =>
                {
                    confirmationCount++;
                    return GeneralSettingsCloseChoice.Discard;
                });

            var result = ShowAndAct(dialog, dialog.Close);

            Assert.False(result);
            Assert.Equal(1, confirmationCount);
            Assert.Equal(1, restoreCount);
        });
    }

    [Fact]
    public void CloseButton_WithEditsAndSave_PersistsAndClosesWithoutRestoringPreview()
    {
        WpfStaTestHost.Run(() =>
        {
            var restoreCount = 0;
            var saveCount = 0;
            var viewModel = CreateViewModel(
                saveAsync: (_, _, _) =>
                {
                    saveCount++;
                    return Task.CompletedTask;
                },
                restorePreview: () => restoreCount++);
            viewModel.FontSize = 15.5;
            var dialog = new GeneralSettingsDialog(
                viewModel,
                _ => GeneralSettingsCloseChoice.SaveAndApply);

            var result = ShowAndAct(dialog, () => viewModel.CloseCommand.Execute(null));

            Assert.True(result);
            Assert.Equal(1, saveCount);
            Assert.Equal(0, restoreCount);
            Assert.False(viewModel.HasUnsavedChanges);
        });
    }

    private static bool ShowAndAct(GeneralSettingsDialog dialog, Action action)
    {
        var timedOut = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            timedOut = true;
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
        };
        dialog.ContentRendered += (_, _) => action();

        timeout.Start();
        var result = dialog.ShowDialog();
        timeout.Stop();

        Assert.False(timedOut);
        Assert.False(dialog.IsVisible);
        return result == true;
    }

    private static GeneralSettingsViewModel CreateViewModel(
        Func<ManagerUiSettings, NewServerDefaultsSettings, ApplicationAppearanceSettings, Task>? saveAsync = null,
        Action? restorePreview = null)
        => new(
            new ManagerUiSettings(),
            new NewServerDefaultsSettings(),
            saveAsync ?? (static (_, _, _) => Task.CompletedTask),
            restorePreview: restorePreview);
}
