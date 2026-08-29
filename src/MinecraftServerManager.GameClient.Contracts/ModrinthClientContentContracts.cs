namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>Sorting modes supported by the public Modrinth search API.</summary>
public enum ModrinthClientContentSort
{
    Relevance = 0,
    Downloads = 1,
    Follows = 2,
    Newest = 3,
    Updated = 4,
}

public enum ModrinthClientDependencyKind
{
    Required = 0,
    Optional = 1,
    Incompatible = 2,
    Embedded = 3,
}

public enum ModrinthClientContentFallbackReason
{
    MissingVerifiedFile = 0,
    UnsupportedFile = 1,
    UnresolvedDependency = 2,
    DependencyConflict = 3,
    FileNameConflict = 4,
    DownloadFailed = 5,
}

public sealed record ModrinthClientContentSearchRequest(
    MinecraftClientContentKind Kind,
    string Query = "",
    string? GameVersion = null,
    MinecraftClientLoader? Loader = null,
    ModrinthClientContentSort Sort = ModrinthClientContentSort.Relevance,
    int Offset = 0,
    int Limit = 20);

public sealed record ModrinthClientContentProject(
    string ProjectId,
    string Slug,
    MinecraftClientContentKind Kind,
    string Title,
    string Description,
    string Author,
    Uri? IconUri,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    long Downloads,
    DateTimeOffset DateModified,
    Uri ProjectPageUri);

public sealed record ModrinthClientContentSearchPage(
    IReadOnlyList<ModrinthClientContentProject> Projects,
    int Offset,
    int Limit,
    int TotalHits);

public sealed record ModrinthClientContentFile(
    string FileName,
    Uri DownloadUri,
    long Size,
    string? Sha512,
    string? Sha1,
    bool Primary);

public sealed record ModrinthClientContentDependency(
    string? ProjectId,
    string? VersionId,
    string? FileName,
    ModrinthClientDependencyKind Kind);

public sealed record ModrinthClientContentVersion(
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    DateTimeOffset DatePublished,
    IReadOnlyList<ModrinthClientContentFile> Files,
    IReadOnlyList<ModrinthClientContentDependency> Dependencies);

public sealed record ModrinthClientContentArtifact(
    string ProjectId,
    string ProjectSlug,
    string ProjectTitle,
    MinecraftClientContentKind Kind,
    string VersionId,
    string VersionNumber,
    ModrinthClientContentFile File,
    Uri VersionPageUri,
    bool IsDependency);

public sealed record ModrinthClientContentFallback(
    string ProjectId,
    string? VersionId,
    string DisplayName,
    ModrinthClientContentFallbackReason Reason,
    string Message,
    Uri VersionPageUri,
    Uri? DirectDownloadUri = null);

public sealed record ModrinthClientContentInstallPlan(
    ModrinthClientContentProject Project,
    ModrinthClientContentVersion Version,
    string GameVersion,
    MinecraftClientLoader? RequiredLoader,
    IReadOnlyList<MinecraftClientLoader> CompatibleLoaders,
    IReadOnlyList<ModrinthClientContentArtifact> Artifacts,
    IReadOnlyList<ModrinthClientContentFallback> Fallbacks)
{
    public bool CanInstallAutomatically => Artifacts.Count > 0 && Fallbacks.Count == 0;
}

public sealed record ModrinthClientContentInstallRequest(
    string InstanceDirectory,
    string ProjectId,
    MinecraftClientContentKind Kind,
    string GameVersion,
    MinecraftClientLoader? Loader = null);

public sealed record ModrinthClientContentInstallProgress(
    string Stage,
    string Message,
    int CompletedItems,
    int TotalItems,
    long CompletedBytes = 0);

public sealed record ModrinthClientContentInstallResult(
    ModrinthClientContentInstallPlan Plan,
    IReadOnlyList<MinecraftClientContentEntry> InstalledEntries,
    IReadOnlyList<ModrinthClientContentFallback> Fallbacks)
{
    public bool Installed => InstalledEntries.Count > 0 && Fallbacks.Count == 0;
}
