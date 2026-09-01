using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using FormsScreen = System.Windows.Forms.Screen;

namespace MinecraftServerManager.App.Infrastructure;

/// <summary>
/// Gives every product window deterministic Windows multi-monitor placement.
/// Top-level windows start on the primary display; owned windows follow their owner. The Win32
/// move is performed after the HWND exists, so coordinates remain physical-pixel correct under
/// PerMonitorV2 DPI awareness.
/// </summary>
internal static class PrimaryDisplayWindowPlacement
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(PrimaryDisplayWindowPlacement),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty PlacementStateProperty = DependencyProperty.RegisterAttached(
        "PlacementState",
        typeof(PlacementState),
        typeof(PrimaryDisplayWindowPlacement));

    /// <summary>
    /// Diagnostic executables and WPF tests set this before constructing windows. It prevents
    /// automated rendering/smoke windows from taking focus away from the user's foreground app.
    /// </summary>
    internal static bool SuppressActivationForNewWindows { get; set; }

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>
    /// Resolves the current WPF owner for native file/folder dialogs. Supplying an owner makes the
    /// Windows common dialog use the same monitor as the product instead of the mouse monitor.
    /// </summary>
    internal static Window? ResolveDialogOwner()
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Windows
                   .OfType<Window>()
                   .FirstOrDefault(static candidate => candidate.IsVisible && candidate.IsActive)
               ?? (application.MainWindow is { IsVisible: true } mainWindow ? mainWindow : null);
    }

    internal static bool? ShowDialogOnProductDisplay(CommonDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var owner = ResolveDialogOwner();
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    internal static bool IsDisplaySettingsSubscribedForTesting(Window window)
        => window.GetValue(PlacementStateProperty) is PlacementState
        {
            IsDisplaySettingsSubscribed: true,
        };

    internal static bool ShouldClampAfterDisplayChange(bool isLoaded, WindowState windowState)
        => isLoaded && windowState == WindowState.Normal;

    internal static bool ActivateWhenInteractive(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return !SuppressActivationForNewWindows && window.Activate();
    }

    internal static Rect CalculateCenteredBounds(Rect windowBounds, Rect anchorBounds, Rect workArea)
    {
        workArea = NormalizeBounds(workArea, new Rect(0, 0, 1, 1));
        windowBounds = NormalizeBounds(windowBounds, new Rect(0, 0, 1, 1));
        anchorBounds = NormalizeBounds(anchorBounds, workArea);

        var left = anchorBounds.Left + ((anchorBounds.Width - windowBounds.Width) / 2d);
        var top = anchorBounds.Top + ((anchorBounds.Height - windowBounds.Height) / 2d);
        return ClampBoundsToWorkArea(
            new Rect(left, top, windowBounds.Width, windowBounds.Height),
            workArea);
    }

    internal static Rect ClampBoundsToWorkArea(Rect windowBounds, Rect workArea)
    {
        workArea = NormalizeBounds(workArea, new Rect(0, 0, 1, 1));
        windowBounds = NormalizeBounds(windowBounds, new Rect(workArea.Location, new Size(1, 1)));

        var maximumLeft = Math.Max(workArea.Left, workArea.Right - windowBounds.Width);
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - windowBounds.Height);
        return new Rect(
            Math.Clamp(windowBounds.Left, workArea.Left, maximumLeft),
            Math.Clamp(windowBounds.Top, workArea.Top, maximumTop),
            windowBounds.Width,
            windowBounds.Height);
    }

    private static Rect NormalizeBounds(Rect candidate, Rect fallback)
        => !candidate.IsEmpty
           && double.IsFinite(candidate.Left)
           && double.IsFinite(candidate.Top)
           && double.IsFinite(candidate.Width)
           && double.IsFinite(candidate.Height)
           && candidate.Width > 0
           && candidate.Height > 0
            ? candidate
            : fallback;

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Window window)
        {
            throw new InvalidOperationException(
                $"{nameof(PrimaryDisplayWindowPlacement)} can only be attached to a Window.");
        }

        if ((bool)e.NewValue)
        {
            if (window.GetValue(PlacementStateProperty) is not PlacementState)
            {
                var state = new PlacementState(window);
                window.SetValue(PlacementStateProperty, state);
                state.Attach();
            }

            return;
        }

        if (window.GetValue(PlacementStateProperty) is PlacementState existing)
        {
            existing.Dispose();
            window.ClearValue(PlacementStateProperty);
        }
    }

    private sealed class PlacementState : IDisposable
    {
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;

        private readonly Window _window;
        private WindowStartupLocation _requestedStartupLocation;
        private bool _startupLocationCaptured;
        private bool _displaySettingsSubscribed;
        private bool _disposed;

        public bool IsDisplaySettingsSubscribed => _displaySettingsSubscribed;

        public PlacementState(Window window)
        {
            _window = window;
        }

        public void Attach()
        {
            if (SuppressActivationForNewWindows)
            {
                _window.ShowActivated = false;
            }

            _window.SourceInitialized += OnSourceInitialized;
            _window.Loaded += OnLoaded;
            _window.ContentRendered += OnContentRendered;
            _window.Closed += OnClosed;
            SubscribeToDisplaySettingsChangesIfSourceExists();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Loaded -= OnLoaded;
            _window.ContentRendered -= OnContentRendered;
            _window.Closed -= OnClosed;
            if (_displaySettingsSubscribed)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _displaySettingsSubscribed = false;
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            SubscribeToDisplaySettingsChangesIfSourceExists();
            CaptureStartupLocation();
            PlaceForStartup();
        }

        private void SubscribeToDisplaySettingsChangesIfSourceExists()
        {
            if (_disposed || _displaySettingsSubscribed)
            {
                return;
            }

            // SystemEvents is static and would otherwise keep a constructed-but-never-shown
            // Window alive forever. Subscribe only after the native source exists; Closed then
            // deterministically releases the subscription.
            if (new WindowInteropHelper(_window).Handle == IntPtr.Zero)
            {
                return;
            }

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _displaySettingsSubscribed = true;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            CaptureStartupLocation();
            PlaceForStartup();
        }

        private void OnContentRendered(object? sender, EventArgs e)
        {
            // SizeToContent windows acquire their final HWND size after layout. One final move keeps
            // them centered without tracking or overriding later user-driven movement.
            _window.ContentRendered -= OnContentRendered;
            PlaceForStartup();
        }

        private void CaptureStartupLocation()
        {
            if (_startupLocationCaptured)
            {
                return;
            }

            _requestedStartupLocation = _window.WindowStartupLocation;
            _startupLocationCaptured = true;
            if (_requestedStartupLocation != WindowStartupLocation.Manual)
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }

        private void PlaceForStartup()
        {
            if (_disposed
                || !_startupLocationCaptured
                || _requestedStartupLocation == WindowStartupLocation.Manual)
            {
                return;
            }

            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero || !TryGetWindowBounds(handle, out var windowBounds))
            {
                return;
            }

            var owner = _requestedStartupLocation == WindowStartupLocation.CenterOwner
                ? _window.Owner
                : null;
            var ownerHandle = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            var targetScreen = ownerHandle != IntPtr.Zero
                ? FormsScreen.FromHandle(ownerHandle)
                : FormsScreen.PrimaryScreen;
            if (targetScreen is null)
            {
                return;
            }

            var workArea = ToRect(targetScreen.WorkingArea);
            var anchorBounds = ownerHandle != IntPtr.Zero
                               && owner?.WindowState != WindowState.Minimized
                               && TryGetWindowBounds(ownerHandle, out var ownerBounds)
                ? ownerBounds
                : workArea;
            MoveWithoutActivation(
                handle,
                CalculateCenteredBounds(windowBounds, anchorBounds, workArea));
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (_disposed
                || _window.Dispatcher.HasShutdownStarted
                || _window.Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                _ = _window.Dispatcher.BeginInvoke(
                    ClampToVisibleWorkArea,
                    DispatcherPriority.Background);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shutdown won the display-change race.
            }
        }

        private void ClampToVisibleWorkArea()
        {
            // Windows owns monitor migration for minimized and maximized states. Moving their
            // special HWND rectangles can offset a maximized window or corrupt its restore bounds.
            if (_disposed
                || !ShouldClampAfterDisplayChange(_window.IsLoaded, _window.WindowState))
            {
                return;
            }

            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero || !TryGetWindowBounds(handle, out var windowBounds))
            {
                return;
            }

            var ownerHandle = _window.Owner is null
                ? IntPtr.Zero
                : new WindowInteropHelper(_window.Owner).Handle;
            var targetScreen = ownerHandle != IntPtr.Zero
                ? FormsScreen.FromHandle(ownerHandle)
                : FormsScreen.FromHandle(handle);
            var workArea = ToRect(targetScreen.WorkingArea);
            MoveWithoutActivation(handle, ClampBoundsToWorkArea(windowBounds, workArea));
        }

        private void OnClosed(object? sender, EventArgs e)
            => Dispose();

        private static void MoveWithoutActivation(IntPtr handle, Rect bounds)
        {
            _ = SetWindowPos(
                handle,
                IntPtr.Zero,
                checked((int)Math.Round(bounds.Left)),
                checked((int)Math.Round(bounds.Top)),
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        }

        private static bool TryGetWindowBounds(IntPtr handle, out Rect bounds)
        {
            if (GetWindowRect(handle, out var nativeBounds)
                && nativeBounds.Right > nativeBounds.Left
                && nativeBounds.Bottom > nativeBounds.Top)
            {
                bounds = new Rect(
                    nativeBounds.Left,
                    nativeBounds.Top,
                    nativeBounds.Right - nativeBounds.Left,
                    nativeBounds.Bottom - nativeBounds.Top);
                return true;
            }

            bounds = Rect.Empty;
            return false;
        }

        private static Rect ToRect(System.Drawing.Rectangle rectangle)
            => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
