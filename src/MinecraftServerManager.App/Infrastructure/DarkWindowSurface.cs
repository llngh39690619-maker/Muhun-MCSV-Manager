using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MinecraftServerManager.App.Infrastructure;

/// <summary>
/// Applies the application dark surface to the native HWND as soon as it is created. WPF's
/// managed Background is not enough on its own: while the compositor is waiting for a frame,
/// Windows can otherwise expose the source's default (white) surface during show, resize or a
/// temporarily busy UI thread.
/// </summary>
public static class DarkWindowSurface
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const int WmEraseBackground = 0x0014;

    private static readonly ConditionalWeakTable<Window, SurfaceRegistration> Registrations = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DarkWindowSurface),
        new FrameworkPropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not Window window)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            Enable(window);
        }
        else
        {
            Disable(window);
        }
    }

    private static void Enable(Window window)
    {
        if (Registrations.TryGetValue(window, out _))
        {
            return;
        }

        var registration = new SurfaceRegistration(window);
        Registrations.Add(window, registration);
        registration.Attach();
    }

    private static void Disable(Window window)
    {
        if (!Registrations.TryGetValue(window, out var registration))
        {
            return;
        }

        registration.Detach();
        Registrations.Remove(window);
    }

    private static Color ResolveColor(Window window)
        => window.Background is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0x10, 0x13, 0x18);

    private static int ToColorRef(Color color)
        => color.R | color.G << 8 | color.B << 16;

    private sealed class SurfaceRegistration(Window window)
    {
        private readonly Window _window = window;
        private readonly DependencyPropertyDescriptor? _backgroundDescriptor =
            DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(Window));
        private HwndSource? _source;

        public void Attach()
        {
            _window.SourceInitialized += OnSourceInitialized;
            _window.Closed += OnClosed;
            _backgroundDescriptor?.AddValueChanged(_window, OnBackgroundChanged);
            if (PresentationSource.FromVisual(_window) is HwndSource source)
            {
                AttachSource(source);
            }
        }

        public void Detach()
        {
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnClosed;
            _backgroundDescriptor?.RemoveValueChanged(_window, OnBackgroundChanged);
            if (_source is not null)
            {
                _source.RemoveHook(WindowHook);
                _source = null;
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs args)
        {
            if (PresentationSource.FromVisual(_window) is HwndSource source)
            {
                AttachSource(source);
            }
        }

        private void AttachSource(HwndSource source)
        {
            if (ReferenceEquals(_source, source))
            {
                ApplyNativeTheme();
                return;
            }

            _source?.RemoveHook(WindowHook);
            _source = source;
            _source.AddHook(WindowHook);
            ApplyNativeTheme();
        }

        private void OnBackgroundChanged(object? sender, EventArgs args) => ApplyNativeTheme();

        private void OnClosed(object? sender, EventArgs args)
        {
            Detach();
            Registrations.Remove(_window);
        }

        private void ApplyNativeTheme()
        {
            if (_source is null || _source.IsDisposed)
            {
                return;
            }

            var color = ResolveColor(_window);
            _source.CompositionTarget.BackgroundColor = color;

            var handle = _source.Handle;
            var enabled = 1;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
            }

            var captionColor = ToColorRef(color);
            var textColor = ToColorRef(Colors.White);
            _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref captionColor, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmTextColor, ref textColor, sizeof(int));
            _ = SetWindowTheme(handle, "DarkMode_Explorer", null);
        }

        private IntPtr WindowHook(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message != WmEraseBackground || wParam == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var color = ResolveColor(_window);
            using var brush = new NativeBrush(color);
            if (GetClientRect(hwnd, out var rectangle) && FillRect(wParam, ref rectangle, brush.Handle) != 0)
            {
                handled = true;
                return new IntPtr(1);
            }

            return IntPtr.Zero;
        }
    }

    private sealed class NativeBrush : IDisposable
    {
        public NativeBrush(Color color)
        {
            Handle = CreateSolidBrush((uint)ToColorRef(color));
        }

        public IntPtr Handle { get; }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                _ = DeleteObject(Handle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, [In] ref NativeRect rectangle, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint colorRef);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);
}
