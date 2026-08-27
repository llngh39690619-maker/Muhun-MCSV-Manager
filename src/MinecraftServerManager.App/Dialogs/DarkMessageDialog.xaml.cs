using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Dialogs;

internal readonly record struct DarkMessageButtonLayout(
    string PrimaryLabel,
    MessageBoxResult PrimaryResult,
    string? SecondaryLabel,
    MessageBoxResult SecondaryResult,
    MessageBoxResult DefaultResult,
    MessageBoxResult CloseResult);

/// <summary>
/// Application-owned replacement for the native message box. It keeps prompts on the same WPF
/// resource and native dark-surface path as every other application window.
/// </summary>
internal static class DarkMessageBox
{
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
        => Show(null, messageBoxText, caption, button, icon, defaultResult);

    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        ArgumentNullException.ThrowIfNull(messageBoxText);
        ArgumentNullException.ThrowIfNull(caption);
        var layout = CreateButtonLayout(button, defaultResult);
        ValidateIcon(icon);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null
            && !dispatcher.CheckAccess()
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished)
        {
            return dispatcher.Invoke(
                () => ShowOnUiThread(owner, messageBoxText, caption, button, icon, layout),
                DispatcherPriority.Send);
        }

        return ShowOnUiThread(owner, messageBoxText, caption, button, icon, layout);
    }

    /// <summary>
    /// Startup-only prompt path. The native fallback is intentionally unavailable to normal
    /// application flows and exists solely for failures that prevent WPF from constructing its
    /// own themed HWND.
    /// </summary>
    internal static MessageBoxResult ShowStartupPrompt(
        string messageBoxText,
        string caption,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        try
        {
            return Show(messageBoxText, caption, button, icon, defaultResult);
        }
        catch (Exception exception) when (IsPresentationBootstrapFailure(exception))
        {
            var layout = CreateButtonLayout(button, defaultResult);
            // Emergency startup fallback only: if WPF cannot construct a themed Window or native
            // HWND at all, preserving a visible fatal prompt is safer than terminating silently.
            // Normal application code must never call the native API directly.
            return MessageBox.Show(messageBoxText, caption, button, icon, layout.DefaultResult);
        }
    }

    internal static DarkMessageButtonLayout CreateButtonLayout(
        MessageBoxButton button,
        MessageBoxResult requestedDefault)
    {
        var conventionalDefault = button switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.Yes,
            _ => throw new InvalidEnumArgumentException(
                nameof(button),
                (int)button,
                typeof(MessageBoxButton))
        };
        var defaultResult = requestedDefault == MessageBoxResult.None
            ? conventionalDefault
            : requestedDefault;

        var layout = button switch
        {
            MessageBoxButton.OK => new DarkMessageButtonLayout(
                LocalizationService.Current.Get("common.ok"),
                MessageBoxResult.OK,
                null,
                MessageBoxResult.None,
                defaultResult,
                MessageBoxResult.OK),
            MessageBoxButton.OKCancel => new DarkMessageButtonLayout(
                LocalizationService.Current.Get("common.ok"),
                MessageBoxResult.OK,
                LocalizationService.Current.Get("common.cancel"),
                MessageBoxResult.Cancel,
                defaultResult,
                MessageBoxResult.Cancel),
            MessageBoxButton.YesNo => new DarkMessageButtonLayout(
                LocalizationService.Current.Get("common.yes"),
                MessageBoxResult.Yes,
                LocalizationService.Current.Get("common.no"),
                MessageBoxResult.No,
                defaultResult,
                MessageBoxResult.No),
            _ => throw new InvalidEnumArgumentException(
                nameof(button),
                (int)button,
                typeof(MessageBoxButton))
        };

        if (layout.DefaultResult != layout.PrimaryResult
            && layout.DefaultResult != layout.SecondaryResult)
        {
            throw new ArgumentException(
                LocalizationService.Current.Get("common.invalidDefaultResult"),
                nameof(requestedDefault));
        }

        return layout;
    }

    private static MessageBoxResult ShowOnUiThread(
        Window? requestedOwner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        DarkMessageButtonLayout layout)
    {
        var dialog = new DarkMessageDialog(messageBoxText, caption, icon, layout);
        var owner = ResolveOwner(requestedOwner);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _ = dialog.ShowDialog();
        return dialog.Result;
    }

    private static Window? ResolveOwner(Window? requestedOwner)
    {
        if (CanOwnDialog(requestedOwner))
        {
            return requestedOwner;
        }

        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Windows
                   .OfType<Window>()
                   .FirstOrDefault(window => window.IsActive && CanOwnDialog(window))
               ?? (CanOwnDialog(application.MainWindow) ? application.MainWindow : null);
    }

    private static bool CanOwnDialog(Window? window)
        => window is { IsLoaded: true, IsVisible: true }
           && PresentationSource.FromVisual(window) is not null
           && !window.Dispatcher.HasShutdownStarted
           && !window.Dispatcher.HasShutdownFinished;

    private static void ValidateIcon(MessageBoxImage icon)
    {
        if ((int)icon is not (0 or 16 or 32 or 48 or 64))
        {
            throw new InvalidEnumArgumentException(nameof(icon), (int)icon, typeof(MessageBoxImage));
        }
    }

    private static bool IsPresentationBootstrapFailure(Exception exception)
        => exception is XamlParseException
            or InvalidOperationException
            or Win32Exception
            or COMException
            or TypeInitializationException;
}

internal partial class DarkMessageDialog : Window
{
    private readonly DarkMessageButtonLayout _layout;
    private bool _completed;

    internal DarkMessageDialog(
        string message,
        string caption,
        MessageBoxImage icon,
        DarkMessageButtonLayout layout)
    {
        InitializeComponent();
        _layout = layout;
        Title = caption;
        CaptionText.Text = caption;
        MessageText.Text = message;
        ConfigureIcon(icon);
        ConfigureButtons();
        Result = layout.CloseResult;
    }

    internal MessageBoxResult Result { get; private set; }

    private void ConfigureButtons()
    {
        PrimaryButton.Content = _layout.PrimaryLabel;
        PrimaryButton.IsDefault = _layout.DefaultResult == _layout.PrimaryResult;

        if (_layout.SecondaryLabel is null)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            SecondaryButton.IsTabStop = false;
            return;
        }

        SecondaryButton.Content = _layout.SecondaryLabel;
        SecondaryButton.IsDefault = _layout.DefaultResult == _layout.SecondaryResult;
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        var resourceKey = "AccentBrush";
        IconGlyph.Text = (int)icon switch
        {
            0 => string.Empty,
            16 => "×",
            32 => "?",
            48 => "!",
            64 => "i",
            _ => string.Empty
        };
        resourceKey = (int)icon switch
        {
            16 => "DangerBrush",
            48 => "WarningBrush",
            _ => resourceKey
        };

        if ((int)icon == 0)
        {
            IconSurface.Visibility = Visibility.Collapsed;
            IconSurface.Margin = new Thickness(0);
            return;
        }

        if (TryFindResource(resourceKey) is Brush brush)
        {
            IconSurface.BorderBrush = brush;
            IconGlyph.Foreground = brush;
        }
    }

    private void OnPrimaryButtonClick(object sender, RoutedEventArgs e)
        => Complete(_layout.PrimaryResult);

    private void OnSecondaryButtonClick(object sender, RoutedEventArgs e)
        => Complete(_layout.SecondaryResult);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Complete(_layout.CloseResult);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_completed)
        {
            Result = _layout.CloseResult;
        }
    }

    private void Complete(MessageBoxResult result)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        Result = result;
        DialogResult = true;
    }
}
