using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

internal interface IOnlineModpackDialogService
{
    ServerInstance? ShowInstallDialog(Window? owner);
}

internal sealed class OnlineModpackDialogService(IOnlineModpackWorkflow workflow)
    : IOnlineModpackDialogService
{
    private readonly IOnlineModpackWorkflow _workflow = workflow
        ?? throw new ArgumentNullException(nameof(workflow));

    public ServerInstance? ShowInstallDialog(Window? owner)
    {
        var dialog = new OnlineModpackDialog(_workflow);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.InstalledServer : null;
    }
}
