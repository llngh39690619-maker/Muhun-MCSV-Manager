using System.IO;
using System.Windows;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Tests;

public sealed class MainWindowSettingsLayoutPreviewTests
{
    [Fact]
    public async Task PreviewSize_NormalizesMaximizedWindowAndDiscardRestoresBounds()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(directory.Path);
                paths.EnsureCreated();
                viewModel = new MainWindowViewModel(paths);
                var window = new MainWindow(viewModel)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.WorkArea.Left + 24,
                    Top = SystemParameters.WorkArea.Top + 24,
                    ShowInTaskbar = false,
                };
                window.Show();
                try
                {
                    DrainDispatcher();
                    var initialBounds = window.PreviewNormalLayout(1280, 720);
                    DrainDispatcher();
                    window.WindowState = WindowState.Maximized;
                    DrainDispatcher();
                    var snapshot = window.CaptureLayoutForSettingsPreview();
                    Assert.Equal(WindowState.Maximized, snapshot.WindowState);
                    AssertRectNear(initialBounds, snapshot.NormalBounds);

                    var previewBounds = window.PreviewNormalLayout(1120, 700);
                    DrainDispatcher();

                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.InRange(window.ActualWidth, previewBounds.Width - 2, previewBounds.Width + 2);
                    Assert.InRange(window.ActualHeight, previewBounds.Height - 2, previewBounds.Height + 2);
                    Assert.True(previewBounds.Left >= SystemParameters.WorkArea.Left);
                    Assert.True(previewBounds.Top >= SystemParameters.WorkArea.Top);
                    Assert.True(previewBounds.Right <= SystemParameters.WorkArea.Right + 0.5);
                    Assert.True(previewBounds.Bottom <= SystemParameters.WorkArea.Bottom + 0.5);

                    window.RestoreLayoutAfterSettingsPreview(snapshot);
                    DrainDispatcher();

                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    AssertRectNear(snapshot.NormalBounds, window.RestoreBounds);
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

    private static void AssertRectNear(Rect expected, Rect actual)
    {
        Assert.InRange(actual.Left, expected.Left - 2, expected.Left + 2);
        Assert.InRange(actual.Top, expected.Top - 2, expected.Top + 2);
        Assert.InRange(actual.Width, expected.Width - 2, expected.Width + 2);
        Assert.InRange(actual.Height, expected.Height - 2, expected.Height + 2);
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
