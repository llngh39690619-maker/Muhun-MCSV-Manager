using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MinecraftServerManager.App.Controls;

/// <summary>
/// A password input whose eye toggles an editable plain-text preview. Losing focus, unloading,
/// or an owning dialog safety event always returns it to the masked input.
/// </summary>
public partial class RevealPasswordBox : UserControl
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password),
        typeof(string),
        typeof(RevealPasswordBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnPasswordPropertyChanged));

    public static readonly DependencyProperty MaxLengthProperty = DependencyProperty.Register(
        nameof(MaxLength),
        typeof(int),
        typeof(RevealPasswordBox),
        new PropertyMetadata(12));

    private bool _isSynchronizing;

    public RevealPasswordBox()
    {
        InitializeComponent();
    }

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value ?? string.Empty);
    }

    public int MaxLength
    {
        get => (int)GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    public bool IsPasswordRevealed
        => RevealedInput.Visibility == Visibility.Visible;

    private static void OnPasswordPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (RevealPasswordBox)sender;
        var next = e.NewValue as string ?? string.Empty;
        if (control._isSynchronizing) return;

        control._isSynchronizing = true;
        try
        {
            if (!string.Equals(control.MaskedInput.Password, next, StringComparison.Ordinal))
            {
                control.MaskedInput.Password = next;
            }

            // A collapsed TextBox remains discoverable through automation/raw visual trees.
            // Never place a PIN there until the user explicitly asks to reveal it.
            if (control.IsPasswordRevealed)
            {
                control.RevealedInput.Text = next;
            }
            else
            {
                control.RevealedInput.Clear();
            }
        }
        finally
        {
            control._isSynchronizing = false;
        }
    }

    private void OnMaskedPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing) return;
        _isSynchronizing = true;
        try
        {
            var next = MaskedInput.Password;
            if (IsPasswordRevealed)
            {
                RevealedInput.Text = next;
            }
            else
            {
                RevealedInput.Clear();
            }
            SetCurrentValue(PasswordProperty, next);
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void OnRevealedTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSynchronizing || !IsPasswordRevealed) return;
        _isSynchronizing = true;
        try
        {
            var next = RevealedInput.Text;
            MaskedInput.Password = next;
            SetCurrentValue(PasswordProperty, next);
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void OnRevealClicked(object sender, RoutedEventArgs e)
    {
        if (IsPasswordRevealed)
        {
            HidePassword(focusMaskedInput: true);
            return;
        }

        _isSynchronizing = true;
        try
        {
            RevealedInput.Text = MaskedInput.Password;
        }
        finally
        {
            _isSynchronizing = false;
        }

        MaskedInput.Visibility = Visibility.Collapsed;
        RevealedInput.Visibility = Visibility.Visible;
        ApplyRevealButtonLocalization("password.hide");
        RevealedInput.Focus();
        RevealedInput.CaretIndex = RevealedInput.Text.Length;
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && IsAncestorOf(next))
        {
            return;
        }

        HidePassword();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => HidePassword();

    internal void HidePassword(bool focusMaskedInput = false)
    {
        RevealedInput.Visibility = Visibility.Collapsed;
        MaskedInput.Visibility = Visibility.Visible;
        ApplyRevealButtonLocalization("password.reveal");
        _isSynchronizing = true;
        try
        {
            RevealedInput.Clear();
        }
        finally
        {
            _isSynchronizing = false;
        }

        if (focusMaskedInput && IsLoaded)
        {
            MaskedInput.Focus();
        }
    }

    private void ApplyRevealButtonLocalization(string key)
    {
        var resourceKey = $"L10n.{key}";
        RevealButton.SetResourceReference(FrameworkElement.ToolTipProperty, resourceKey);
        RevealButton.SetResourceReference(AutomationProperties.NameProperty, resourceKey);
    }
}
