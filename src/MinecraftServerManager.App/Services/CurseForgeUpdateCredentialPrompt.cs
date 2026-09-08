using System.Security;
using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Transfers ownership of a single-operation CurseForge credential to the caller. A configured
/// credential may be copied from the CurrentUser DPAPI store, but the returned copy must never be
/// logged, serialized, cached by the caller, or converted to a managed string outside the provider
/// request boundary.
/// </summary>
internal interface ICurseForgeUpdateCredentialPrompt
{
    SecureString? RequestCredential(Window? owner);
}

internal sealed class CurseForgeUpdateCredentialPrompt(
    ICurseForgeCredentialStore? credentialStore = null) : ICurseForgeUpdateCredentialPrompt
{
    private readonly ICurseForgeCredentialStore? _credentialStore = credentialStore;

    public SecureString? RequestCredential(Window? owner)
    {
        try
        {
            var stored = _credentialStore?.AcquireReadOnly();
            if (stored is not null)
            {
                if (!stored.IsReadOnly())
                {
                    stored.MakeReadOnly();
                }

                return stored;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DarkMessageBox.Show(
                owner,
                LocalizationService.Current.Get(
                    "modpackUpdate.curseForgeCredential.storeReadFailed"),
                LocalizationService.Current.Get("modpackUpdate.curseForgeCredential.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var dialog = new CurseForgeUpdateCredentialDialog();
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        try
        {
            SecureString? ownedCredential = null;
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            try
            {
                ownedCredential = dialog.TakeCredential();
                if (dialog.RememberCredential)
                {
                    try
                    {
                        if (_credentialStore is null)
                        {
                            throw new InvalidOperationException(
                                "The CurseForge credential store is unavailable.");
                        }

                        _credentialStore.Save(ownedCredential);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        DarkMessageBox.Show(
                            owner,
                            LocalizationService.Current.Get(
                                "modpackUpdate.curseForgeCredential.storeSaveFailed"),
                            LocalizationService.Current.Get("modpackUpdate.curseForgeCredential.title"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }

                var transferred = ownedCredential;
                ownedCredential = null;
                return transferred;
            }
            finally
            {
                ownedCredential?.Dispose();
            }
        }
        finally
        {
            dialog.DisposeUnclaimedCredential();
        }
    }
}

/// <summary>Test seam around the existing modal version picker.</summary>
internal sealed record ModpackUpdateSelection(
    OnlineModpackVersion Version,
    bool MinecraftEulaAccepted);

internal interface IModpackUpdateSelectionService
{
    ModpackUpdateSelection? SelectUpdate(
        ServerInstance instance,
        IReadOnlyList<OnlineModpackVersion> availableVersions,
        Window? owner);
}

internal sealed class ModpackUpdateSelectionService : IModpackUpdateSelectionService
{
    public ModpackUpdateSelection? SelectUpdate(
        ServerInstance instance,
        IReadOnlyList<OnlineModpackVersion> availableVersions,
        Window? owner)
    {
        var dialog = new ModpackUpdateSelectionDialog(instance, availableVersions);
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true && dialog.SelectedVersion is { } selectedVersion
            ? new ModpackUpdateSelection(selectedVersion, dialog.MinecraftEulaAccepted)
            : null;
    }
}
