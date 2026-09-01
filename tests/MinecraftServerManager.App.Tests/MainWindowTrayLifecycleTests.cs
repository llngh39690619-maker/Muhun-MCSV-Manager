using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Tests;

public sealed class MainWindowTrayLifecycleTests
{
    [Fact]
    public async Task Minimize_HidesWindowAndTrayOpenRestoresPreviousWindowState()
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
                var trayIcon = new FakeTrayIcon();
                var window = CreateOffscreenWindow(viewModel, trayIcon);

                window.Show();
                try
                {
                    window.WindowState = WindowState.Maximized;
                    DrainDispatcher();
                    window.WindowState = WindowState.Minimized;
                    DrainDispatcher();

                    Assert.False(window.IsVisible);
                    Assert.True(trayIcon.IsShown);
                    Assert.Equal(0, trayIcon.DisposeCount);

                    trayIcon.RequestOpen();
                    DrainDispatcher();

                    Assert.True(window.IsVisible);
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.False(trayIcon.IsShown);
                    Assert.Equal(0, trayIcon.DisposeCount);
                }
                finally
                {
                    window.PrepareForApplicationShutdown();
                    window.Close();
                }

                Assert.Equal(1, trayIcon.DisposeCount);
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

    [Fact]
    public async Task TrayOpen_FromWorkerThread_IsMarshalledToTheWindowDispatcher()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;
        MainWindow? window = null;
        FakeTrayIcon? trayIcon = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(temporary.Path);
                paths.EnsureCreated();
                viewModel = new MainWindowViewModel(paths);
                trayIcon = new FakeTrayIcon();
                window = CreateOffscreenWindow(viewModel, trayIcon);
                window.Show();
                window.WindowState = WindowState.Minimized;
                DrainDispatcher();
                Assert.False(window.IsVisible);
                Assert.True(trayIcon.IsShown);
            });

            await Task.Run(() => trayIcon!.RequestOpen());

            var restored = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!restored && DateTime.UtcNow < deadline)
            {
                WpfStaTestHost.Run(() =>
                    restored = window!.IsVisible
                               && window.WindowState == WindowState.Normal
                               && !trayIcon!.IsShown);
                if (!restored)
                {
                    await Task.Delay(10);
                }
            }

            Assert.True(restored, "背景執行緒的系統匣還原要求未送回 WPF Dispatcher。");
        }
        finally
        {
            if (window is not null)
            {
                WpfStaTestHost.Run(() =>
                {
                    window.PrepareForApplicationShutdown();
                    window.Close();
                });
            }

            if (viewModel is not null)
            {
                await viewModel.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TrayExit_UsesExistingSafeCloseAndDisposesTrayIconExactlyOnce()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;
        var settingsFile = Path.Combine(temporary.Path, "manager.json");

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(temporary.Path);
                paths.EnsureCreated();
                viewModel = new MainWindowViewModel(paths);
                var trayIcon = new FakeTrayIcon();
                var window = CreateOffscreenWindow(viewModel, trayIcon);

                window.Show();
                window.WindowState = WindowState.Minimized;
                DrainDispatcher();
                trayIcon.RequestExit();

                Assert.True(PumpDispatcherUntil(
                    () => !window.IsLoaded,
                    TimeSpan.FromSeconds(5)));
                Assert.False(window.IsVisible);
                Assert.False(trayIcon.IsShown);
                Assert.Equal(1, trayIcon.DisposeCount);

                window.PrepareForApplicationShutdown();
                Assert.Equal(1, trayIcon.DisposeCount);
            });

            Assert.True(File.Exists(settingsFile), "系統匣結束未完成既有的設定儲存流程。");
        }
        finally
        {
            if (viewModel is not null)
            {
                await viewModel.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TitleBarClose_RemainsARealExitInsteadOfHidingToTray()
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
                var trayIcon = new FakeTrayIcon();
                var window = CreateOffscreenWindow(viewModel, trayIcon);

                window.Show();
                window.Close();

                Assert.True(PumpDispatcherUntil(
                    () => !window.IsLoaded,
                    TimeSpan.FromSeconds(5)));
                Assert.False(window.IsVisible);
                Assert.False(trayIcon.IsShown);
                Assert.Equal(1, trayIcon.DisposeCount);
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

    [Fact]
    public void ProductionTrayAdapter_LocalizesMenuImmediatelyAndDisposesIdempotently()
    {
        Assert.Equal("X MCSV", MainWindowTrayIcon.ToolTipText);

        WpfStaTestHost.Run(() =>
        {
            LocalizationService.Current.SetCulture("zh-TW");
            var trayIcon = new MainWindowTrayIcon();
            try
            {
                Assert.Equal(LocalizationService.Current.Get("tray.open"), trayIcon.OpenMenuTextForTesting);
                Assert.Equal(LocalizationService.Current.Get("tray.exit"), trayIcon.ExitMenuTextForTesting);

                LocalizationService.Current.SetCulture("en-US");
                Assert.Equal("Open X MCSV", trayIcon.OpenMenuTextForTesting);
                Assert.Equal("Exit", trayIcon.ExitMenuTextForTesting);
            }
            finally
            {
                trayIcon.Hide();
                trayIcon.Dispose();
                trayIcon.Dispose();
                LocalizationService.Current.SetCulture("zh-TW");
            }

            Assert.False(trayIcon.TryShow());
        });
    }

    [Fact]
    public async Task RealNativeTray_MenuPipeline_RestoresAndSafelyClosesWithoutAnOrphan()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;
        var settingsFile = Path.Combine(temporary.Path, "manager.json");
        var shellAvailable = true;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                if (NativeMethods.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero)
                {
                    // Formal GUI tests run on a private Windows desktop so they cannot flash on
                    // the user's monitors or interfere with a game. That desktop intentionally
                    // has no Explorer shell; fake-adapter tests above cover the same tray pipeline.
                    Assert.True(WpfStaTestHost.IsIsolatedDesktop);
                    shellAvailable = false;
                    return;
                }

                var paths = new ApplicationPaths(temporary.Path);
                paths.EnsureCreated();
                viewModel = new MainWindowViewModel(paths);
                var trayIcon = new MainWindowTrayIcon();
                var window = CreateOffscreenWindow(viewModel, trayIcon);

                window.Show();
                try
                {
                    window.WindowState = WindowState.Minimized;
                    DrainDispatcher();

                    Assert.True(window.IsLoaded);
                    Assert.False(window.IsVisible);
                    Assert.True(trayIcon.IsVisibleForTesting);

                    trayIcon.PerformOpenMenuClickForTesting();
                    DrainDispatcher();

                    Assert.True(window.IsLoaded);
                    Assert.True(window.IsVisible);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.False(trayIcon.IsVisibleForTesting);

                    window.WindowState = WindowState.Minimized;
                    DrainDispatcher();
                    Assert.False(window.IsVisible);
                    Assert.True(trayIcon.IsVisibleForTesting);

                    trayIcon.PerformExitMenuClickForTesting();
                    Assert.True(PumpDispatcherUntil(
                        () => !window.IsLoaded,
                        TimeSpan.FromSeconds(5)));

                    Assert.False(window.IsVisible);
                    Assert.False(trayIcon.IsVisibleForTesting);
                    Assert.True(trayIcon.IsDisposedForTesting);
                    Assert.Equal(1, trayIcon.DisposeExecutionCountForTesting);

                    trayIcon.Dispose();
                    Assert.Equal(1, trayIcon.DisposeExecutionCountForTesting);
                }
                finally
                {
                    if (window.IsLoaded)
                    {
                        window.PrepareForApplicationShutdown();
                        window.Close();
                    }

                    trayIcon.Dispose();
                }
            });

            if (shellAvailable)
            {
                Assert.True(File.Exists(settingsFile), "真實系統匣『結束』沒有完成安全設定儲存流程。");
            }
        }
        finally
        {
            if (viewModel is not null)
            {
                await viewModel.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DiagnosticComposition_DoesNotCreateOrUseANativeTrayIcon()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;
        var factoryCalls = 0;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(temporary.Path);
                paths.EnsureCreated();
                viewModel = new MainWindowViewModel(paths);
                var window = MinecraftServerManager.App.App.CreateMainWindow(
                    viewModel,
                    enableSystemTray: false,
                    () =>
                    {
                        factoryCalls++;
                        return new FakeTrayIcon();
                    });

                window.Show();
                try
                {
                    window.WindowState = WindowState.Minimized;
                    DrainDispatcher();
                    Assert.Equal(0, factoryCalls);
                    Assert.True(window.IsVisible);
                    Assert.True(window.ShowInTaskbar);
                    Assert.Equal(WindowState.Minimized, window.WindowState);
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

    [Fact]
    public async Task TrayFactoryFailure_FallsBackToOrdinaryTaskbarMinimize()
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
                var window = MinecraftServerManager.App.App.CreateMainWindow(
                    viewModel,
                    enableSystemTray: true,
                    static () => throw new InvalidOperationException("native tray unavailable"));

                window.Show();
                try
                {
                    window.WindowState = WindowState.Minimized;
                    DrainDispatcher();
                    Assert.True(window.IsVisible);
                    Assert.True(window.ShowInTaskbar);
                    Assert.Equal(WindowState.Minimized, window.WindowState);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrayShowFailure_DoesNotHideTheTaskbarWindow(bool throwFromTryShow)
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
                var trayIcon = new FakeTrayIcon
                {
                    TryShowResult = false,
                    ThrowFromTryShow = throwFromTryShow
                };
                var window = CreateOffscreenWindow(viewModel, trayIcon);

                window.Show();
                try
                {
                    window.WindowState = WindowState.Minimized;
                    DrainDispatcher();
                    Assert.Equal(1, trayIcon.TryShowCount);
                    Assert.True(window.IsVisible);
                    Assert.True(window.ShowInTaskbar);
                    Assert.Equal(WindowState.Minimized, window.WindowState);
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

    [Fact]
    public async Task QueuedAndLateTrayCallbacks_CannotReopenAClosedWindow()
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
                var trayIcon = new FakeTrayIcon();
                var window = CreateOffscreenWindow(viewModel, trayIcon);

                window.Show();
                window.WindowState = WindowState.Minimized;
                DrainDispatcher();
                Assert.False(window.IsVisible);

                trayIcon.RequestOpen();
                window.PrepareForApplicationShutdown();
                window.Close();
                trayIcon.RequestCapturedOpen();
                trayIcon.RequestCapturedExit();
                DrainDispatcher();

                Assert.False(window.IsLoaded);
                Assert.False(window.IsVisible);
                Assert.Equal(1, trayIcon.DisposeCount);
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

    [Fact]
    public async Task PrepareForShutdown_FromWorkerThread_NeverSynchronouslyWaitsForDispatcher()
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
                var trayIcon = new FakeTrayIcon();
                var window = CreateOffscreenWindow(viewModel, trayIcon);
                window.Show();

                var worker = Task.Run(window.PrepareForApplicationShutdown);
                var returnedWithoutDispatcher = worker.Wait(TimeSpan.FromSeconds(2));
                try
                {
                    Assert.True(
                        returnedWithoutDispatcher,
                        "PrepareForApplicationShutdown 同步等待 Dispatcher，可能在結束競態中死鎖。");
                }
                finally
                {
                    DrainDispatcher();
                    window.PrepareForApplicationShutdown();
                    window.Close();
                }

                Assert.Equal(1, trayIcon.DisposeCount);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterCleanupFailure_DoesNotBlockTitleBarOrTraySafeShutdown(bool exitFromTray)
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;
        var settingsFile = Path.Combine(temporary.Path, "manager.json");

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(temporary.Path);
                paths.EnsureCreated();
                viewModel = new MainWindowViewModel(paths);
                var trayIcon = new FakeTrayIcon
                {
                    ThrowFromHide = true,
                    ThrowFromDispose = true
                };
                var window = CreateOffscreenWindow(viewModel, trayIcon);

                window.Show();
                if (exitFromTray)
                {
                    window.WindowState = WindowState.Minimized;
                    DrainDispatcher();
                    trayIcon.RequestExit();
                }
                else
                {
                    window.Close();
                }

                Assert.True(PumpDispatcherUntil(
                    () => !window.IsLoaded,
                    TimeSpan.FromSeconds(5)));
                Assert.False(window.IsVisible);
                Assert.Equal(1, trayIcon.DisposeCount);
            });

            Assert.True(File.Exists(settingsFile), "系統匣清理失敗中斷了既有安全關機流程。");
        }
        finally
        {
            if (viewModel is not null)
            {
                await viewModel.DisposeAsync();
            }
        }
    }

    private static MainWindow CreateOffscreenWindow(
        MainWindowViewModel viewModel,
        IMainWindowTrayIcon trayIcon)
        => new(viewModel, trayIcon)
        {
            Width = 1180,
            Height = 760,
            Left = -10_000,
            Top = -10_000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

    private static bool PumpDispatcherUntil(Func<bool> predicate, TimeSpan timeout)
    {
        if (predicate())
        {
            return true;
        }

        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + timeout;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        timer.Tick += (_, _) =>
        {
            if (!predicate() && DateTime.UtcNow < deadline)
            {
                return;
            }

            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        return predicate();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            () => frame.Continue = false,
            DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }

    private sealed class FakeTrayIcon : IMainWindowTrayIcon
    {
        private EventHandler? _openRequested;
        private EventHandler? _exitRequested;
        private EventHandler? _capturedOpenRequested;
        private EventHandler? _capturedExitRequested;

        public event EventHandler? OpenRequested
        {
            add
            {
                _openRequested += value;
                _capturedOpenRequested += value;
            }
            remove => _openRequested -= value;
        }

        public event EventHandler? ExitRequested
        {
            add
            {
                _exitRequested += value;
                _capturedExitRequested += value;
            }
            remove => _exitRequested -= value;
        }

        public bool TryShowResult { get; init; } = true;

        public bool ThrowFromTryShow { get; init; }

        public bool ThrowFromHide { get; init; }

        public bool ThrowFromDispose { get; init; }

        public bool IsShown { get; private set; }

        public int TryShowCount { get; private set; }

        public int HideCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool TryShow()
        {
            TryShowCount++;
            if (ThrowFromTryShow)
            {
                throw new InvalidOperationException("simulated TryShow failure");
            }

            IsShown = TryShowResult;
            return TryShowResult;
        }

        public void Hide()
        {
            HideCount++;
            IsShown = false;
            if (ThrowFromHide)
            {
                throw new InvalidOperationException("simulated Hide failure");
            }
        }

        public void RequestOpen() => _openRequested?.Invoke(this, EventArgs.Empty);

        public void RequestExit() => _exitRequested?.Invoke(this, EventArgs.Empty);

        public void RequestCapturedOpen() => _capturedOpenRequested?.Invoke(this, EventArgs.Empty);

        public void RequestCapturedExit() => _capturedExitRequested?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            DisposeCount++;
            if (ThrowFromDispose)
            {
                throw new InvalidOperationException("simulated Dispose failure");
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? className, string? windowName);
    }
}
