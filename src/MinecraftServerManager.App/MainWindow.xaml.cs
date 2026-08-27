using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App;

internal readonly record struct MainWindowLayoutSnapshot(
    WindowState WindowState,
    WindowState TrayRestoreWindowState,
    Rect NormalBounds);

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _shutdownInProgress;
    private bool _trayIconDisposed;
    private bool _isClosed;
    private WindowState _restoreWindowState = WindowState.Normal;
    private readonly IMainWindowTrayIcon _trayIcon;
    private readonly Dictionary<ListBox, ConsoleAutoScrollState> _consoleAutoScrollStates = [];

    public MainWindow(MainWindowViewModel viewModel)
        : this(viewModel, DisabledMainWindowTrayIcon.Instance)
    {
    }

    internal MainWindow(MainWindowViewModel viewModel, IMainWindowTrayIcon trayIcon)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(trayIcon);
        _trayIcon = trayIcon;
        InitializeComponent();
        DataContext = viewModel;
        _trayIcon.OpenRequested += OnTrayOpenRequested;
        _trayIcon.ExitRequested += OnTrayExitRequested;
    }

    internal void PrepareForApplicationShutdown()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                _ = Dispatcher.BeginInvoke(
                    PrepareForApplicationShutdown,
                    DispatcherPriority.Send);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shutdown won the race. Closed/OnExit performs the final cleanup.
            }
            return;
        }

        _allowClose = true;
        DisposeTrayIcon();
    }

    /// <summary>
    /// Captures the real normal-window bounds before settings preview changes WindowState. Width
    /// bindings contain persisted preferences, while RestoreBounds contains the user's current
    /// resize session; both maximized and normal windows therefore need a WPF-level snapshot.
    /// </summary>
    internal MainWindowLayoutSnapshot CaptureLayoutForSettingsPreview()
    {
        var normalBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ResolveCurrentWidth(), ResolveCurrentHeight())
            : RestoreBounds;
        if (!IsUsableBounds(normalBounds))
        {
            var workArea = ResolveCurrentWorkArea();
            normalBounds = new Rect(
                double.IsFinite(Left) ? Left : workArea.Left,
                double.IsFinite(Top) ? Top : workArea.Top,
                ResolveCurrentWidth(),
                ResolveCurrentHeight());
        }

        return new MainWindowLayoutSnapshot(WindowState, _restoreWindowState, normalBounds);
    }

    /// <summary>
    /// Makes a requested settings size visually effective. A maximized window ignores Width and
    /// Height visually, so normalize it first. SetCurrentValue keeps the XAML bindings attached.
    /// </summary>
    internal Rect PreviewNormalLayout(double requestedWidth, double requestedHeight)
    {
        var workArea = ResolveCurrentWorkArea();
        var minimumWidth = Math.Min(Math.Max(1, MinWidth), Math.Max(1, workArea.Width));
        var minimumHeight = Math.Min(Math.Max(1, MinHeight), Math.Max(1, workArea.Height));
        var width = Math.Clamp(
            double.IsFinite(requestedWidth) ? requestedWidth : ResolveCurrentWidth(),
            minimumWidth,
            Math.Max(minimumWidth, workArea.Width));
        var height = Math.Clamp(
            double.IsFinite(requestedHeight) ? requestedHeight : ResolveCurrentHeight(),
            minimumHeight,
            Math.Max(minimumHeight, workArea.Height));

        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        SetCurrentValue(WidthProperty, width);
        SetCurrentValue(HeightProperty, height);
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - width);
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - height);
        var left = double.IsFinite(Left)
            ? Math.Clamp(Left, workArea.Left, maximumLeft)
            : workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
        var top = double.IsFinite(Top)
            ? Math.Clamp(Top, workArea.Top, maximumTop)
            : workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        SetCurrentValue(LeftProperty, left);
        SetCurrentValue(TopProperty, top);
        return new Rect(left, top, width, height);
    }

    internal void RestoreLayoutAfterSettingsPreview(MainWindowLayoutSnapshot snapshot)
    {
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        if (IsUsableBounds(snapshot.NormalBounds))
        {
            SetCurrentValue(WidthProperty, snapshot.NormalBounds.Width);
            SetCurrentValue(HeightProperty, snapshot.NormalBounds.Height);
            SetCurrentValue(LeftProperty, snapshot.NormalBounds.Left);
            SetCurrentValue(TopProperty, snapshot.NormalBounds.Top);
        }

        _restoreWindowState = snapshot.TrayRestoreWindowState;
        WindowState = snapshot.WindowState;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _restoreWindowState = WindowState;
            return;
        }

        if (_isClosed || _trayIconDisposed || _allowClose || _shutdownInProgress)
        {
            return;
        }

        try
        {
            if (!_trayIcon.TryShow())
            {
                SafeHideTrayIcon();
                return;
            }
        }
        catch (Exception)
        {
            SafeHideTrayIcon();
            return;
        }

        try
        {
            Hide();
        }
        catch (Exception)
        {
            SafeHideTrayIcon();
        }
    }

    private void OnTrayOpenRequested(object? sender, EventArgs e)
        => DispatchTrayAction(RestoreFromTray);

    private void OnTrayExitRequested(object? sender, EventArgs e)
        => DispatchTrayAction(ExitFromTray);

    private void DispatchTrayAction(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(
                () =>
                {
                    if (CanExecuteTrayAction())
                    {
                        action();
                    }
                },
                DispatcherPriority.Normal);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown won the race; never resurrect a closing window.
        }
    }

    private void RestoreFromTray()
    {
        if (!CanExecuteTrayAction())
        {
            return;
        }

        Show();
        WindowState = _restoreWindowState;
        SafeHideTrayIcon();
        Activate();
    }

    private void ExitFromTray()
    {
        if (!CanExecuteTrayAction())
        {
            return;
        }

        RestoreFromTray();
        Close();
    }

    private bool CanExecuteTrayAction()
        => !_isClosed
           && !_trayIconDisposed
           && !_allowClose
           && !_shutdownInProgress
           && IsLoaded
           && !Dispatcher.HasShutdownStarted
           && !Dispatcher.HasShutdownFinished;

    private void OnConsoleListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox listBox || _consoleAutoScrollStates.ContainsKey(listBox)) return;
        var state = new ConsoleAutoScrollState(listBox);
        _consoleAutoScrollStates.Add(listBox, state);
        state.Attach();
    }

    private void OnConsoleListUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox listBox || !_consoleAutoScrollStates.Remove(listBox, out var state)) return;
        state.Dispose();
    }

    private void OnConsoleListDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ListBox listBox && _consoleAutoScrollStates.TryGetValue(listBox, out var state))
        {
            listBox.Dispatcher.BeginInvoke(state.AttachItemsSource, DispatcherPriority.DataBind);
        }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || DataContext is not MainWindowViewModel viewModel)
        {
            DisposeTrayIcon();
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        if (viewModel.HasRunningServers && !viewModel.KeepsRunningServersOnGuiExit)
        {
            var answer = DarkMessageBox.Show(
                this,
                L("main.close.compatibilityRunning.message"),
                L("main.close.compatibilityRunning.title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (viewModel.HasActiveBackgroundJobs)
        {
            var answer = DarkMessageBox.Show(
                this,
                L("main.close.backgroundJobs.message"),
                L("main.close.backgroundJobs.title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _shutdownInProgress = true;
        IsEnabled = false;
        try
        {
            await viewModel.ShutdownAsync();
            _allowClose = true;
            DisposeTrayIcon();
            await Dispatcher.InvokeAsync(Close, DispatcherPriority.ApplicationIdle);
        }
        catch (Exception exception)
        {
            IsEnabled = true;
            _shutdownInProgress = false;
            DarkMessageBox.Show(
                this,
                exception.Message,
                L("main.close.failedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        DisposeTrayIcon();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIconDisposed)
        {
            return;
        }

        _trayIconDisposed = true;
        try
        {
            _trayIcon.OpenRequested -= OnTrayOpenRequested;
        }
        catch (Exception)
        {
        }

        try
        {
            _trayIcon.ExitRequested -= OnTrayExitRequested;
        }
        catch (Exception)
        {
        }

        SafeHideTrayIcon();
        try
        {
            _trayIcon.Dispose();
        }
        catch (Exception)
        {
            // Optional shell integration must never prevent the existing safe shutdown path.
        }
    }

    private void SafeHideTrayIcon()
    {
        try
        {
            _trayIcon.Hide();
        }
        catch (Exception)
        {
            // Enforce no-throw behavior even for an injected or partially initialized adapter.
        }
    }

    private double ResolveCurrentWidth()
        => ActualWidth > 0 && double.IsFinite(ActualWidth)
            ? ActualWidth
            : double.IsFinite(Width) && Width > 0
                ? Width
                : Math.Max(MinWidth, 1120);

    private double ResolveCurrentHeight()
        => ActualHeight > 0 && double.IsFinite(ActualHeight)
            ? ActualHeight
            : double.IsFinite(Height) && Height > 0
                ? Height
                : Math.Max(MinHeight, 700);

    private Rect ResolveCurrentWorkArea()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                var workingArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                var dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
                {
                    return new Rect(
                        workingArea.Left / dpi.DpiScaleX,
                        workingArea.Top / dpi.DpiScaleY,
                        workingArea.Width / dpi.DpiScaleX,
                        workingArea.Height / dpi.DpiScaleY);
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A not-yet-created HWND or a disappearing monitor falls back to WPF's work area.
        }

        return SystemParameters.WorkArea;
    }

    private static bool IsUsableBounds(Rect bounds)
        => !bounds.IsEmpty
           && double.IsFinite(bounds.Left)
           && double.IsFinite(bounds.Top)
           && double.IsFinite(bounds.Width)
           && double.IsFinite(bounds.Height)
           && bounds.Width > 0
           && bounds.Height > 0;

    private static string L(string key, params object?[] arguments)
        => LocalizationService.Current.Get(key, arguments);

    private sealed class ConsoleAutoScrollState : IDisposable
    {
        private const double BottomTolerance = 2;
        private readonly ListBox _listBox;
        private ScrollViewer? _scrollViewer;
        private INotifyCollectionChanged? _collection;
        private bool _followTail = true;
        private bool _scrollPending;
        private bool _disposed;

        public ConsoleAutoScrollState(ListBox listBox)
        {
            _listBox = listBox;
        }

        public void Attach()
        {
            AttachItemsSource();
            _listBox.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                _scrollViewer = FindVisualChild<ScrollViewer>(_listBox);
                if (_scrollViewer is not null)
                {
                    _scrollViewer.ScrollChanged += OnScrollChanged;
                }

                _followTail = true;
                ScrollToEndIfFollowing();
            }, DispatcherPriority.Loaded);
        }

        public void AttachItemsSource()
        {
            if (_disposed) return;
            if (_collection is not null)
            {
                _collection.CollectionChanged -= OnCollectionChanged;
            }

            _collection = _listBox.ItemsSource as INotifyCollectionChanged;
            if (_collection is not null)
            {
                _collection.CollectionChanged += OnCollectionChanged;
            }

            _followTail = true;
            ScrollToEndIfFollowing();
        }

        public void Dispose()
        {
            _disposed = true;
            if (_collection is not null)
            {
                _collection.CollectionChanged -= OnCollectionChanged;
            }

            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_followTail)
            {
                ScrollToEndIfFollowing();
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrollViewer is null) return;

            // An extent-only change means new console content arrived. Preserve the previous
            // follow state until the queued ScrollToEnd runs. User wheel/drag changes VerticalOffset.
            if (Math.Abs(e.VerticalChange) < double.Epsilon && Math.Abs(e.ExtentHeightChange) > double.Epsilon)
            {
                return;
            }

            _followTail = _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset <= BottomTolerance;
        }

        private void ScrollToEndIfFollowing()
        {
            if (_disposed || !_followTail || _scrollPending) return;
            _scrollPending = true;
            _listBox.Dispatcher.BeginInvoke(() =>
            {
                _scrollPending = false;
                if (!_disposed && _followTail)
                {
                    _scrollViewer?.ScrollToEnd();
                }
            }, DispatcherPriority.Background);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) return match;
                var nested = FindVisualChild<T>(child);
                if (nested is not null) return nested;
            }

            return null;
        }
    }
}
