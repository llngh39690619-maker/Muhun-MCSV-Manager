namespace MinecraftServerManager.Core.Models;

/// <summary>
/// Normalizes the two timestamp units observed in FTB metadata. Values outside Minecraft's
/// plausible lifetime are rejected instead of being rendered as a misleading 1970 date.
/// Fixed bounds keep cached catalogue results deterministic and prevent clock skew from changing
/// an already persisted result.
/// </summary>
public static class FtbTimestampNormalizer
{
    private static readonly DateTimeOffset MinimumUtc =
        new(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MaximumUtc =
        new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly long MinimumSeconds = MinimumUtc.ToUnixTimeSeconds();
    private static readonly long MaximumSeconds = MaximumUtc.ToUnixTimeSeconds();
    private static readonly long MinimumMilliseconds = MinimumUtc.ToUnixTimeMilliseconds();
    private static readonly long MaximumMilliseconds = MaximumUtc.ToUnixTimeMilliseconds();

    public static DateTimeOffset? NormalizeUtc(long? value)
    {
        if (value is null or <= 0)
        {
            return null;
        }

        if (value.Value >= MinimumSeconds && value.Value <= MaximumSeconds)
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }

        if (value.Value >= MinimumMilliseconds && value.Value <= MaximumMilliseconds)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value.Value);
        }

        return null;
    }

    public static long? NormalizeToUnixTimeMilliseconds(long? value)
        => NormalizeUtc(value)?.ToUnixTimeMilliseconds();
}

public sealed record FtbTarget(string Type, string Name, string Version);

public sealed record FtbPackVersion(
    int Id,
    string Name,
    string Type,
    long? Updated,
    IReadOnlyList<FtbTarget> Targets,
    bool IsPrivate = false)
{
    public string? MinecraftVersion => FindTarget("game", "minecraft")?.Version;

    public string? ModLoaderName => Targets.FirstOrDefault(target =>
        target.Type.Equals("modloader", StringComparison.OrdinalIgnoreCase))?.Name;

    public string? ModLoaderVersion => Targets.FirstOrDefault(target =>
        target.Type.Equals("modloader", StringComparison.OrdinalIgnoreCase))?.Version;

    public string? JavaVersion => FindTarget("runtime", "java")?.Version;

    private FtbTarget? FindTarget(string type, string name) => Targets.FirstOrDefault(target =>
        target.Type.Equals(type, StringComparison.OrdinalIgnoreCase)
        && target.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed record FtbPackFileHashes(
    string Sha1,
    string Sha256,
    string Sha512);

/// <summary>One file declared by an official public FTB client-pack manifest.</summary>
public sealed record FtbPackFile(
    long Id,
    string Name,
    /// <summary>Normalized instance-relative destination, including the file name.</summary>
    string Path,
    Uri DownloadUri,
    IReadOnlyList<Uri> Mirrors,
    long Size,
    bool ClientOnly,
    bool ServerOnly,
    bool Optional,
    string Type,
    FtbPackFileHashes Hashes)
{
    /// <summary>Prefers FTB-owned blob mirrors before the authoritative external CDN URL.</summary>
    public IReadOnlyList<Uri> PreferredDownloadUris => Mirrors
        .Where(static uri => uri.IdnHost.Equals(
            "files.feed-the-beast.com",
            StringComparison.OrdinalIgnoreCase))
        .Concat([DownloadUri])
        .Concat(Mirrors)
        .DistinctBy(static uri => uri.AbsoluteUri, StringComparer.Ordinal)
        .ToArray();
}

public sealed record FtbPackMemorySpecs(
    int MinimumMb,
    int RecommendedMb);

/// <summary>
/// Bounded projection of <c>/v1/modpacks/public/modpack/{pack}/{version}</c>. Only public release
/// manifests are eligible for automatic client installation.
/// </summary>
public sealed record FtbPackVersionManifest(
    int PackId,
    int VersionId,
    string Name,
    string Type,
    bool IsPrivate,
    long? Updated,
    IReadOnlyList<FtbTarget> Targets,
    FtbPackMemorySpecs Memory,
    IReadOnlyList<FtbPackFile> Files)
{
    public string? MinecraftVersion => Targets.FirstOrDefault(target =>
        target.Type.Equals("game", StringComparison.OrdinalIgnoreCase) &&
        target.Name.Equals("minecraft", StringComparison.OrdinalIgnoreCase))?.Version;

    public string? ModLoaderName => Targets.FirstOrDefault(target =>
        target.Type.Equals("modloader", StringComparison.OrdinalIgnoreCase))?.Name;

    public string? ModLoaderVersion => Targets.FirstOrDefault(target =>
        target.Type.Equals("modloader", StringComparison.OrdinalIgnoreCase))?.Version;

    public string? JavaVersion => Targets.FirstOrDefault(target =>
        target.Type.Equals("runtime", StringComparison.OrdinalIgnoreCase) &&
        target.Name.Equals("java", StringComparison.OrdinalIgnoreCase))?.Version;
}

public sealed record FtbArtwork(
    Uri Uri,
    string Type,
    int Width,
    int Height,
    IReadOnlyList<Uri>? Mirrors = null)
{
    public IEnumerable<Uri> EnumerateUris()
    {
        yield return Uri;
        if (Mirrors is null) yield break;
        foreach (var mirror in Mirrors)
        {
            yield return mirror;
        }
    }
}

public sealed record FtbPack(
    int Id,
    string Name,
    string Slug,
    bool IsPrivate,
    IReadOnlyList<FtbPackVersion> Versions,
    string? Synopsis = null,
    long? InstallCount = null,
    IReadOnlyList<FtbArtwork>? Artwork = null)
{
    public const int MaximumArtworkCandidatesPerRole = 32;

    public FtbPackVersion? LatestRelease => Versions
        .Where(version => !version.IsPrivate &&
                          version.Type.Equals("release", StringComparison.OrdinalIgnoreCase))
        .MaxBy(version => version.Id);

    public IReadOnlyList<Uri> IconUriCandidates => BuildArtworkCandidates(
        "square",
        "splash",
        "screenshot");

    public IReadOnlyList<Uri> PreviewImageUriCandidates => BuildArtworkCandidates(
        "splash",
        "screenshot",
        "square");

    public Uri? IconUri => IconUriCandidates.FirstOrDefault();

    public Uri? PreviewImageUri => PreviewImageUriCandidates.FirstOrDefault();

    private IReadOnlyList<Uri> BuildArtworkCandidates(params string[] preferredTypes)
    {
        if (Artwork is null || Artwork.Count == 0)
        {
            return [];
        }

        var results = new List<Uri>(Math.Min(MaximumArtworkCandidatesPerRole, Artwork.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preferredType in preferredTypes)
        {
            foreach (var artwork in Artwork.Where(art =>
                         art.Type.Equals(preferredType, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var uri in artwork.EnumerateUris())
                {
                    if (results.Count >= MaximumArtworkCandidatesPerRole)
                    {
                        return results.ToArray();
                    }

                    if (seen.Add(uri.AbsoluteUri))
                    {
                        results.Add(uri);
                    }
                }
            }
        }

        return results.ToArray();
    }
}

public sealed record FtbSearchResult(IReadOnlyList<FtbPack> Packs);

public sealed record FtbInstallerArtifact(
    string ReleaseTag,
    string FilePath,
    long Size,
    string Sha256);

public sealed record FtbInstallRequest(
    int PackId,
    int VersionId,
    string InstallerPath,
    string InstallationDirectory,
    bool MinecraftEulaAccepted = false);

public sealed record FtbInstallerOutputLine(bool IsError, string Text);

public sealed record FtbInstallerProcessResult(
    int ExitCode,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError,
    bool OutputTruncated = false);

public sealed record FtbInstallResult(
    string InstallationDirectory,
    ServerPackDetectionResult Detection,
    FtbInstallerProcessResult ProcessResult);
