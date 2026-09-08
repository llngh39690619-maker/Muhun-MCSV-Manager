using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

internal interface IOnlineModpackDialogService
{
    ServerInstance? ShowInstallDialog(Window? owner);
}

internal sealed class OnlineModpackDialogService(
    IOnlineModpackWorkflow workflow,
    ICurseForgeCredentialStore? curseForgeCredentialStore = null,
    ICurseForgeCredentialFileImportService? curseForgeCredentialFileImportService = null)
    : IOnlineModpackDialogService
{
    private readonly IOnlineModpackWorkflow _workflow = workflow
        ?? throw new ArgumentNullException(nameof(workflow));
    private readonly ICurseForgeCredentialStore? _curseForgeCredentialStore =
        curseForgeCredentialStore;
    private readonly ICurseForgeCredentialFileImportService? _curseForgeCredentialFileImportService =
        curseForgeCredentialFileImportService;

    public ServerInstance? ShowInstallDialog(Window? owner)
    {
        var dialog = new OnlineModpackDialog(
            new OnlineModpackViewModel(_workflow),
            loadFeaturedOnOpen: true,
            backgroundSubmitter: null,
            catalogRefreshDebounce: null,
            curseForgeCredentialStore: _curseForgeCredentialStore,
            curseForgeCredentialFileImportService: _curseForgeCredentialFileImportService);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.InstalledServer : null;
    }
}
