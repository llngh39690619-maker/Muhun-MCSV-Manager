using System.Security;
using System.Windows;

namespace MinecraftServerManager.App.Dialogs;

internal partial class CurseForgeUpdateCredentialDialog : Window
{
    private SecureString? _credential;

    internal CurseForgeUpdateCredentialDialog()
    {
        InitializeComponent();
        Closed += (_, _) => ApiKeyInput.Clear();
    }

    internal SecureString TakeCredential()
    {
        var credential = _credential
            ?? throw new InvalidOperationException("The transient credential is unavailable.");
        _credential = null;
        return credential;
    }

    internal void DisposeUnclaimedCredential()
    {
        ApiKeyInput.Clear();
        _credential?.Dispose();
        _credential = null;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        using var source = ApiKeyInput.SecurePassword;
        if (source.Length == 0)
        {
            ValidationText.Visibility = Visibility.Visible;
            ApiKeyInput.Focus();
            return;
        }

        _credential = source.Copy();
        _credential.MakeReadOnly();
        ApiKeyInput.Clear();
        DialogResult = true;
    }
}
