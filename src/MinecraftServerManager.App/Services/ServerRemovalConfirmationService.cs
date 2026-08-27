using System.Windows;
using MinecraftServerManager.App.Dialogs;

namespace MinecraftServerManager.App.Services;

internal sealed class ServerRemovalConfirmationService : IServerRemovalConfirmationService
{
    public bool ConfirmRemoval(string serverName, string directoryPath)
    {
        var dialog = new RemoveServerConfirmationDialog(serverName, directoryPath);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }
}
