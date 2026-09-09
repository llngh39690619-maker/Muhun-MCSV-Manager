namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>
/// One exact CurseForge client-modpack installation. The API credential is deliberately not part
/// of this serializable request and must be supplied separately for the lifetime of the operation.
/// </summary>
public sealed record CurseForgeClientPackInstallRequest(
    Guid InstanceId,
    string Name,
    int ModId,
    int FileId,
    MinecraftClientMemoryMode MemoryMode,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    int WindowWidth,
    int WindowHeight,
    bool FullScreen,
    bool IncludeOptionalFiles = false,
    int MaximumConcurrentDownloads = 4,
    bool EnableQuickLaunch = false,
    bool HideLauncherAfterGameStarts = true,
    bool ShowGameLog = false,
    bool EnableDedicatedGpu = true,
    bool EnableDiscordPresence = false,
    int? JavaMajorVersion = null,
    string? CatalogIconImagePath = null,
    string? CatalogPreviewImagePath = null,
    string? ExpectedMinecraftVersion = null);

public sealed record CurseForgeClientPackInstallProgress(
    string Stage,
    string Message,
    int CompletedItems = 0,
    int TotalItems = 0,
    double? Fraction = null);

public sealed record CurseForgeClientPackInstallResult(
    MinecraftClientInstance Instance,
    int ModId,
    int FileId,
    string ProjectName,
    string PackName,
    string PackVersion,
    int InstalledContentFiles,
    int SkippedOptionalFiles,
    IReadOnlyList<string> InstalledPaths);
