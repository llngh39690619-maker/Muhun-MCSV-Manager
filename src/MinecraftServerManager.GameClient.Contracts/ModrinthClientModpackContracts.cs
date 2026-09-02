namespace MinecraftServerManager.GameClient.Contracts;

public enum ModrinthClientModpackSort
{
    Relevance = 0,
    Downloads = 1,
    Follows = 2,
    Newest = 3,
    Updated = 4,
}

public sealed record ModrinthClientModpackSearchRequest(
    string Query = "",
    string? GameVersion = null,
    MinecraftClientLoader? Loader = null,
    string? Category = null,
    ModrinthClientModpackSort Sort = ModrinthClientModpackSort.Relevance,
    int Offset = 0,
    int Limit = 20);

public sealed record ModrinthClientModpackProject(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    Uri? IconUri,
    Uri? FeaturedImageUri,
    IReadOnlyList<Uri> GalleryImageUris,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Environments,
    long Downloads,
    long Followers,
    DateTimeOffset DateModified,
    string? FullDescription = null);

public sealed record ModrinthClientModpackSearchPage(
    IReadOnlyList<ModrinthClientModpackProject> Projects,
    int Offset,
    int Limit,
    int TotalHits);

public sealed record ModrinthClientMrpackFile(
    string FileName,
    Uri DownloadUri,
    long Size,
    string Sha512,
    string? Sha1,
    bool Primary);

public sealed record ModrinthClientModpackVersion(
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    string Environment,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    DateTimeOffset DatePublished,
    long Downloads,
    ModrinthClientMrpackFile MrpackFile);

public sealed record ModrinthClientPackInstallRequest(
    Guid InstanceId,
    string Name,
    string ProjectId,
    string VersionId,
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
    string? CatalogPreviewImagePath = null);

public sealed record ModrinthClientPackInstallProgress(
    string Stage,
    string Message,
    int CompletedItems = 0,
    int TotalItems = 0,
    long CompletedBytes = 0,
    double? Fraction = null);

public sealed record ModrinthClientPackInstallResult(
    MinecraftClientInstance Instance,
    ModrinthClientModpackProject Project,
    ModrinthClientModpackVersion Version,
    string PackName,
    string PackVersionId,
    int InstalledContentFiles,
    int SkippedUnsupportedFiles,
    int SkippedOptionalFiles,
    IReadOnlyList<string> InstalledPaths);
