namespace MinecraftServerManager.App.Services;

internal interface IServerDeletionConfirmationService
{
    bool ConfirmDeletion(string serverName, string directoryPath);
}
