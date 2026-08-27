using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Downloads one catalog-selected hybrid server JAR to an arbitrary caller-selected name. The
/// source URI is reconstructed from immutable catalog identity before any network request, and
/// the file is committed only after exact length and SHA-256 verification.
/// </summary>
public sealed class HybridServerCoreDownloader
{
    private readonly HttpClient _githubArtifactClient;
    private readonly HttpClient _mohistArtifactClient;

    public HybridServerCoreDownloader(
        HttpClient githubArtifactClient,
        HttpClient mohistArtifactClient)
    {
        ArgumentNullException.ThrowIfNull(githubArtifactClient);
        ArgumentNullException.ThrowIfNull(mohistArtifactClient);
        _githubArtifactClient = githubArtifactClient;
        _mohistArtifactClient = mohistArtifactClient;
    }

    public async Task<HybridServerCoreDownloadResult> DownloadAsync(
        HybridServerCoreBuildInfo build,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateBuild(build);

        var client = build.CoreType == CoreType.Mohist
            ? _mohistArtifactClient
            : _githubArtifactClient;
        Func<Uri, bool> uriPolicy = build.CoreType == CoreType.Mohist
            ? HybridServerCoreCatalogProvider.IsMohistApiUri
            : HybridServerCoreCatalogProvider.IsGitHubArtifactUri;
        Func<Uri, Uri, bool> redirectPolicy = build.CoreType == CoreType.Mohist
            ? static (_, _) => false
            : HybridServerCoreCatalogProvider.IsAllowedGitHubArtifactRedirect;

        var path = await FirstPartyArtifactHttp.DownloadVerifiedSha256Async(
                client,
                build.DownloadUri,
                destinationPath,
                build.Sha256,
                build.Size,
                uriPolicy,
                redirectPolicy,
                build.FileName,
                $"{build.CoreType} server JAR",
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return new HybridServerCoreDownloadResult(build, path);
    }

    private static void ValidateBuild(HybridServerCoreBuildInfo build)
    {
        if (build.Size is < 1 or > FirstPartyArtifactHttp.MaximumArtifactBytes
            || build.Sha256.Length != 64
            || build.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Hybrid build 的 size/SHA-256 契約無效。");
        }

        Uri expected;
        if (build.CoreType == CoreType.Mohist)
        {
            if (build.SourceAssetId is not null
                || build.SourceReleaseId <= 0
                || !long.TryParse(
                    build.BuildVersion,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var buildId)
                || buildId != build.SourceReleaseId)
            {
                throw new InvalidDataException("Mohist build identity 無效。");
            }

            expected = new Uri(
                $"https://api.mohistmc.com/project/mohist/"
                + $"{Uri.EscapeDataString(build.MinecraftVersion)}/builds/{buildId}/download");
        }
        else if (build.CoreType is CoreType.Arclight or CoreType.CatServer or CoreType.Akarin)
        {
            if (build.SourceReleaseId <= 0 || build.SourceAssetId is null or <= 0)
            {
                throw new InvalidDataException("GitHub hybrid build identity 無效。");
            }

            expected = HybridServerCoreCatalogProvider.BuildExpectedGitHubDownloadUri(build);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(build), "不是可下載的 hybrid core build。");
        }

        if (!expected.AbsoluteUri.Equals(build.DownloadUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Hybrid build download URI 與 catalog identity 不符。");
        }
    }
}
