using System.Security;
using System.Net;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

public enum OnlineModpackProvider
{
    Ftb,
    CurseForge,
    Modrinth
}

public sealed record OnlineModpackProviderChoice(
    OnlineModpackProvider Provider,
    string DisplayName,
    bool RequiresApiKey);

public enum OnlineModpackSort
{
    Relevance,
    Downloads,
    RecentlyUpdated,
    Newest
}

/// <summary>
/// A provider-neutral catalogue request. <see cref="SourceCategory"/> deliberately remains a
/// provider-owned stable identifier: Modrinth uses a category slug while CurseForge uses a
/// positive numeric category id. A UI must obtain those values from the selected source rather
/// than translating a display label back into an identifier.
/// </summary>
public sealed record OnlineModpackBrowseRequest(
    OnlineModpackProvider Provider,
    string Query = "",
    OnlineModpackSort Sort = OnlineModpackSort.Relevance,
    string? GameVersion = null,
    string? Loader = null,
    string? SourceCategory = null,
    int Offset = 0,
    int Limit = 20)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Provider))
        {
            throw new ArgumentOutOfRangeException(nameof(Provider));
        }

        if (!Enum.IsDefined(Sort))
        {
            throw new ArgumentOutOfRangeException(nameof(Sort));
        }

        if (Offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Offset),
                LocalizationService.Current.Get("online.validation.offsetNonNegative"));
        }

        if (Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Limit),
                LocalizationService.Current.Get("online.validation.limitRange"));
        }

        if (Offset > 10_000 - Limit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Offset),
                LocalizationService.Current.Get("online.validation.rangeMaximum"));
        }

        ValidateText(Query, nameof(Query), 200);
        ValidateText(GameVersion, nameof(GameVersion), 64);
        ValidateText(Loader, nameof(Loader), 64);
        ValidateText(SourceCategory, nameof(SourceCategory), 64);
    }

    private static void ValidateText(string? value, string name, int maximumLength)
    {
        if (value?.Trim().Length > maximumLength || value?.Any(char.IsControl) == true)
        {
            throw new ArgumentException(
                LocalizationService.Current.Get("online.validation.invalidText", name),
                name);
        }
    }
}

/// <summary>
/// A catalogue result whose media addresses have passed first-line metadata validation.
/// Non-HTTPS, literal-IP, localhost or credential-bearing addresses are reduced to null. The
/// bounded image downloader must still resolve and revalidate DNS before each connection.
/// </summary>
public sealed record OnlineModpackSearchResult
{
    public const int MaximumIconUriCandidates = 8;
    public const int MaximumPreviewImageUriCandidates = 8;
    private const int MaximumCandidateInputsPerRole = 64;

    public OnlineModpackSearchResult(
        OnlineModpackProvider provider,
        string projectId,
        string name,
        string summary,
        string authors,
        Uri? projectPageUri = null,
        Uri? iconUri = null,
        Uri? previewImageUri = null,
        long? downloadCount = null,
        DateTimeOffset? updatedAtUtc = null,
        IEnumerable<Uri>? iconUriCandidates = null,
        IEnumerable<Uri>? previewImageUriCandidates = null)
    {
        if (downloadCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadCount));
        }

        Provider = provider;
        ProjectId = projectId;
        Name = name;
        Summary = summary;
        Authors = authors;
        ProjectPageUri = projectPageUri;
        IconUriCandidates = BuildCandidates(
            iconUri,
            iconUriCandidates,
            MaximumIconUriCandidates);
        PreviewImageUriCandidates = BuildCandidates(
            previewImageUri,
            previewImageUriCandidates,
            MaximumPreviewImageUriCandidates);
        IconUri = IconUriCandidates.FirstOrDefault();
        PreviewImageUri = PreviewImageUriCandidates.FirstOrDefault();
        DownloadCount = downloadCount;
        UpdatedAtUtc = updatedAtUtc?.ToUniversalTime();
    }

    public OnlineModpackProvider Provider { get; init; }

    public string ProjectId { get; init; }

    public string Name { get; init; }

    public string Summary { get; init; }

    public string Authors { get; init; }

    public Uri? ProjectPageUri { get; init; }

    /// <summary>The first safe icon candidate, retained for existing callers.</summary>
    public Uri? IconUri { get; }

    /// <summary>The first safe preview candidate, retained for existing callers.</summary>
    public Uri? PreviewImageUri { get; }

    /// <summary>Ordered, de-duplicated and bounded icon candidates.</summary>
    public IReadOnlyList<Uri> IconUriCandidates { get; }

    /// <summary>Ordered, de-duplicated and bounded preview candidates.</summary>
    public IReadOnlyList<Uri> PreviewImageUriCandidates { get; }

    public long? DownloadCount { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public string SourceDisplay => Provider switch
    {
        OnlineModpackProvider.Ftb => "FTB",
        OnlineModpackProvider.CurseForge => "CurseForge",
        OnlineModpackProvider.Modrinth => "Modrinth",
        _ => Provider.ToString()
    };

    private static IReadOnlyList<Uri> BuildCandidates(
        Uri? primary,
        IEnumerable<Uri>? additional,
        int maximumResults)
    {
        var results = new List<Uri>(maximumResults);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inspected = 0;

        Add(primary);
        if (additional is not null)
        {
            foreach (var candidate in additional)
            {
                if (inspected >= MaximumCandidateInputsPerRole || results.Count >= maximumResults)
                {
                    break;
                }

                Add(candidate);
            }
        }

        return Array.AsReadOnly(results.ToArray());

        void Add(Uri? candidate)
        {
            inspected++;
            var accepted = OnlineModpackMediaUriPolicy.AcceptExternalHttps(candidate);
            if (accepted is null || results.Count >= maximumResults)
            {
                return;
            }

            var key = $"https://{accepted.IdnHost.TrimEnd('.').ToLowerInvariant()}"
                      + accepted.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
            if (seen.Add(key))
            {
                results.Add(accepted);
            }
        }
    }
}

public static class OnlineModpackMediaUriPolicy
{
    public static Uri? AcceptExternalHttps(Uri? candidate)
    {
        if (candidate is null
            || !candidate.IsAbsoluteUri
            || !candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !candidate.IsDefaultPort
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || string.IsNullOrWhiteSpace(candidate.IdnHost)
            || candidate.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || candidate.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(candidate.IdnHost, out _))
        {
            return null;
        }

        return candidate;
    }
}

public sealed record OnlineModpackVersion(
    OnlineModpackProvider Provider,
    string ProjectId,
    string VersionId,
    string VersionName,
    string MinecraftVersion,
    string Loader,
    string ReleaseChannel,
    DateTimeOffset ReleasedAtUtc,
    bool HasOfficialServerPack)
{
    public string ReleaseDateDisplay => ReleasedAtUtc == DateTimeOffset.MinValue
        ? LocalizationService.Current.Get("online.updatedDateUnavailable")
        : ReleasedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string ServerPackStatus => HasOfficialServerPack
        ? LocalizationService.Current.Get("online.version.serverPackAvailable")
        : LocalizationService.Current.Get("online.version.serverPackUnavailable");
}

public enum OnlineModpackInstallStage
{
    Preparing,
    ResolvingMetadata,
    Downloading,
    Verifying,
    Extracting,
    InstallingLoader,
    DetectingServer,
    Finalizing
}

/// <param name="Percentage">
/// A value from 0 through 100, or <see langword="null"/> when the current stage has no measurable
/// total.
/// </param>
public sealed record OnlineModpackInstallProgress(
    OnlineModpackInstallStage Stage,
    string Message,
    double? Percentage = null,
    string? Detail = null);

public sealed record OnlineModpackInstallRequest(
    OnlineModpackSearchResult Project,
    OnlineModpackVersion Version,
    string ServerName,
    bool MinecraftEulaAccepted = false);

/// <summary>
/// Provider-neutral application workflow used by <c>OnlineModpackDialog</c>. Implementations own
/// provider API calls, verified downloads, staging, extraction, loader installation and final
/// server detection. They must call executables directly with argument lists when required and
/// must never invoke a command shell.
/// </summary>
/// <remarks>
/// CurseForge credentials are operation-scoped <see cref="SecureString"/> values supplied by the
/// active UI only. Implementations must never retain, log, serialize, or place a supplied
/// credential in a URI or command-line argument. Providers that do not require a credential receive
/// <see langword="null"/>.
/// </remarks>
public interface IOnlineModpackWorkflow
{
    /// <summary>
    /// Optional bounded artwork cache used by presentation clients. Compatibility and test
    /// workflows may leave it null; catalog results remain fully usable without images.
    /// </summary>
    IOnlineModpackArtworkCache? ArtworkCache => null;

    async Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseAsync(
        OnlineModpackBrowseRequest request,
        SecureString? transientApiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Sort != OnlineModpackSort.Relevance
            || !string.IsNullOrWhiteSpace(request.GameVersion)
            || !string.IsNullOrWhiteSpace(request.Loader)
            || !string.IsNullOrWhiteSpace(request.SourceCategory))
        {
            throw new NotSupportedException(
                LocalizationService.Current.Get("online.validation.advancedFiltersUnsupported"));
        }

        var results = string.IsNullOrWhiteSpace(request.Query)
            ? await GetFeaturedAsync(request.Provider, transientApiKey, cancellationToken)
                .ConfigureAwait(false)
            : await SearchAsync(
                    request.Provider,
                    request.Query.Trim(),
                    transientApiKey,
                    cancellationToken)
                .ConfigureAwait(false);
        return results.Skip(request.Offset).Take(request.Limit).ToArray();
    }

    Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
        OnlineModpackProvider provider,
        SecureString? transientApiKey,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>([]);

    Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
        OnlineModpackProvider provider,
        string query,
        SecureString? transientApiKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
        OnlineModpackSearchResult project,
        SecureString? transientApiKey,
        CancellationToken cancellationToken);

    Task<ServerInstance> InstallAsync(
        OnlineModpackInstallRequest request,
        SecureString? transientApiKey,
        IProgress<OnlineModpackInstallProgress> progress,
        CancellationToken cancellationToken);
}
