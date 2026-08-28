using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public interface IMinecraftReleaseCatalog
{
    Task<MinecraftReleaseCatalogSnapshot> GetStableReleasesAsync(
        CancellationToken cancellationToken = default);
}
