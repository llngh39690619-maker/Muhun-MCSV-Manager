namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>Bounded request for installing one official public stable FTB client pack.</summary>
public sealed record FtbClientPackInstallRequest(
    Guid InstanceId,
    string Name,
    int PackId,
    int VersionId,
    MinecraftClientMemoryMode MemoryMode,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    int WindowWidth,
    int WindowHeight,
    bool FullScreen,
    bool IncludeOptionalFiles = false,
    int MaximumConcurrentDownloads = 8,
    bool EnableQuickLaunch = false,
    bool HideLauncherAfterGameStarts = true,
    bool ShowGameLog = false,
    bool EnableDedicatedGpu = true,
    bool EnableDiscordPresence = false,
    int? JavaMajorVersion = null,
    string? CatalogIconImagePath = null,
    string? CatalogPreviewImagePath = null);

public sealed record FtbClientPackInstallProgress(
    string Stage,
    string Message,
    int CompletedItems = 0,
    int TotalItems = 0,
    long CompletedBytes = 0,
    double? Fraction = null);

public sealed record FtbClientPackInstallResult(
    MinecraftClientInstance Instance,
    int PackId,
    int VersionId,
    string PackName,
    string PackVersionName,
    int InstalledContentFiles,
    int SkippedServerFiles,
    int SkippedOptionalFiles,
    IReadOnlyList<string> InstalledPaths);
