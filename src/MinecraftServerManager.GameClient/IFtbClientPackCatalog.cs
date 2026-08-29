using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.GameClient;

/// <summary>Provides only public FTB pack metadata and version manifests.</summary>
public interface IFtbClientPackCatalog
{
    Task<FtbPack> GetPackAsync(
        int packId,
        CancellationToken cancellationToken = default);

    Task<FtbPackVersionManifest> GetVersionManifestAsync(
        int packId,
        int versionId,
        CancellationToken cancellationToken = default);
}
