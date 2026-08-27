using System.Security;
using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Transfers ownership of a single-operation CurseForge credential to the caller. Implementations
/// must never persist, log, cache, or convert the credential to a managed string.
/// </summary>
internal interface ICurseForgeUpdateCredentialPrompt
{
    SecureString? RequestCredential(Window? owner);
}

internal sealed class CurseForgeUpdateCredentialPrompt : ICurseForgeUpdateCredentialPrompt
{
    public SecureString? RequestCredential(Window? owner)
    {
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
            return dialog.ShowDialog() == true
                ? dialog.TakeCredential()
                : null;
        }
        finally
        {
            dialog.DisposeUnclaimedCredential();
        }
    }
}

/// <summary>Test seam around the existing modal version picker.</summary>
internal interface IModpackUpdateSelectionService
{
    OnlineModpackVersion? SelectVersion(
        ServerInstance instance,
        IReadOnlyList<OnlineModpackVersion> availableVersions,
        Window? owner);
}

internal sealed class ModpackUpdateSelectionService : IModpackUpdateSelectionService
{
    public OnlineModpackVersion? SelectVersion(
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

        return dialog.ShowDialog() == true ? dialog.SelectedVersion : null;
    }
}
