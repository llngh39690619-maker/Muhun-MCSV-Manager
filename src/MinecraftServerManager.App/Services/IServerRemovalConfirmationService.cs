namespace MinecraftServerManager.App.Services;

internal interface IServerRemovalConfirmationService
{
    bool ConfirmRemoval(string serverName, string directoryPath);
}
