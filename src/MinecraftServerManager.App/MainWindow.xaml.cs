using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App;

internal readonly record struct MainWindowLayoutSnapshot(
    WindowState WindowState,
    WindowState TrayRestoreWindowState,
    Rect NormalBounds);

public partial class MainWindow : Window
{
    internal const double DesignMinimumWindowWidth = 1120;
    internal const double DesignMinimumWindowHeight = 700;
    private static readonly TimeSpan DefaultWindowSizePersistenceDebounce =
        TimeSpan.FromMilliseconds(500);
    private bool _allowClose;
    private bool _shutdownInProgress;
    private bool _trayIconDisposed;
    private bool _isClosed;
    private WindowState _restoreWindowState = WindowState.Normal;
    private readonly IMainWindowTrayIcon _trayIcon;
    private readonly DispatcherTimer _windowSizePersistenceTimer;
    private readonly Dictionary<ListBox, ConsoleAutoScrollState> _consoleAutoScrollStates = [];
    private Size? _lastNormalWindowSize;
    private Size? _pendingNormalWindowSize;
    private bool _windowSizeTrackingInitialized;
    private bool _isApplyingWorkAreaConstraints;

    public MainWindow(MainWindowViewModel viewModel)
        : this(viewModel, DisabledMainWindowTrayIcon.Instance)
    {
    }

    internal MainWindow(
        MainWindowViewModel viewModel,
        IMainWindowTrayIcon trayIcon,
        TimeSpan? windowSizePersistenceDebounce = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(trayIcon);
        _trayIcon = trayIcon;
        InitializeComponent();
        var debounce = windowSizePersistenceDebounce ?? DefaultWindowSizePersistenceDebounce;
        if (debounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSizePersistenceDebounce));
        }
        _windowSizePersistenceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = debounce,
        };
        _windowSizePersistenceTimer.Tick += OnWindowSizePersistenceTimerTick;
        DataContext = viewModel;
        viewModel.ClientWorkspace.HideLauncherRequested += OnClientHideLauncherRequested;
        viewModel.ClientWorkspace.RestoreLauncherRequested += OnClientRestoreLauncherRequested;
        _trayIcon.OpenRequested += OnTrayOpenRequested;
        _trayIcon.ExitRequested += OnTrayExitRequested;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var requestedWidth = DataContext is MainWindowViewModel viewModel
            ? viewModel.WindowWidth
            : ResolveCurrentWidth();
        var requestedHeight = DataContext is MainWindowViewModel currentViewModel
            ? currentViewModel.WindowHeight
            : ResolveCurrentHeight();
        var normalBounds = PreviewNormalLayout(requestedWidth, requestedHeight);
        _windowSizeTrackingInitialized = true;
        _lastNormalWindowSize = ResolvePersistableNormalSize(normalBounds);
        if (_lastNormalWindowSize is { } clampedSize
            && (Math.Abs(clampedSize.Width - requestedWidth) >= 0.5
                || Math.Abs(clampedSize.Height - requestedHeight) >= 0.5))
        {
            _pendingNormalWindowSize = clampedSize;
            _windowSizePersistenceTimer.Start();
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_windowSizeTrackingInitialized
            || _isClosed
            || _shutdownInProgress
            || _isApplyingWorkAreaConstraints
            || WindowState != WindowState.Normal)
        {
            return;
        }

        var workArea = ResolveCurrentWorkArea();
        ApplyEffectiveMinimumWindowSize(workArea);
        var currentWidth = ResolveCurrentWidth();
        var currentHeight = ResolveCurrentHeight();
        if (currentWidth > workArea.Width + 0.5 || currentHeight > workArea.Height + 0.5)
        {
            ApplyCurrentWorkAreaConstraints(workArea, currentWidth, currentHeight);
            return;
        }

        var size = ResolvePersistableNormalSize(
            new Rect(Left, Top, currentWidth, currentHeight),
            workArea);
        if (size is null)
        {
            return;
        }

        _lastNormalWindowSize = size;
        _pendingNormalWindowSize = size;
        _windowSizePersistenceTimer.Stop();
        _windowSizePersistenceTimer.Start();
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (!_windowSizeTrackingInitialized
            || _isClosed
            || _shutdownInProgress
            || _isApplyingWorkAreaConstraints
            || WindowState != WindowState.Normal)
        {
            return;
        }

        var workArea = ResolveCurrentWorkArea();
        ApplyEffectiveMinimumWindowSize(workArea);
        var currentWidth = ResolveCurrentWidth();
        var currentHeight = ResolveCurrentHeight();
        if (currentWidth > workArea.Width + 0.5 || currentHeight > workArea.Height + 0.5)
        {
            ApplyCurrentWorkAreaConstraints(workArea, currentWidth, currentHeight);
        }
    }

    private void ApplyCurrentWorkAreaConstraints(
        Rect workArea,
        double requestedWidth,
        double requestedHeight)
    {
        _isApplyingWorkAreaConstraints = true;
        try
        {
            var bounds = PreviewNormalLayout(requestedWidth, requestedHeight, workArea);
            var size = ResolvePersistableNormalSize(bounds, workArea);
            if (size is null)
            {
                return;
            }

            _lastNormalWindowSize = size;
            _pendingNormalWindowSize = size;
            _windowSizePersistenceTimer.Stop();
            _windowSizePersistenceTimer.Start();
        }
        finally
        {
            _isApplyingWorkAreaConstraints = false;
        }
    }

    private async void OnWindowSizePersistenceTimerTick(object? sender, EventArgs e)
    {
        _windowSizePersistenceTimer.Stop();
        try
        {
            await FlushPendingWindowSizePersistenceAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Keep the pending value for the authoritative close-time retry. A transient disk
            // failure must not tear down the GUI merely because the user resized a window.
            System.Diagnostics.Debug.WriteLine(error);
        }
    }

    internal async Task FlushPendingWindowSizePersistenceAsync()
    {
        var pending = _pendingNormalWindowSize;
        if (pending is null || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        await viewModel.PersistNormalWindowSizeAsync(pending.Value.Width, pending.Value.Height);
        if (_pendingNormalWindowSize == pending)
        {
            _pendingNormalWindowSize = null;
        }
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
        => PreviewNormalLayout(requestedWidth, requestedHeight, ResolveCurrentWorkArea());

    internal Rect PreviewNormalLayout(
        double requestedWidth,
        double requestedHeight,
        Rect workArea)
    {
        workArea = NormalizeWorkArea(workArea);
        ApplyEffectiveMinimumWindowSize(workArea);
        var size = ClampNormalSizeToWorkArea(
            requestedWidth,
            requestedHeight,
            workArea,
            ResolveCurrentWidth(),
            ResolveCurrentHeight());
        var width = size.Width;
        var height = size.Height;

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

    internal static Size ClampNormalSizeToWorkArea(
        double requestedWidth,
        double requestedHeight,
        Rect workArea,
        double fallbackWidth = DesignMinimumWindowWidth,
        double fallbackHeight = DesignMinimumWindowHeight)
    {
        workArea = NormalizeWorkArea(workArea);
        var maximumWidth = Math.Max(1d, workArea.Width);
        var maximumHeight = Math.Max(1d, workArea.Height);
        var minimumWidth = Math.Min(DesignMinimumWindowWidth, maximumWidth);
        var minimumHeight = Math.Min(DesignMinimumWindowHeight, maximumHeight);
        var candidateWidth = double.IsFinite(requestedWidth) && requestedWidth > 0
            ? requestedWidth
            : fallbackWidth;
        var candidateHeight = double.IsFinite(requestedHeight) && requestedHeight > 0
            ? requestedHeight
            : fallbackHeight;
        return new Size(
            Math.Clamp(candidateWidth, minimumWidth, maximumWidth),
            Math.Clamp(candidateHeight, minimumHeight, maximumHeight));
    }

    private void ApplyEffectiveMinimumWindowSize(Rect workArea)
    {
        workArea = NormalizeWorkArea(workArea);
        SetCurrentValue(
            MinWidthProperty,
            Math.Min(DesignMinimumWindowWidth, Math.Max(1d, workArea.Width)));
        SetCurrentValue(
            MinHeightProperty,
            Math.Min(DesignMinimumWindowHeight, Math.Max(1d, workArea.Height)));
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
        if (_windowSizeTrackingInitialized
            && WindowState != WindowState.Normal
            && IsUsableBounds(RestoreBounds))
        {
            _lastNormalWindowSize = ResolvePersistableNormalSize(RestoreBounds);
        }

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

    private void OnClientHideLauncherRequested(object? sender, EventArgs e)
    {
        if (_isClosed || _shutdownInProgress)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => OnClientHideLauncherRequested(sender, e),
                DispatcherPriority.Normal);
            return;
        }

        ApplyClientLauncherWindowTransition(ClientLauncherWindowTransition.Minimize);
    }

    private void OnClientRestoreLauncherRequested(object? sender, EventArgs e)
    {
        if (_isClosed || _shutdownInProgress)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => OnClientRestoreLauncherRequested(sender, e),
                DispatcherPriority.Normal);
            return;
        }

        ApplyClientLauncherWindowTransition(ClientLauncherWindowTransition.Restore);
    }

    internal void ApplyClientLauncherWindowTransition(
        ClientLauncherWindowTransition transition)
    {
        if (!Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Client launcher window transitions must run on the window dispatcher.");
        }

        if (_isClosed || _shutdownInProgress)
        {
            return;
        }

        switch (transition)
        {
            case ClientLauncherWindowTransition.None:
                return;
            case ClientLauncherWindowTransition.Minimize:
                WindowState = WindowState.Minimized;
                return;
            case ClientLauncherWindowTransition.Restore:
                RestoreFromTray();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition), transition, null);
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
        PrimaryDisplayWindowPlacement.ActivateWhenInteractive(this);
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
            _windowSizePersistenceTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                _pendingNormalWindowSize = ResolvePersistableNormalSize(
                    new Rect(Left, Top, ResolveCurrentWidth(), ResolveCurrentHeight()));
            }
            else if (_lastNormalWindowSize is { } lastNormalWindowSize)
            {
                _pendingNormalWindowSize = lastNormalWindowSize;
            }
            await FlushPendingWindowSizePersistenceAsync();
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
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ClientWorkspace.HideLauncherRequested -= OnClientHideLauncherRequested;
            viewModel.ClientWorkspace.RestoreLauncherRequested -= OnClientRestoreLauncherRequested;
        }
        _windowSizePersistenceTimer.Stop();
        _windowSizePersistenceTimer.Tick -= OnWindowSizePersistenceTimerTick;
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

    private Size? ResolvePersistableNormalSize(Rect normalBounds)
        => ResolvePersistableNormalSize(normalBounds, ResolveCurrentWorkArea());

    private static Size? ResolvePersistableNormalSize(Rect normalBounds, Rect workArea)
    {
        if (!IsUsableBounds(normalBounds))
        {
            return null;
        }

        var normalized = ClampNormalSizeToWorkArea(
            normalBounds.Width,
            normalBounds.Height,
            workArea);
        return new Size(
            Math.Round(normalized.Width),
            Math.Round(normalized.Height));
    }

    private static Rect NormalizeWorkArea(Rect workArea)
        => IsUsableBounds(workArea)
            ? workArea
            : new Rect(
                SystemParameters.WorkArea.Left,
                SystemParameters.WorkArea.Top,
                Math.Max(1d, SystemParameters.WorkArea.Width),
                Math.Max(1d, SystemParameters.WorkArea.Height));

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
