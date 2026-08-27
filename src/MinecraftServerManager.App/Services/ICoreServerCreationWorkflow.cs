using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Server software families understood by the core-server creation UI. This is a capability
/// vocabulary only; the dialog displays solely the products returned by
/// <see cref="ICoreServerCreationWorkflow.GetAvailableCoresAsync"/>.
/// </summary>
public enum CoreServerSoftware
{
    Paper,
    Spigot,
    CraftBukkit,
    Forge,
    NeoForge,
    Fabric,
    Mohist,
    Arclight,
    Velocity,
    Vanilla,
    CatServer,
    Akarin
}

/// <summary>A core product that is currently available from the production workflow.</summary>
public sealed record CoreServerProduct(
    CoreServerSoftware Software,
    string CoreId,
    string DisplayName,
    string Description);

/// <summary>An actual downloadable version returned for one core product.</summary>
public sealed record CoreServerVersion(
    string CoreId,
    string VersionId,
    string DisplayName,
    string MinecraftVersion,
    string Build,
    DateTimeOffset? ReleasedAtUtc = null,
    bool IsRecommended = false)
{
    public string BuildDisplay => string.IsNullOrWhiteSpace(Build)
        ? LocalizationService.Current.Get("core.build.unspecified")
        : Build;

    public string ReleaseDateDisplay => ReleasedAtUtc is { } releasedAt
        ? releasedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : LocalizationService.Current.Get("core.release.unavailable");
}

public enum CoreServerCreationStage
{
    Preparing,
    ResolvingVersion,
    PreparingDirectory,
    Downloading,
    Verifying,
    Installing,
    DetectingServer,
    Finalizing
}

/// <param name="Percentage">
/// A value from 0 through 100, or <see langword="null"/> when the current stage has no measurable
/// total.
/// </param>
/// <param name="Detail">
/// Optional live output for a nested operation. This does not replace <paramref name="Message"/>
/// or change the overall <paramref name="Percentage"/>.
/// </param>
/// <param name="IsDetailIndeterminate">
/// Whether the optional detail operation has an unknown total and should use an indeterminate
/// secondary progress indicator.
/// </param>
public sealed record CoreServerCreationProgress(
    CoreServerCreationStage Stage,
    string Message,
    double? Percentage = null,
    string? Detail = null,
    bool IsDetailIndeterminate = false);

public sealed record CoreServerCreationRequest(
    CoreServerProduct Core,
    CoreServerVersion Version,
    string ServerName);

public enum CoreServerCatalogBootstrapKind
{
    BuiltInBaseline,
    FreshCache,
    StaleCache
}

/// <summary>
/// Local-only first paint for the core dialog. Cached versions are discovery hints only: the
/// creation workflow still resolves the selected product/version against its live provider before
/// downloading or installing anything.
/// </summary>
public sealed record CoreServerCatalogBootstrap(
    IReadOnlyList<CoreServerProduct> Cores,
    IReadOnlyDictionary<string, IReadOnlyList<CoreServerVersion>> CachedVersions,
    CoreServerCatalogBootstrapKind Kind,
    DateTimeOffset? CachedAtUtc,
    string StatusText);

/// <summary>One bounded background catalog result, or the terminal refresh status.</summary>
public sealed record CoreServerCatalogUpdate(
    CoreServerProduct? Core,
    IReadOnlyList<CoreServerVersion> Versions,
    string SourceId,
    int CompletedCores,
    int TotalCores,
    bool Succeeded,
    bool IsFinal,
    string StatusText);

/// <summary>
/// Optional fast-catalog capability. Implementations must keep bootstrap local and bounded, then
/// stream real provider results without fabricating products or versions.
/// </summary>
public interface IIncrementalCoreServerCatalogWorkflow
{
    ValueTask<CoreServerCatalogBootstrap> GetCatalogBootstrapAsync(
        CancellationToken cancellationToken);

    IAsyncEnumerable<CoreServerCatalogUpdate> RefreshCatalogAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral application workflow used by <c>CoreServerCreationDialog</c>.
/// Implementations resolve live product/version catalogs and own verified download, installation,
/// server detection and final persistence. Every operation must honor cancellation.
/// </summary>
public interface ICoreServerCreationWorkflow
{
    /// <summary>
    /// Returns only core products that are currently supported and have a usable upstream source.
    /// The UI never supplements this result with a built-in product list.
    /// </summary>
    Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns actual upstream versions for <paramref name="core"/>. An empty result is valid and
    /// is presented as an explicit unavailable state rather than a fabricated fallback version.
    /// </summary>
    Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
        CoreServerProduct core,
        CancellationToken cancellationToken);

    Task<ServerInstance> CreateAsync(
        CoreServerCreationRequest request,
        IProgress<CoreServerCreationProgress> progress,
        CancellationToken cancellationToken);
}
