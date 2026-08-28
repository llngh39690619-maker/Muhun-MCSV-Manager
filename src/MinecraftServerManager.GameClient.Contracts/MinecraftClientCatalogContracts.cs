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

public sealed record MinecraftClientAccountInfo(
    string Id,
    string Username,
    string MinecraftUuid,
    DateTimeOffset LastAuthenticatedAtUtc);
