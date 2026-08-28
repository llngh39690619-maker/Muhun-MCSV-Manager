using System.Diagnostics;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public interface IMinecraftClientProcessBuilder
{
    Task<Process> BuildAsync(
        MinecraftClientInstance instance,
        AuthenticatedMinecraftSession authenticatedSession,
        MinecraftClientMemoryResolution memory,
        CancellationToken cancellationToken = default);
}
