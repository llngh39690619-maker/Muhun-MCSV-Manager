using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MinecraftServerManager.App.Dialogs;

public partial class DeleteServerConfirmationDialog : Window
{
    private const int WmEraseBackground = 0x0014;
    private static readonly Color FallbackWindowColor = Color.FromRgb(0x10, 0x13, 0x18);
    private HwndSource? _windowSource;

    public DeleteServerConfirmationDialog(string serverName, string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        InitializeComponent();
        ServerName = serverName;
        DirectoryPath = directoryPath;
        DataContext = this;
        EnsureDarkSurface();
    }

    public string ServerName { get; }

    public string DirectoryPath { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureDarkSurface();

        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (_windowSource is null)
        {
            return;
        }

        _windowSource.CompositionTarget.BackgroundColor = ResolveWindowColor();
        _windowSource.AddHook(OnWindowMessage);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        EnsureDarkSurface();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnWindowMessage);
            _windowSource = null;
        }

        base.OnClosed(e);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        EnsureDarkSurface();
        DialogResult = true;
    }

    private nint OnWindowMessage(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmEraseBackground)
        {
            return 0;
        }

        handled = true;
        return 1;
    }

    private void EnsureDarkSurface()
    {
        var brush = TryFindResource("WindowBrush") as Brush
            ?? new SolidColorBrush(FallbackWindowColor);
        Background = brush;
        DialogRoot.Background = brush;
    }

    private Color ResolveWindowColor()
        => (TryFindResource("WindowBrush") as SolidColorBrush)?.Color
            ?? FallbackWindowColor;
}
