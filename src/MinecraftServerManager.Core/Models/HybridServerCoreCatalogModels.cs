namespace MinecraftServerManager.Core.Models;

/// <summary>Where the expected SHA-256 for an official hybrid-server artifact originates.</summary>
public enum HybridArtifactVerification
{
    /// <summary>The first-party metadata response publishes the SHA-256.</summary>
    UpstreamSha256,

    /// <summary>
    /// The application pins the SHA-256 of an immutable, reviewed first-party release asset and
    /// cross-checks its repository, release ID, tag, asset ID, name, and size before exposing it.
    /// </summary>
    PinnedCatalogSha256
}

/// <summary>A hybrid Minecraft server family backed only by a first-party source.</summary>
public sealed record HybridServerCoreProductInfo(
    CoreType CoreType,
    string DisplayName,
    string Description);

/// <summary>A Minecraft/product version that has at least one verified downloadable build.</summary>
public sealed record HybridServerCoreVersionInfo(
    CoreType CoreType,
    string DisplayName,
    string MinecraftVersion,
    string ProductVersion,
    int JavaMajorVersion,
    bool IsLegacy,
    IReadOnlyList<string> Loaders);

/// <summary>A concrete, verified direct-server-JAR selection.</summary>
public sealed record HybridServerCoreBuildInfo(
    CoreType CoreType,
    string DisplayName,
    string MinecraftVersion,
    string ProductVersion,
    string? Loader,
    string? LoaderVersion,
    string BuildVersion,
    int JavaMajorVersion,
    bool IsStable,
    bool IsLegacy,
    Uri DownloadUri,
    string FileName,
    long Size,
    string Sha256,
    HybridArtifactVerification Verification,
    Uri MetadataUri,
    string SourceReleaseTag,
    long SourceReleaseId,
    long? SourceAssetId);

/// <summary>The result of atomically downloading and verifying a hybrid server core.</summary>
public sealed record HybridServerCoreDownloadResult(
    HybridServerCoreBuildInfo Build,
    string FilePath);

/// <summary>The single reviewed BuildTools artifact embedded in this application release.</summary>
public sealed record SpigotBuildToolsArtifactInfo(
    int BuildNumber,
    string SourceCommit,
    Uri MetadataUri,
    Uri DownloadUri,
    string FileName,
    long Size,
    string Sha256);

/// <summary>The supported verification recipe resolved from an exact Spigot version JSON.</summary>
public sealed record SpigotBuildToolsVersionInfo(
    string MinecraftVersion,
    int? JavaMajorVersion,
    bool IsSupported,
    string? UnsupportedReason,
    SpigotBuildOutputVerificationKind VerificationKind =
        SpigotBuildOutputVerificationKind.OfficialOutputSha256);

/// <summary>
/// The evidence used to accept a locally compiled BuildTools output. Newer definitions publish an
/// official output SHA-256. Historical definitions instead pin all four official source refs; those
/// builds require strict source/head and output-structure verification before promotion.
/// </summary>
public enum SpigotBuildOutputVerificationKind
{
    OfficialOutputSha256,
    OfficialSourceRefs
}

/// <summary>An exact, fail-closed local BuildTools invocation contract.</summary>
public sealed record SpigotBuildPlan(
    CoreType CoreType,
    string DisplayName,
    string MinecraftVersion,
    int JavaMajorVersion,
    string OutputFileName,
    string? ExpectedOutputSha256,
    int RequiredBuildToolsVersion,
    string VersionIdentity,
    IReadOnlyDictionary<string, string> SourceRefs,
    SpigotBuildToolsArtifactInfo BuildTools,
    SpigotBuildOutputVerificationKind OutputVerificationKind =
        SpigotBuildOutputVerificationKind.OfficialOutputSha256,
    string? BuildRevision = null);

/// <summary>A plan is either usable or contains a user-facing reason; never a partial plan.</summary>
public sealed record SpigotBuildPlanResolution(
    SpigotBuildPlan? Plan,
    string? UnsupportedReason)
{
    public bool IsSupported => Plan is not null && string.IsNullOrWhiteSpace(UnsupportedReason);
}

public sealed record SpigotBuildToolsBuildResult(
    SpigotBuildPlan Plan,
    string FilePath,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError,
    bool OutputWasTruncated,
    string ActualOutputSha256);

/// <summary>Bounded preflight for the platform/path assumptions documented by BuildTools.</summary>
public sealed record SpigotBuildToolsPreflightResult(
    bool CanRun,
    bool UsesBuildToolsManagedPortableGit,
    string? UnsupportedReason);
