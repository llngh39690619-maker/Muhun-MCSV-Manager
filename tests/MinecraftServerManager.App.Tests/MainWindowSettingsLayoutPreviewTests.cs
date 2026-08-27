using System.IO;
using System.Windows;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class MainWindowSettingsLayoutPreviewTests
{
    [Fact]
    public async Task ViewModel_PreservesGloballySafeSavedSizeUntilCurrentMonitorIsKnown()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        using (var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile))
        {
            await store.SaveAsync(new ManagerSettings
            {
                UserInterface = new ManagerUiSettings
                {
                    WindowWidth = 5000,
                    WindowHeight = 2500,
                },
            });
        }

        await using var viewModel = new MainWindowViewModel(paths);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        Assert.Equal(5000, viewModel.WindowWidth);
        Assert.Equal(2500, viewModel.WindowHeight);
    }

    [Fact]
    public async Task SmallWorkAreaSize_PersistsAcrossCloseAndReopenWithoutDesignMinimumInflation()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();

        await using (var firstSession = new MainWindowViewModel(paths))
        {
            await firstSession.InitializeAsync(allowInteractiveAutoImport: false);
            await firstSession.PersistNormalWindowSizeAsync(900, 600);
        }

        await using var reopened = new MainWindowViewModel(paths);
        await reopened.InitializeAsync(allowInteractiveAutoImport: false);

        Assert.Equal(900, reopened.WindowWidth);
        Assert.Equal(600, reopened.WindowHeight);
        WpfStaTestHost.Run(() =>
        {
            var window = new MainWindow(reopened);
            try
            {
                var restored = window.PreviewNormalLayout(
                    reopened.WindowWidth,
                    reopened.WindowHeight,
                    new Rect(-900, 0, 900, 600));

                Assert.Equal(new Size(900, 600), restored.Size);
                Assert.Equal(900, window.MinWidth);
                Assert.Equal(600, window.MinHeight);
            }
            finally
            {
                window.PrepareForApplicationShutdown();
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreviewSize_UsesInjectedMonitorWorkAreaAndLowersDesignMinimumForSmallScreens()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                viewModel = new MainWindowViewModel(new ApplicationPaths(directory.Path));
                var window = new MainWindow(viewModel);
                try
                {
                    var smallBounds = window.PreviewNormalLayout(
                        1800,
                        1000,
                        new Rect(-900, 40, 900, 600));

                    Assert.Equal(900, window.MinWidth);
                    Assert.Equal(600, window.MinHeight);
                    Assert.Equal(new Rect(-900, 40, 900, 600), smallBounds);
                    Assert.Equal(900, window.Width);
                    Assert.Equal(600, window.Height);

                    var largeBounds = window.PreviewNormalLayout(
                        1800,
                        1000,
                        new Rect(0, 0, 1920, 1080));

                    Assert.Equal(MainWindow.DesignMinimumWindowWidth, window.MinWidth);
                    Assert.Equal(MainWindow.DesignMinimumWindowHeight, window.MinHeight);
                    Assert.Equal(1800, largeBounds.Width);
                    Assert.Equal(1000, largeBounds.Height);
                    Assert.True(largeBounds.Left >= 0);
                    Assert.True(largeBounds.Right <= 1920);
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

    [Theory]
    [InlineData(1920, 1080, 1600, 900, 1600, 900)]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]
    [InlineData(900, 600, 1120, 700, 900, 600)]
    public void ClampNormalSizeToWorkArea_HandlesMixedMonitorSizes(
        double workWidth,
        double workHeight,
        double requestedWidth,
        double requestedHeight,
        double expectedWidth,
        double expectedHeight)
    {
        var result = MainWindow.ClampNormalSizeToWorkArea(
            requestedWidth,
            requestedHeight,
            new Rect(-workWidth, 0, workWidth, workHeight));

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Fact]
    public async Task NormalResize_PersistsAndMaximizeDoesNotOverwriteTheRestoredSize()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        await new JsonSettingsStore<ManagerSettings>(paths.SettingsFile).SaveAsync(new ManagerSettings());
        var viewModel = new MainWindowViewModel(paths);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var expectedWidth = Math.Min(1300d, SystemParameters.WorkArea.Width);
        var expectedHeight = Math.Min(760d, SystemParameters.WorkArea.Height);

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var window = new MainWindow(viewModel)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.WorkArea.Left + 12,
                    Top = SystemParameters.WorkArea.Top + 12,
                    ShowInTaskbar = false,
                };
                window.Show();
                try
                {
                    DrainDispatcher();
                    window.Width = expectedWidth;
                    window.Height = expectedHeight;
                    DrainDispatcher();
                    expectedWidth = Math.Round(window.ActualWidth);
                    expectedHeight = Math.Round(window.ActualHeight);
                    WaitWithDispatcher(window.FlushPendingWindowSizePersistenceAsync());

                    window.WindowState = WindowState.Maximized;
                    DrainDispatcher();
                    WaitWithDispatcher(window.FlushPendingWindowSizePersistenceAsync());
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
            await viewModel.DisposeAsync();
        }

        using var persistedStore = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        var persisted = Assert.IsType<ManagerSettings>(await persistedStore.LoadAsync());
        Assert.Equal(Math.Round(expectedWidth), persisted.UserInterface.WindowWidth);
        Assert.Equal(Math.Round(expectedHeight), persisted.UserInterface.WindowHeight);

        await using var reloaded = new MainWindowViewModel(paths);
        await reloaded.InitializeAsync(allowInteractiveAutoImport: false);
        Assert.Equal(Math.Round(expectedWidth), reloaded.WindowWidth);
        Assert.Equal(Math.Round(expectedHeight), reloaded.WindowHeight);
    }

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

    private static void WaitWithDispatcher(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
