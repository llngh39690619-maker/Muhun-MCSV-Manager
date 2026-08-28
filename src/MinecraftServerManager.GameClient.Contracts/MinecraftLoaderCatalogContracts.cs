namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>How an upstream project classifies a loader catalog entry.</summary>
public enum MinecraftLoaderReleaseChannel
{
    Stable = 0,
    Recommended,
    External,
}

/// <summary>
/// A loader version discovered from an official first-party catalog. Artifact hashes and size are
/// optional because some official catalogs publish those only beside the artifact, at install time.
/// </summary>
public sealed record MinecraftLoaderCatalogEntry(
    MinecraftClientLoader Loader,
    string GameVersion,
    string Version,
    MinecraftLoaderReleaseChannel ReleaseChannel,
    MinecraftClientLoaderInstallKind InstallKind,
    Uri OfficialSourceUri,
    Uri? InstallProfileOrArtifactUri,
    string Description,
    long? ArtifactSizeBytes = null,
    string? Sha1 = null,
    string? Sha256 = null,
    string? Sha512 = null);
