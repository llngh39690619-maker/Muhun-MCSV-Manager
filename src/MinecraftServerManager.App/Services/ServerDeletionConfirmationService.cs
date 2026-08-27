using System.Windows;
using MinecraftServerManager.App.Dialogs;

namespace MinecraftServerManager.App.Services;

internal sealed class ServerDeletionConfirmationService : IServerDeletionConfirmationService
{
    public bool ConfirmDeletion(string serverName, string directoryPath)
    {
        var dialog = new DeleteServerConfirmationDialog(serverName, directoryPath);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }
}
