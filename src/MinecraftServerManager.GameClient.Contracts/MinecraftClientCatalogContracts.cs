namespace MinecraftServerManager.GameClient.Contracts;

public sealed record MinecraftReleaseInfo(
    string Id,
    DateTimeOffset ReleasedAtUtc,
    Uri MetadataUri,
    string MetadataSha1,
    int ComplianceLevel);

public sealed record MinecraftReleaseCatalogSnapshot(
    string LatestReleaseId,
    DateTimeOffset LoadedAtUtc,
    IReadOnlyList<MinecraftReleaseInfo> Releases);

public sealed record MinecraftLoaderVersionInfo(
    MinecraftClientLoader Loader,
    string GameVersion,
    string Version,
    bool Stable,
    MinecraftClientLoaderInstallKind InstallKind,
    Uri? MetadataUri = null);

public sealed record MinecraftClientInstallRequest(
    Guid InstanceId,
    string Name,
    MinecraftClientEdition Edition,
    string GameVersion,
    MinecraftClientLoader Loader,
    string? LoaderVersion,
    MinecraftClientMemoryMode MemoryMode,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    int WindowWidth,
    int WindowHeight,
    bool FullScreen,
    bool EnableQuickLaunch = false,
    bool HideLauncherAfterGameStarts = true,
    bool ShowGameLog = false,
    bool EnableDedicatedGpu = true,
    bool EnableDiscordPresence = false,
    int? JavaMajorVersion = null);

public sealed record MinecraftClientInstallProgress(
    string Stage,
    string Message,
    double? Fraction = null);

public sealed record MinecraftClientInstallResult(
    MinecraftClientInstance Instance,
    string InstalledVersionId);

public enum MinecraftClientSkinVariant
{
    Classic = 0,
    Slim = 1,
}

public sealed record MinecraftClientSkinInfo(
    string Id,
    Uri TextureUri,
    MinecraftClientSkinVariant Variant,
    bool IsActive);

public sealed record MinecraftClientCapeInfo(
    string Id,
    string Alias,
    Uri? TextureUri,
    bool IsActive);

public sealed record MinecraftClientAccountInfo(
    string Id,
    string Username,
    string MinecraftUuid,
    DateTimeOffset LastAuthenticatedAtUtc,
    DateTimeOffset? AuthenticationExpiresAtUtc,
    MinecraftClientSkinInfo? ActiveSkin,
    IReadOnlyList<MinecraftClientCapeInfo> Capes);

/// <summary>
/// Public, short-lived information which the player may safely copy while completing
/// Microsoft's device authorization flow. The OAuth device credential is deliberately
/// kept inside the authentication library and is never exposed by this contract.
/// </summary>
public sealed record MinecraftDeviceCodePrompt(
    Uri VerificationUri,
    string UserCode,
    DateTimeOffset ExpiresAtUtc);
