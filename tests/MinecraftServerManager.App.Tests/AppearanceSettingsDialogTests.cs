using System.Windows;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class AppearanceSettingsDialogTests
{
    [Fact]
    public void SaveLifecycle_BlocksCloseWhileBusyThenClosesWithTrueDialogResult()
    {
        WpfStaTestHost.Run(() =>
        {
            using var directory = new AppearanceThemeServiceTests.TestDirectory();
            var persistence = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var service = AppearanceThemeServiceTests.CreateService(directory.Path);
            var viewModel = new AppearanceSettingsViewModel(
                service,
                Application.Current.Resources,
                new ApplicationAppearanceSettings(),
                _ => persistence.Task);
            var dialog = new AppearanceSettingsDialog(viewModel);
            Task<bool>? saveTask = null;
            var wasBusyDuringFirstClose = false;
            var remainedVisibleDuringFirstClose = false;

            var timeout = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                dialog.Close();
            };

            dialog.Loaded += (_, _) =>
            {
                saveTask = viewModel.SaveAsync();
                wasBusyDuringFirstClose = viewModel.IsBusy;

                // A title-bar close while the durable save is pending must be cancelled.
                dialog.Close();
                remainedVisibleDuringFirstClose = dialog.IsVisible;

                timeout.Start();
                persistence.SetResult(true);
            };

            var result = dialog.ShowDialog();
            timeout.Stop();

            Assert.True(wasBusyDuringFirstClose);
            Assert.True(remainedVisibleDuringFirstClose);
            Assert.NotNull(saveTask);
            Assert.True(saveTask.GetAwaiter().GetResult());
            Assert.True(result);
            Assert.False(dialog.IsVisible);
        });
    }
}
