using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

internal interface ICoreServerCreationDialogService
{
    ServerInstance? ShowCreateDialog(Window? owner);
}

internal sealed class CoreServerCreationDialogService(ICoreServerCreationWorkflow workflow)
    : ICoreServerCreationDialogService
{
    private readonly ICoreServerCreationWorkflow _workflow = workflow
        ?? throw new ArgumentNullException(nameof(workflow));

    public ServerInstance? ShowCreateDialog(Window? owner)
    {
        var dialog = new CoreServerCreationDialog(_workflow);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.CreatedServer : null;
    }
}
