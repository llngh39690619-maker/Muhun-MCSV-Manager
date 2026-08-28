using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public interface IMinecraftClientPayloadInstaller
{
    Task<string> InstallAsync(
        MinecraftClientInstallRequest request,
        string stagingDirectory,
        string? javaExecutablePath,
        IProgress<MinecraftClientInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
