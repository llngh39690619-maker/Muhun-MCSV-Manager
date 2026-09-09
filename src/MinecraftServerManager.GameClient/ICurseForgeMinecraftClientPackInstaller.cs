using System.Security;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public interface ICurseForgeMinecraftClientPackInstaller
{
    Task<CurseForgeClientPackInstallResult> InstallAsync(
        CurseForgeClientPackInstallRequest request,
        SecureString apiKey,
        string? javaExecutablePath,
        IProgress<CurseForgeClientPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
