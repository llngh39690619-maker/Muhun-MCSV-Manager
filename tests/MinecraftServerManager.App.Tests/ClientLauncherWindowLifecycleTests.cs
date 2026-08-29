using System.Windows;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientLauncherWindowLifecycleTests
{
    [Fact]
    public void FailedOrOptedOutLaunch_DoesNotMinimizeOrCreateAHiddenSession()
    {
        var lifecycle = new ClientLauncherWindowLifecycle();
        var failed = Guid.NewGuid();
        var optedOut = Guid.NewGuid();

        Assert.Equal(
            ClientLauncherWindowTransition.None,
            lifecycle.CompleteLaunch(failed, launchSucceeded: false, hideLauncherAfterGameStarts: true));
        Assert.Equal(
            ClientLauncherWindowTransition.None,
            lifecycle.CompleteLaunch(optedOut, launchSucceeded: true, hideLauncherAfterGameStarts: false));
        Assert.Equal(0, lifecycle.HiddenSessionCount);
        Assert.Equal(ClientLauncherWindowTransition.None, lifecycle.CompleteSession(failed));
        Assert.Equal(ClientLauncherWindowTransition.None, lifecycle.CompleteSession(optedOut));
    }

    [Fact]
    public void TwoSuccessfulClients_RestoreOnlyAfterLastHiddenSessionEnds()
    {
        var lifecycle = new ClientLauncherWindowLifecycle();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Equal(
            ClientLauncherWindowTransition.Minimize,
            lifecycle.CompleteLaunch(first, launchSucceeded: true, hideLauncherAfterGameStarts: true));
        Assert.Equal(
            ClientLauncherWindowTransition.Minimize,
            lifecycle.CompleteLaunch(second, launchSucceeded: true, hideLauncherAfterGameStarts: true));
        Assert.Equal(2, lifecycle.HiddenSessionCount);

        Assert.Equal(ClientLauncherWindowTransition.None, lifecycle.CompleteSession(first));
        Assert.Equal(1, lifecycle.HiddenSessionCount);
        Assert.Equal(ClientLauncherWindowTransition.Restore, lifecycle.CompleteSession(second));
        Assert.Equal(0, lifecycle.HiddenSessionCount);
        Assert.Equal(ClientLauncherWindowTransition.None, lifecycle.CompleteSession(second));
    }

    [Fact]
    public void Shutdown_DropsTrackedSessionsAndSuppressesLateMinimizeOrRestore()
    {
        var lifecycle = new ClientLauncherWindowLifecycle();
        var running = Guid.NewGuid();

        Assert.Equal(
            ClientLauncherWindowTransition.Minimize,
            lifecycle.CompleteLaunch(running, launchSucceeded: true, hideLauncherAfterGameStarts: true));

        lifecycle.BeginShutdown();

        Assert.Equal(0, lifecycle.HiddenSessionCount);
        Assert.Equal(ClientLauncherWindowTransition.None, lifecycle.CompleteSession(running));
        Assert.Equal(
            ClientLauncherWindowTransition.None,
            lifecycle.CompleteLaunch(
                Guid.NewGuid(),
                launchSucceeded: true,
                hideLauncherAfterGameStarts: true));
    }

    [Fact]
    public async Task LauncherEvents_WithNoTray_MinimizeToTaskbarAndRestoreTheWindow()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(temporary.Path);
                paths.EnsureCreated();
                viewModel = new MainWindowViewModel(paths);
                var window = new MainWindow(viewModel)
                {
                    Width = 1180,
                    Height = 760,
                    Left = -10_000,
                    Top = -10_000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };

                window.Show();
                try
                {
                    viewModel.ClientWorkspace.PublishLauncherWindowTransition(
                        ClientLauncherWindowTransition.Minimize);
                    DrainDispatcher();

                    Assert.True(window.IsVisible);
                    Assert.True(window.ShowInTaskbar);
                    Assert.Equal(WindowState.Minimized, window.WindowState);

                    viewModel.ClientWorkspace.PublishLauncherWindowTransition(
                        ClientLauncherWindowTransition.Restore);
                    DrainDispatcher();

                    Assert.True(window.IsVisible);
                    Assert.True(window.ShowInTaskbar);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                }
                finally
                {
                    window.PrepareForApplicationShutdown();
                    window.Close();
                }
            });
        }
        finally
        {
            if (viewModel is not null)
            {
                await viewModel.DisposeAsync();
            }
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            () => frame.Continue = false,
            DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
}
