using System.Net;

namespace MinecraftServerManager.Core.Providers;

public enum CurseForgeModLoaderType
{
    Any = 0,
    Forge = 1,
    Cauldron = 2,
    LiteLoader = 3,
    Fabric = 4,
    Quilt = 5,
    NeoForge = 6
}

public enum CurseForgeModpackSortField
{
    Featured = 1,
    Popularity = 2,
    LastUpdated = 3,
    Name = 4,
    Author = 5,
    TotalDownloads = 6,
    ReleasedDate = 11,
    Rating = 12
}

public enum CurseForgeFileHashAlgorithm
{
    Sha1 = 1,
    Md5 = 2
}

/// <summary>
/// The expected role of a CurseForge project file. Callers must state the role explicitly so a
/// client export can never be substituted for an author-published server pack (or vice versa).
/// </summary>
public enum CurseForgeModpackFileRole
{
    ClientPack,
    ServerPack
}

public enum CurseForgeApiErrorCode
{
    InvalidApiKey,
    Forbidden,
    NotFound,
    RateLimited,
    ApiFailure
}

public enum CurseForgeServerPackResolutionStatus
{
    Available,
    NoOfficialServerPack,
    DistributionUnavailable,
    SelectedFileUnavailable,
    OfficialServerPackUnavailable
}

public sealed record CurseForgeModpackSearchRequest(
    string Query = "",
    string? GameVersion = null,
    CurseForgeModLoaderType ModLoader = CurseForgeModLoaderType.Any,
    int Index = 0,
    int PageSize = 20,
    CurseForgeModpackSortField SortField = CurseForgeModpackSortField.Popularity,
    bool SortDescending = true,
    int? CategoryId = null);

public sealed record CurseForgeCatalogIds(int MinecraftGameId, int ModpacksClassId);

public sealed record CurseForgePagination(
    int Index,
    int PageSize,
    int ResultCount,
    int TotalCount);

public sealed record CurseForgeModpackProject(
    int ModId,
    int GameId,
    int ClassId,
    string Slug,
    string Name,
    string Summary,
    string Author,
    Uri? WebsiteUri,
    Uri? IconUri,
    bool IsAvailable,
    bool AllowModDistribution,
    long DownloadCount,
    DateTimeOffset? DateModified,
    Uri? PreviewImageUri);

public sealed record CurseForgeModpackSearchPage(
    CurseForgeCatalogIds Catalog,
    IReadOnlyList<CurseForgeModpackProject> Projects,
    CurseForgePagination Pagination);

public sealed record CurseForgeFileHash(
    CurseForgeFileHashAlgorithm Algorithm,
    string Value);

public sealed record CurseForgeModpackFile(
    int FileId,
    int GameId,
    int ModId,
    string DisplayName,
    string FileName,
    bool IsAvailable,
    bool IsServerPack,
    int? ServerPackFileId,
    int ReleaseType,
    int FileStatus,
    long FileLength,
    DateTimeOffset? FileDate,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<CurseForgeFileHash> Hashes);

public sealed record CurseForgeModpackFilePage(
    IReadOnlyList<CurseForgeModpackFile> Files,
    CurseForgePagination Pagination);

public sealed record CurseForgeServerPackResolution(
    CurseForgeServerPackResolutionStatus Status,
    CurseForgeModpackProject Project,
    CurseForgeModpackFile? SelectedFile,
    CurseForgeModpackFile? ServerPackFile,
    string Message)
{
    public bool IsAvailable => Status == CurseForgeServerPackResolutionStatus.Available
                               && ServerPackFile is not null;
}

public sealed record CurseForgeModpackDownloadResult(
    int ModId,
    int FileId,
    string FileName,
    string DestinationPath,
    long Size,
    CurseForgeFileHashAlgorithm HashAlgorithm,
    string Hash);

public sealed class CurseForgeApiException : HttpRequestException
{
    public CurseForgeApiException(
        CurseForgeApiErrorCode errorCode,
        HttpStatusCode statusCode,
        TimeSpan? retryAfter = null)
        : base(BuildMessage(errorCode, statusCode, retryAfter), null, statusCode)
    {
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }

    public CurseForgeApiErrorCode ErrorCode { get; }

    public TimeSpan? RetryAfter { get; }

    private static string BuildMessage(
        CurseForgeApiErrorCode errorCode,
        HttpStatusCode statusCode,
        TimeSpan? retryAfter)
    {
        var detail = errorCode switch
        {
            CurseForgeApiErrorCode.InvalidApiKey => "API Key 無效或已失效。",
            CurseForgeApiErrorCode.Forbidden => "API Key 沒有權限存取此 CurseForge 資源。",
            CurseForgeApiErrorCode.NotFound => "CurseForge 找不到指定的專案或檔案。",
            CurseForgeApiErrorCode.RateLimited => retryAfter is { } delay
                ? $"CurseForge API 已限制請求，請在約 {Math.Max(1, Math.Ceiling(delay.TotalSeconds)):0} 秒後重試。"
                : "CurseForge API 已限制請求，請稍後重試。",
            _ => "CurseForge API 回應失敗。"
        };
        return $"{detail} HTTP {(int)statusCode}.";
    }
}

public sealed class CurseForgeServerPackException : InvalidOperationException
{
    public CurseForgeServerPackException(
        CurseForgeServerPackResolutionStatus status,
        string message)
        : base(message)
    {
        Status = status;
    }

    public CurseForgeServerPackResolutionStatus Status { get; }
}
