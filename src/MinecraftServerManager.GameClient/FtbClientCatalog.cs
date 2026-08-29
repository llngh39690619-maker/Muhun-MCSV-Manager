using System.Globalization;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// A bounded client-facing projection of the official public FTB catalogue.
/// </summary>
public sealed class FtbClientCatalog : IFtbClientPackCatalog
{
    private readonly FtbCatalogProvider _provider;

    public FtbClientCatalog(FtbCatalogProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<FtbClientCatalogPage> BrowseAsync(
        FtbClientCatalogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var hasLocalFilters = !string.IsNullOrWhiteSpace(request.GameVersion)
                              || request.Loader is not null;
        var fetchLimit = hasLocalFilters
            ? Math.Min(100, Math.Max(40, request.Limit * 2))
            : request.Limit;
        var result = string.IsNullOrWhiteSpace(request.Query)
            ? await _provider.GetFeaturedAsync(fetchLimit, cancellationToken).ConfigureAwait(false)
            : await _provider.SearchAsync(request.Query.Trim(), fetchLimit, cancellationToken)
                .ConfigureAwait(false);

        return MapAndFilter(result.Packs, request);
    }

    public Task<FtbPack> GetPackAsync(
        int packId,
        CancellationToken cancellationToken = default)
        => _provider.GetPackAsync(packId, cancellationToken);

    public Task<FtbPackVersionManifest> GetVersionManifestAsync(
        int packId,
        int versionId,
        CancellationToken cancellationToken = default)
        => _provider.GetVersionManifestAsync(packId, versionId, cancellationToken);

    internal static FtbClientCatalogPage MapAndFilter(
        IReadOnlyList<FtbPack> packs,
        FtbClientCatalogRequest request)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var projects = new List<FtbClientCatalogProject>(Math.Min(request.Limit, packs.Count));
        foreach (var pack in packs)
        {
            if (pack.IsPrivate)
            {
                continue;
            }

            var versions = pack.Versions
                .Where(static version => version.Type.Equals(
                    "release",
                    StringComparison.OrdinalIgnoreCase))
                .Where(static version => !version.IsPrivate)
                .Where(version => MatchesVersion(version, request))
                .OrderByDescending(static version =>
                    FtbTimestampNormalizer.NormalizeUtc(version.Updated))
                .ThenByDescending(static version => version.Id)
                .ToArray();
            if (versions.Length == 0)
            {
                continue;
            }

            projects.Add(new FtbClientCatalogProject(
                pack.Id,
                pack.Name,
                string.IsNullOrWhiteSpace(pack.Synopsis)
                    ? string.Empty
                    : pack.Synopsis.Trim(),
                pack.InstallCount ?? 0,
                FtbTimestampNormalizer.NormalizeUtc(versions[0].Updated) ?? DateTimeOffset.MinValue,
                pack.IconUri,
                pack.PreviewImageUri,
                versions.Select(version => new FtbClientCatalogVersion(
                        pack.Id,
                        version.Id,
                        version.Name,
                        version.MinecraftVersion?.Trim() ?? string.Empty,
                        version.ModLoaderName,
                        version.ModLoaderVersion,
                        FtbTimestampNormalizer.NormalizeUtc(version.Updated) ?? DateTimeOffset.MinValue,
                        version.JavaVersion))
                    .ToArray()));
            if (projects.Count == request.Limit)
            {
                break;
            }
        }

        return new FtbClientCatalogPage(projects, projects.Count);
    }

    private static bool MatchesVersion(
        FtbPackVersion version,
        FtbClientCatalogRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.GameVersion)
            && !string.Equals(
                version.MinecraftVersion,
                request.GameVersion.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return request.Loader is null
               || NormalizeLoader(version.ModLoaderName).Equals(
                   NormalizeLoader(request.Loader.Value.ToString()),
                   StringComparison.Ordinal);
    }

    private static string NormalizeLoader(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value
                .Where(static character => character != '-'
                                           && character != '_'
                                           && !char.IsWhiteSpace(character))
                .Select(char.ToLowerInvariant)
                .ToArray()) switch
            {
                "neoforged" => "neoforge",
                var normalized => normalized,
            };

}

public sealed record FtbClientCatalogRequest(
    string Query = "",
    string? GameVersion = null,
    MinecraftClientLoader? Loader = null,
    int Limit = 20)
{
    public void Validate()
    {
        if (Limit is < 1 or > 100)
        {
            throw FtbClientValidation.Mark(
                new ArgumentOutOfRangeException(
                    nameof(Limit),
                    FtbClientValidation.GetCode(FtbClientValidationFailure.ResultLimitOutOfRange)),
                FtbClientValidationFailure.ResultLimitOutOfRange);
        }

        if (Query.Length > 200)
        {
            throw FtbClientValidation.Mark(
                new ArgumentException(
                    FtbClientValidation.GetCode(FtbClientValidationFailure.QueryTooLong),
                    nameof(Query)),
                FtbClientValidationFailure.QueryTooLong);
        }

        if (GameVersion?.Length > 64)
        {
            throw FtbClientValidation.Mark(
                new ArgumentException(
                    FtbClientValidation.GetCode(FtbClientValidationFailure.GameVersionTooLong),
                    nameof(GameVersion)),
                FtbClientValidationFailure.GameVersionTooLong);
        }
    }
}

public sealed record FtbClientCatalogProject(
    int PackId,
    string Title,
    string Description,
    long Installs,
    DateTimeOffset UpdatedAt,
    Uri? IconUri,
    Uri? PreviewImageUri,
    IReadOnlyList<FtbClientCatalogVersion> StableVersions)
{
    public string ProjectId => PackId.ToString(CultureInfo.InvariantCulture);
}

public sealed record FtbClientCatalogVersion(
    int PackId,
    int VersionId,
    string Name,
    string GameVersion,
    string? LoaderName,
    string? LoaderVersion,
    DateTimeOffset UpdatedAt,
    string? JavaVersion = null);

public sealed record FtbClientCatalogPage(
    IReadOnlyList<FtbClientCatalogProject> Projects,
    int TotalHits);

public enum FtbClientValidationFailure
{
    ResultLimitOutOfRange,
    QueryTooLong,
    GameVersionTooLong,
    InvalidPackId,
}

/// <summary>
/// Attaches a stable, culture-neutral failure identity to the standard argument exception types.
/// The application boundary is responsible for turning that identity into user-facing text.
/// </summary>
public static class FtbClientValidation
{
    private const string FailureDataKey = "MinecraftServerManager.GameClient.FtbValidationFailure";

    public static bool TryGetFailure(
        Exception error,
        out FtbClientValidationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Data[FailureDataKey] is FtbClientValidationFailure taggedFailure)
        {
            failure = taggedFailure;
            return true;
        }

        failure = default;
        return false;
    }

    internal static string GetCode(FtbClientValidationFailure failure) => failure switch
    {
        FtbClientValidationFailure.ResultLimitOutOfRange => "ftb.result-limit-out-of-range",
        FtbClientValidationFailure.QueryTooLong => "ftb.query-too-long",
        FtbClientValidationFailure.GameVersionTooLong => "ftb.game-version-too-long",
        FtbClientValidationFailure.InvalidPackId => "ftb.invalid-pack-id",
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    internal static T Mark<T>(T error, FtbClientValidationFailure failure)
        where T : Exception
    {
        error.Data[FailureDataKey] = failure;
        return error;
    }
}

/// <summary>Builds only the exact protocol URI documented by the official FTB App.</summary>
public static class FtbAppProtocol
{
    public static readonly Uri OfficialDownloadPage = new("https://www.feed-the-beast.com/ftb-app");

    public static Uri CreateInstallUri(int packId)
    {
        if (packId <= 0)
        {
            throw FtbClientValidation.Mark(
                new ArgumentOutOfRangeException(
                    nameof(packId),
                    FtbClientValidation.GetCode(FtbClientValidationFailure.InvalidPackId)),
                FtbClientValidationFailure.InvalidPackId);
        }

        return new Uri(
            $"ftb://modpack/install?packId={packId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Absolute);
    }

    public static bool TryReadInstallPackId(Uri? uri, out int packId)
    {
        packId = 0;
        if (uri is null
            || !uri.IsAbsoluteUri
            || !uri.Scheme.Equals("ftb", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("modpack", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals("/install", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        const string prefix = "?packId=";
        return uri.Query.StartsWith(prefix, StringComparison.Ordinal)
               && !uri.Query.AsSpan(prefix.Length).Contains('&')
               && int.TryParse(
                   uri.Query.AsSpan(prefix.Length),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out packId)
               && packId > 0;
    }
}
