using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// OptiFine does not expose a supported machine-readable catalog or redistribution grant. This
/// provider deliberately describes the official manual flow and performs no network access.
/// </summary>
public sealed class OptiFineExternalInstallerCatalogProvider : IMinecraftLoaderCatalogProvider
{
    public static readonly Uri OfficialDownloadsUri = new("https://www.optifine.net/downloads");

    public MinecraftClientLoader Loader => MinecraftClientLoader.OptiFine;

    public Task<IReadOnlyList<MinecraftLoaderCatalogEntry>> GetVersionsAsync(
        MinecraftReleaseCatalogSnapshot stableMinecraftReleases,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MinecraftLoaderCatalogEntry> result =
            OfficialCatalogValidation.IsStableMinecraftRelease(stableMinecraftReleases, gameVersion)
                ?
                [
                    new MinecraftLoaderCatalogEntry(
                        Loader,
                        gameVersion,
                        "official-manual-download",
                        MinecraftLoaderReleaseChannel.External,
                        MinecraftClientLoaderInstallKind.ExternalInstallerRequired,
                        OfficialDownloadsUri,
                        null,
                        "OptiFine 無受支援的公開 catalog/checksum API，請從官方頁面下載後手動匯入；X MCSV 不鏡像或靜默下載。")
                ]
                : [];
        return Task.FromResult(result);
    }
}

/// <summary>
/// LabyMod is a separate proprietary client/launcher, not a generic mod loader. This provider only
/// links its official installer and performs no network access.
/// </summary>
public sealed class LabyModExternalInstallerCatalogProvider : IMinecraftLoaderCatalogProvider
{
    public static readonly Uri OfficialDownloadsUri = new("https://www.labymod.net/api/download");

    public MinecraftClientLoader Loader => MinecraftClientLoader.LabyMod;

    public Task<IReadOnlyList<MinecraftLoaderCatalogEntry>> GetVersionsAsync(
        MinecraftReleaseCatalogSnapshot stableMinecraftReleases,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MinecraftLoaderCatalogEntry> result =
            OfficialCatalogValidation.IsStableMinecraftRelease(stableMinecraftReleases, gameVersion)
                ?
                [
                    new MinecraftLoaderCatalogEntry(
                        Loader,
                        gameVersion,
                        "official-external-installer",
                        MinecraftLoaderReleaseChannel.External,
                        MinecraftClientLoaderInstallKind.ExternalInstallerRequired,
                        OfficialDownloadsUri,
                        null,
                        "LabyMod 是獨立專有客戶端；請使用官方安裝器，X MCSV 不使用未公開 API、不鏡像或靜默下載。")
                ]
                : [];
        return Task.FromResult(result);
    }
}
