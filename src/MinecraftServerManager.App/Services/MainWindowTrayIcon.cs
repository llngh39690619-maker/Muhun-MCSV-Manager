using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using System.Windows.Threading;

namespace MinecraftServerManager.App.Services;

internal interface IMainWindowTrayIcon : IDisposable
{
    event EventHandler? OpenRequested;

    event EventHandler? ExitRequested;

    bool TryShow();

    void Hide();
}

internal sealed class DisabledMainWindowTrayIcon : IMainWindowTrayIcon
{
    public static DisabledMainWindowTrayIcon Instance { get; } = new();

    private DisabledMainWindowTrayIcon()
    {
    }

    public event EventHandler? OpenRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? ExitRequested
    {
        add { }
        remove { }
    }

    public bool TryShow() => false;

    public void Hide()
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class MainWindowTrayIcon : IMainWindowTrayIcon
{
    internal const string ToolTipText = "X MCSV";

    private readonly LocalizationService _localization;
    private readonly Dispatcher _dispatcher;
    private readonly Drawing.Icon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Forms.ToolStripMenuItem _openMenuItem;
    private readonly Forms.ToolStripMenuItem _exitMenuItem;
    private bool _disposed;

    public MainWindowTrayIcon(LocalizationService? localization = null)
    {
        _localization = localization ?? LocalizationService.Current;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _icon = LoadApplicationIcon();
        _openMenuItem = new Forms.ToolStripMenuItem();
        _exitMenuItem = new Forms.ToolStripMenuItem();
        _contextMenu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false
        };
        _contextMenu.Items.Add(_openMenuItem);
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add(_exitMenuItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = _icon,
            Text = ToolTipText,
            Visible = false
        };

        _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
        _openMenuItem.Click += OnOpenMenuItemClick;
        _exitMenuItem.Click += OnExitMenuItemClick;
        ApplyLocalizedText();
        _localization.CultureChanged += OnCultureChanged;
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    internal bool IsVisibleForTesting => !_disposed && _notifyIcon.Visible;

    internal bool IsDisposedForTesting => _disposed;

    internal int DisposeExecutionCountForTesting { get; private set; }

    internal string OpenMenuTextForTesting => _openMenuItem.Text ?? string.Empty;

    internal string ExitMenuTextForTesting => _exitMenuItem.Text ?? string.Empty;

    public bool TryShow()
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            _notifyIcon.Visible = true;
            return true;
        }
        catch (Exception)
        {
            Hide();
            return false;
        }
    }

    public void Hide()
    {
        try
        {
            _notifyIcon.Visible = false;
        }
        catch (Exception)
        {
            // The tray icon is optional. Explorer/native teardown must never block app shutdown.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeExecutionCountForTesting++;
        _localization.CultureChanged -= OnCultureChanged;
        OpenRequested = null;
        ExitRequested = null;
        try
        {
            DetachCallbacksNoThrow();
            Hide();
            try
            {
                _notifyIcon.ContextMenuStrip = null;
            }
            catch (Exception)
            {
                // Continue through every owned native resource below.
            }
        }
        finally
        {
            try
            {
                _notifyIcon.Dispose();
            }
            finally
            {
                try
                {
                    _contextMenu.Dispose();
                }
                finally
                {
                    _icon.Dispose();
                }
            }
        }
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
        => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenMenuItemClick(object? sender, EventArgs e)
        => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitMenuItemClick(object? sender, EventArgs e)
        => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            ApplyLocalizedText();
        }
        else
        {
            _dispatcher.BeginInvoke(ApplyLocalizedText, DispatcherPriority.Normal);
        }
    }

    private void ApplyLocalizedText()
    {
        if (_disposed)
        {
            return;
        }

        _openMenuItem.Text = _localization.Get("tray.open");
        _exitMenuItem.Text = _localization.Get("tray.exit");
    }

    internal void PerformOpenMenuClickForTesting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _openMenuItem.PerformClick();
    }

    internal void PerformExitMenuClickForTesting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _exitMenuItem.PerformClick();
    }

    private void DetachCallbacksNoThrow()
    {
        try
        {
            _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
        }
        catch (Exception)
        {
        }

        try
        {
            _openMenuItem.Click -= OnOpenMenuItemClick;
        }
        catch (Exception)
        {
        }

        try
        {
            _exitMenuItem.Click -= OnExitMenuItemClick;
        }
        catch (Exception)
        {
        }
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            using var executableIcon = Drawing.Icon.ExtractAssociatedIcon(executablePath);
            if (executableIcon is not null)
            {
                return (Drawing.Icon)executableIcon.Clone();
            }
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }
}
