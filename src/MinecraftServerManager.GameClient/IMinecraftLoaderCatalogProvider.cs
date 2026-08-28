using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Discovers compatible loader releases from one official upstream catalog.</summary>
public interface IMinecraftLoaderCatalogProvider
{
    MinecraftClientLoader Loader { get; }

    Task<IReadOnlyList<MinecraftLoaderCatalogEntry>> GetVersionsAsync(
        MinecraftReleaseCatalogSnapshot stableMinecraftReleases,
        string gameVersion,
        CancellationToken cancellationToken = default);
}
