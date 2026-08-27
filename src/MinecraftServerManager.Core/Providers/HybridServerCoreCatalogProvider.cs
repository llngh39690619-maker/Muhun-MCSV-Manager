using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// First-party-only catalog for mod/plugin hybrid server cores. GitHub entries are either covered
/// by GitHub's SHA-256 asset digest or by an application-owned immutable release allowlist.
/// Mohist entries are covered by the project's own SHA-256 API field.
/// </summary>
public sealed partial class HybridServerCoreCatalogProvider
{
    private static readonly Uri GitHubApiBase = new("https://api.github.com/");
    private static readonly Uri MohistApiBase = new("https://api.mohistmc.com/");
    private static readonly Uri ArclightReleasesUri = new(
        "https://api.github.com/repos/IzzelAliz/Arclight/releases?per_page=100&page=1");

    private static readonly HybridServerCoreProductInfo[] Products =
    [
        new(CoreType.Mohist, "Mohist", "Forge + Bukkit/Spigot hybrid server"),
        new(CoreType.Arclight, "Arclight", "Forge/NeoForge/Fabric + Bukkit hybrid server"),
        new(CoreType.CatServer, "CatServer", "Forge + Bukkit/Spigot hybrid server"),
        new(CoreType.Akarin, "Akarin", "Historical Paper-derived Bukkit server")
    ];

    // GitHub did not retroactively publish SHA-256 digests for these old assets. Each digest below
    // was independently calculated from the exact official release asset and is deliberately tied
    // to all immutable GitHub identities. A replaced/re-uploaded asset is therefore not exposed.
    private static readonly PinnedGitHubArtifact[] PinnedArtifacts =
    [
        new(
            CoreType.CatServer,
            "CatServer 1.12.2 (pinned official release)",
            "Luohuayu",
            "CatServer",
            "1.12.2",
            "25.02.04",
            65377291,
            225995935,
            "CatServer-4168d848-universal.jar",
            7_795_165,
            "eaf575310acbb48d535212cfb88d93de69f90f2a81879a26f88457713a25952e",
            8,
            "forge"),
        new(
            CoreType.CatServer,
            "CatServer 1.16.5 (pinned official release)",
            "Luohuayu",
            "CatServer",
            "1.16.5",
            "23.05.26-1",
            65783084,
            109893736,
            "CatServer-1.16.5-1d8d6313-server.jar",
            8_009_491,
            "8edea98c597e7af44a45ef9093678c38ca3749d55a71cdff10218c1c6b946fd0",
            8,
            "forge"),
        new(
            CoreType.CatServer,
            "CatServer 1.18.2 (pinned official release)",
            "Luohuayu",
            "CatServer",
            "1.18.2",
            "23.05.26",
            102527978,
            109865674,
            "CatServer-1.18.2-6c3f5965-server.jar",
            11_087_242,
            "b4d70e515e4b203d79f5da80e560cf62d3f7d3006be099fe0c21ce3111d96989",
            17,
            "forge"),
        new(
            CoreType.Akarin,
            "Akarin 1.12.2 R0.4.4 LTS (pinned official release)",
            "Akarin-project",
            "Akarin",
            "1.12.2",
            "1.12.2-R0.4.4",
            93122943,
            96490178,
            "akarin-1.12.2.jar",
            48_696_258,
            "b6eae9e1f9e831505939db26ac032dc866b9dbb6f2a21f5762e6f2a0f5099e68",
            8,
            null)
    ];

    private readonly HttpClient _githubApiClient;
    private readonly HttpClient _mohistApiClient;

    public HybridServerCoreCatalogProvider(
        HttpClient githubApiClient,
        HttpClient mohistApiClient,
        string userAgent)
    {
        ArgumentNullException.ThrowIfNull(githubApiClient);
        ArgumentNullException.ThrowIfNull(mohistApiClient);
        ValidateUserAgent(userAgent);
        _githubApiClient = githubApiClient;
        _mohistApiClient = mohistApiClient;
        ConfigureClient(_githubApiClient, userAgent);
        ConfigureClient(_mohistApiClient, userAgent);
    }

    public IReadOnlyList<HybridServerCoreProductInfo> GetProducts() => Products;

    public async Task<IReadOnlyList<HybridServerCoreVersionInfo>> GetVersionsAsync(
        CoreType coreType,
        CancellationToken cancellationToken = default)
    {
        var builds = coreType switch
        {
            CoreType.Mohist => await GetAllMohistLatestBuildsAsync(cancellationToken)
                .ConfigureAwait(false),
            CoreType.Arclight => await GetArclightBuildsAsync(cancellationToken)
                .ConfigureAwait(false),
            CoreType.CatServer or CoreType.Akarin =>
                await GetPinnedGitHubBuildsAsync(coreType, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(coreType),
                $"{coreType} 不是 hybrid catalog 支援的核心。")
        };

        return builds
            .GroupBy(build => new
            {
                build.CoreType,
                build.MinecraftVersion,
                build.ProductVersion,
                build.JavaMajorVersion,
                build.IsLegacy
            })
            .Select(group => new HybridServerCoreVersionInfo(
                group.Key.CoreType,
                group.First().DisplayName,
                group.Key.MinecraftVersion,
                group.Key.ProductVersion,
                group.Key.JavaMajorVersion,
                group.Key.IsLegacy,
                group.Select(build => build.Loader)
                    .Where(loader => !string.IsNullOrWhiteSpace(loader))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(loader => loader, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderByDescending(info => ParseMinecraftSortKey(info.MinecraftVersion))
            .ThenByDescending(info => info.ProductVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<HybridServerCoreBuildInfo>> GetBuildsAsync(
        CoreType coreType,
        string minecraftVersion,
        string? loader = null,
        CancellationToken cancellationToken = default)
    {
        ValidateMinecraftVersion(minecraftVersion);
        if (loader is not null)
        {
            ValidateLoader(loader);
        }

        IReadOnlyList<HybridServerCoreBuildInfo> builds = coreType switch
        {
            CoreType.Mohist => await GetMohistBuildsForVersionAsync(
                    minecraftVersion,
                    tolerateUnavailable: false,
                    cancellationToken)
                .ConfigureAwait(false),
            CoreType.Arclight => await GetArclightBuildsAsync(cancellationToken)
                .ConfigureAwait(false),
            CoreType.CatServer or CoreType.Akarin =>
                await GetPinnedGitHubBuildsAsync(coreType, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(coreType),
                $"{coreType} 不是 hybrid catalog 支援的核心。")
        };

        return builds
            .Where(build => build.MinecraftVersion.Equals(
                minecraftVersion,
                StringComparison.Ordinal))
            .Where(build => loader is null || string.Equals(
                build.Loader,
                loader,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(build => build.BuildVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<HybridServerCoreBuildInfo>> GetAllMohistLatestBuildsAsync(
        CancellationToken cancellationToken)
    {
        var versionsUri = new Uri(MohistApiBase, "project/mohist/versions");
        var bytes = await FirstPartyArtifactHttp.GetBoundedBytesAsync(
                _mohistApiClient,
                versionsUri,
                256 * 1024,
                IsMohistApiUri,
                "Mohist version API",
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "Mohist version API");
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() > 128)
        {
            throw new InvalidDataException("Mohist version API schema 或項目數量無效。");
        }

        var versions = new List<string>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Mohist version API item 必須是物件。");
            }

            var version = ReadRequiredString(item, "name", "Mohist version");
            ValidateMinecraftVersion(version);
            if (TryGetJavaMajor(version, out _))
            {
                versions.Add(version);
            }
        }

        var result = new List<HybridServerCoreBuildInfo>();
        foreach (var version in versions.Distinct(StringComparer.Ordinal))
        {
            var builds = await GetMohistBuildsForVersionAsync(
                    version,
                    tolerateUnavailable: true,
                    cancellationToken)
                .ConfigureAwait(false);
            result.AddRange(builds);
        }

        return result;
    }

    private async Task<IReadOnlyList<HybridServerCoreBuildInfo>> GetMohistBuildsForVersionAsync(
        string minecraftVersion,
        bool tolerateUnavailable,
        CancellationToken cancellationToken)
    {
        ValidateMinecraftVersion(minecraftVersion);
        if (!TryGetJavaMajor(minecraftVersion, out var javaMajor))
        {
            return [];
        }

        // Mohist's 1.16.5 line explicitly requires Java 11 even though upstream Minecraft 1.16.5
        // itself can run on Java 8. Newer lines follow the normal Minecraft runtime boundary.
        if (minecraftVersion.Equals("1.16.5", StringComparison.Ordinal))
        {
            javaMajor = 11;
        }

        var escaped = Uri.EscapeDataString(minecraftVersion);
        var metadataUri = new Uri(MohistApiBase, $"project/mohist/{escaped}/builds/latest");
        byte[] bytes;
        try
        {
            bytes = await FirstPartyArtifactHttp.GetBoundedBytesAsync(
                    _mohistApiClient,
                    metadataUri,
                    1024 * 1024,
                    IsMohistApiUri,
                    "Mohist latest-build API",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (
            tolerateUnavailable
            && exception.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.InternalServerError)
        {
            return [];
        }

        using var document = ParseJson(bytes, "Mohist latest-build API");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Mohist latest-build API 必須回傳物件。");
        }

        var buildId = ReadRequiredPositiveInt64(root, "id", "Mohist build");
        var sha256 = ReadRequiredSha256(root, "file_sha256", "Mohist build");
        _ = ReadRequiredDateTime(root, "build_date", "Mohist build");
        var commit = ReadRequiredObject(root, "commit", "Mohist build");
        var commitHash = ReadRequiredString(commit, "hash", "Mohist commit");
        ValidateGitCommit(commitHash, "Mohist commit hash");
        _ = ReadRequiredDateTime(commit, "commit_date", "Mohist commit");
        var loaderObject = ReadRequiredObject(root, "loader", "Mohist build");
        var forgeVersion = ReadRequiredString(loaderObject, "forge_version", "Mohist loader");
        ValidateVersionToken(forgeVersion, "Mohist Forge version");

        var fileName = $"mohist-{minecraftVersion}-{commitHash[..8]}-server.jar";
        var downloadUri = new Uri(
            MohistApiBase,
            $"project/mohist/{escaped}/builds/{buildId}/download");
        var headers = await FirstPartyArtifactHttp.ProbeDownloadAsync(
                _mohistApiClient,
                downloadUri,
                IsMohistApiUri,
                static (_, _) => false,
                FirstPartyArtifactHttp.MaximumArtifactBytes,
                fileName,
                "Mohist server JAR",
                cancellationToken)
            .ConfigureAwait(false);

        return
        [
            new HybridServerCoreBuildInfo(
                CoreType.Mohist,
                $"Mohist {minecraftVersion} build {buildId}",
                minecraftVersion,
                $"build-{buildId}",
                "forge",
                forgeVersion,
                buildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                javaMajor,
                IsStable: true,
                IsLegacy: IsLegacyMinecraftVersion(minecraftVersion),
                downloadUri,
                fileName,
                headers.Size,
                sha256,
                HybridArtifactVerification.UpstreamSha256,
                metadataUri,
                commitHash,
                buildId,
                SourceAssetId: null)
        ];
    }

    private async Task<IReadOnlyList<HybridServerCoreBuildInfo>> GetArclightBuildsAsync(
        CancellationToken cancellationToken)
    {
        var bytes = await FirstPartyArtifactHttp.GetBoundedBytesAsync(
                _githubApiClient,
                ArclightReleasesUri,
                FirstPartyArtifactHttp.MaximumJsonBytes,
                IsGitHubApiUri,
                "Arclight GitHub releases API",
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "Arclight GitHub releases API");
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() > 100)
        {
            throw new InvalidDataException("Arclight releases API schema 或項目數量無效。");
        }

        var result = new List<HybridServerCoreBuildInfo>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || ReadRequiredBoolean(release, "draft", "Arclight release")
                || ReadRequiredBoolean(release, "prerelease", "Arclight release"))
            {
                continue;
            }

            var releaseId = ReadRequiredPositiveInt64(release, "id", "Arclight release");
            var tag = ReadRequiredString(release, "tag_name", "Arclight release");
            ValidateReleaseTag(tag, "Arclight release tag");
            if (!release.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array
                || assets.GetArrayLength() > 32)
            {
                throw new InvalidDataException("Arclight release assets schema 無效。");
            }

            foreach (var asset in assets.EnumerateArray())
            {
                var candidate = TryParseArclightAsset(releaseId, tag, asset);
                if (candidate is not null)
                {
                    result.Add(candidate);
                }
            }
        }

        return result;
    }

    private static HybridServerCoreBuildInfo? TryParseArclightAsset(
        long releaseId,
        string tag,
        JsonElement asset)
    {
        if (asset.ValueKind != JsonValueKind.Object
            || !TryReadString(asset, "state", out var state)
            || !state.Equals("uploaded", StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = ReadRequiredString(asset, "name", "Arclight asset");
        var match = ArclightFileNameRegex().Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        var minecraftVersion = match.Groups["minecraft"].Value;
        var productVersion = match.Groups["product"].Value;
        var commit = match.Groups["commit"].Value;
        var loader = match.Groups["loader"].Value.ToLowerInvariant();
        ValidateMinecraftVersion(minecraftVersion);
        ValidateGitAbbreviation(commit, "Arclight asset commit");
        if (!tag.EndsWith('/' + productVersion, StringComparison.Ordinal)
            || fileName.Contains("SNAPSHOT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!TryReadString(asset, "digest", out var digest)
            || string.IsNullOrWhiteSpace(digest)
            || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sha256 = digest[7..];
        ValidateSha256(sha256, "Arclight asset digest");
        var size = ReadRequiredPositiveInt64(asset, "size", "Arclight asset");
        if (size > FirstPartyArtifactHttp.MaximumArtifactBytes)
        {
            throw new InvalidDataException("Arclight asset size 超過安全上限。");
        }

        var assetId = ReadRequiredPositiveInt64(asset, "id", "Arclight asset");
        var expectedDownload = BuildGitHubDownloadUri(
            "IzzelAliz",
            "Arclight",
            tag,
            fileName);
        var actualDownload = ReadRequiredUri(asset, "browser_download_url", "Arclight asset");
        if (!actualDownload.AbsoluteUri.Equals(expectedDownload.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Arclight asset download URL 與 release identity 不符。");
        }

        if (!TryGetJavaMajor(minecraftVersion, out var javaMajor))
        {
            return null;
        }

        return new HybridServerCoreBuildInfo(
            CoreType.Arclight,
            $"Arclight {minecraftVersion} {loader} {productVersion}",
            minecraftVersion,
            productVersion,
            loader,
            LoaderVersion: null,
            BuildVersion: $"{productVersion}-{commit}",
            javaMajor,
            IsStable: true,
            IsLegacy: IsLegacyMinecraftVersion(minecraftVersion),
            expectedDownload,
            fileName,
            size,
            sha256,
            HybridArtifactVerification.UpstreamSha256,
            ArclightReleasesUri,
            tag,
            releaseId,
            assetId);
    }

    private async Task<IReadOnlyList<HybridServerCoreBuildInfo>> GetPinnedGitHubBuildsAsync(
        CoreType coreType,
        CancellationToken cancellationToken)
    {
        var result = new List<HybridServerCoreBuildInfo>();
        foreach (var pinned in PinnedArtifacts.Where(item => item.CoreType == coreType))
        {
            var metadataUri = BuildGitHubReleaseApiUri(
                pinned.Owner,
                pinned.Repository,
                pinned.ReleaseId);
            var bytes = await FirstPartyArtifactHttp.GetBoundedBytesAsync(
                    _githubApiClient,
                    metadataUri,
                    2L * 1024 * 1024,
                    IsGitHubApiUri,
                    $"{pinned.CoreType} GitHub release API",
                    cancellationToken)
                .ConfigureAwait(false);
            using var document = ParseJson(bytes, $"{pinned.CoreType} GitHub release API");
            var release = document.RootElement;
            if (release.ValueKind != JsonValueKind.Object
                || ReadRequiredPositiveInt64(release, "id", "Pinned GitHub release") != pinned.ReleaseId
                || !ReadRequiredString(release, "tag_name", "Pinned GitHub release")
                    .Equals(pinned.ReleaseTag, StringComparison.Ordinal)
                || ReadRequiredBoolean(release, "draft", "Pinned GitHub release")
                || ReadRequiredBoolean(release, "prerelease", "Pinned GitHub release"))
            {
                throw new InvalidDataException(
                    $"{pinned.CoreType} pinned GitHub release identity 不符。");
            }

            if (!release.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array
                || assets.GetArrayLength() > 64)
            {
                throw new InvalidDataException("Pinned GitHub release assets schema 無效。");
            }

            JsonElement? selected = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && ReadRequiredPositiveInt64(asset, "id", "Pinned GitHub asset")
                        == pinned.AssetId)
                {
                    if (selected is not null)
                    {
                        throw new InvalidDataException("Pinned GitHub asset ID 重複。");
                    }

                    selected = asset;
                }
            }

            var selectedAsset = selected
                ?? throw new InvalidDataException("Pinned GitHub asset 已不存在。");
            ValidatePinnedAsset(selectedAsset, pinned);
            var downloadUri = BuildGitHubDownloadUri(
                pinned.Owner,
                pinned.Repository,
                pinned.ReleaseTag,
                pinned.FileName);
            result.Add(new HybridServerCoreBuildInfo(
                pinned.CoreType,
                pinned.DisplayName,
                pinned.MinecraftVersion,
                pinned.ReleaseTag,
                pinned.Loader,
                LoaderVersion: null,
                pinned.ReleaseTag,
                pinned.JavaMajorVersion,
                IsStable: true,
                IsLegacy: true,
                downloadUri,
                pinned.FileName,
                pinned.Size,
                pinned.Sha256,
                HybridArtifactVerification.PinnedCatalogSha256,
                metadataUri,
                pinned.ReleaseTag,
                pinned.ReleaseId,
                pinned.AssetId));
        }

        return result;
    }

    private static void ValidatePinnedAsset(JsonElement asset, PinnedGitHubArtifact pinned)
    {
        if (!ReadRequiredString(asset, "name", "Pinned GitHub asset")
                .Equals(pinned.FileName, StringComparison.Ordinal)
            || ReadRequiredPositiveInt64(asset, "size", "Pinned GitHub asset") != pinned.Size
            || !ReadRequiredString(asset, "state", "Pinned GitHub asset")
                .Equals("uploaded", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Pinned GitHub asset identity 或大小不符。");
        }

        var expectedUri = BuildGitHubDownloadUri(
            pinned.Owner,
            pinned.Repository,
            pinned.ReleaseTag,
            pinned.FileName);
        var actualUri = ReadRequiredUri(asset, "browser_download_url", "Pinned GitHub asset");
        if (!actualUri.AbsoluteUri.Equals(expectedUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Pinned GitHub asset URL 不符。");
        }

        if (TryReadString(asset, "digest", out var digest)
            && !string.IsNullOrEmpty(digest)
            && !digest.Equals("sha256:" + pinned.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Pinned GitHub asset 新增的 upstream digest 與 pin 不符。");
        }
    }

    internal static bool IsGitHubApiUri(Uri uri) =>
        IsHttpsHost(uri, "api.github.com")
        && uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Fragment);

    internal static bool IsMohistApiUri(Uri uri) =>
        IsHttpsHost(uri, "api.mohistmc.com")
        && uri.AbsolutePath.StartsWith("/project/mohist/", StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    internal static bool IsGitHubArtifactUri(Uri uri) =>
        (IsHttpsHost(uri, "github.com")
            && uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal))
        || IsHttpsHost(uri, "release-assets.githubusercontent.com");

    internal static bool IsAllowedGitHubArtifactRedirect(Uri current, Uri next)
    {
        if (IsHttpsHost(current, "github.com"))
        {
            return IsHttpsHost(next, "release-assets.githubusercontent.com");
        }

        return IsHttpsHost(current, "release-assets.githubusercontent.com")
            && IsHttpsHost(next, "release-assets.githubusercontent.com");
    }

    internal static Uri BuildExpectedGitHubDownloadUri(HybridServerCoreBuildInfo build)
    {
        var (owner, repository) = build.CoreType switch
        {
            CoreType.Arclight => ("IzzelAliz", "Arclight"),
            CoreType.CatServer => ("Luohuayu", "CatServer"),
            CoreType.Akarin => ("Akarin-project", "Akarin"),
            _ => throw new ArgumentOutOfRangeException(nameof(build), "不是 GitHub hybrid build。")
        };
        return BuildGitHubDownloadUri(owner, repository, build.SourceReleaseTag, build.FileName);
    }

    private static Uri BuildGitHubReleaseApiUri(string owner, string repository, long releaseId) =>
        new($"https://api.github.com/repos/{owner}/{repository}/releases/{releaseId}");

    private static Uri BuildGitHubDownloadUri(
        string owner,
        string repository,
        string tag,
        string fileName)
    {
        ValidateGitHubPathToken(owner, "GitHub owner", allowSlash: false);
        ValidateGitHubPathToken(repository, "GitHub repository", allowSlash: false);
        ValidateGitHubPathToken(tag, "GitHub release tag", allowSlash: true);
        ValidateFileName(fileName, "GitHub asset name");
        return new Uri($"https://github.com/{owner}/{repository}/releases/download/{tag}/{fileName}");
    }

    private static JsonDocument ParseJson(byte[] bytes, string context)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{context} JSON 無效。", exception);
        }
    }

    private static JsonElement ReadRequiredObject(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} 缺少 {name} 物件。");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement parent, string name, string context)
    {
        if (!TryReadString(parent, name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{context} 缺少 {name} 字串。");
        }

        if (value.Length > 512)
        {
            throw new InvalidDataException($"{context} 的 {name} 過長。");
        }

        return value;
    }

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property)
            || property.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static Uri ReadRequiredUri(JsonElement parent, string name, string context)
    {
        var value = ReadRequiredString(parent, name, context);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException($"{context} 的 {name} URI 無效。");
        }

        return uri;
    }

    private static long ReadRequiredPositiveInt64(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || parsed <= 0)
        {
            throw new InvalidDataException($"{context} 的 {name} 必須是正整數。");
        }

        return parsed;
    }

    private static bool ReadRequiredBoolean(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"{context} 的 {name} 必須是 boolean。");
        }

        return value.GetBoolean();
    }

    private static DateTimeOffset ReadRequiredDateTime(
        JsonElement parent,
        string name,
        string context)
    {
        var value = ReadRequiredString(parent, name, context);
        if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new InvalidDataException($"{context} 的 {name} 日期無效。");
        }

        return parsed;
    }

    private static string ReadRequiredSha256(JsonElement parent, string name, string context)
    {
        var value = ReadRequiredString(parent, name, context).ToLowerInvariant();
        ValidateSha256(value, context + " " + name);
        return value;
    }

    private static void ValidateMinecraftVersion(string value)
    {
        if (!MinecraftVersionRegex().IsMatch(value))
        {
            throw new ArgumentException($"Minecraft version 格式無效：{value}", nameof(value));
        }
    }

    private static void ValidateLoader(string value)
    {
        if (value is not ("forge" or "neoforge" or "fabric"))
        {
            throw new ArgumentException($"不支援的 hybrid loader：{value}", nameof(value));
        }
    }

    private static void ValidateVersionToken(string value, string context)
    {
        if (!VersionTokenRegex().IsMatch(value))
        {
            throw new InvalidDataException($"{context} 格式無效。");
        }
    }

    private static void ValidateReleaseTag(string value, string context) =>
        ValidateGitHubPathToken(value, context, allowSlash: true);

    private static void ValidateGitHubPathToken(string value, string context, bool allowSlash)
    {
        var valid = value.Length is > 0 and <= 128
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-'
                || (allowSlash && character == '/'))
            && !value.Contains("..", StringComparison.Ordinal)
            && !value.StartsWith('/')
            && !value.EndsWith('/');
        if (!valid)
        {
            throw new InvalidDataException($"{context} 格式無效。");
        }
    }

    private static void ValidateFileName(string value, string context)
    {
        if (value.Length is < 1 or > 200
            || !value.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-')))
        {
            throw new InvalidDataException($"{context} 格式無效。");
        }
    }

    private static void ValidateGitCommit(string value, string context)
    {
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{context} 格式無效。");
        }
    }

    private static void ValidateGitAbbreviation(string value, string context)
    {
        if (value.Length is < 7 or > 40 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{context} 格式無效。");
        }
    }

    private static void ValidateSha256(string value, string context)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{context} 格式無效。");
        }
    }

    private static void ValidateUserAgent(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("User-Agent 不得包含換行字元。", nameof(value));
        }
    }

    private static void ConfigureClient(HttpClient client, string userAgent)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent.Trim());
        }

        if (!client.DefaultRequestHeaders.Accept.Any())
        {
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    private static bool TryGetJavaMajor(string minecraftVersion, out int javaMajor)
    {
        javaMajor = 0;
        var parts = minecraftVersion.Split('.');
        if (parts.Length is < 2 or > 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || (parts.Length == 3 && !int.TryParse(parts[2], out _)))
        {
            return false;
        }

        var patch = parts.Length == 3 ? int.Parse(parts[2]) : 0;
        if (major != 1)
        {
            javaMajor = major >= 26 ? 25 : 21;
            return true;
        }

        javaMajor = minor switch
        {
            <= 16 => 8,
            17 => 16,
            <= 19 => 17,
            20 when patch <= 4 => 17,
            _ => 21
        };
        return true;
    }

    private static bool IsLegacyMinecraftVersion(string minecraftVersion)
    {
        var key = ParseMinecraftSortKey(minecraftVersion);
        return key < ParseMinecraftSortKey("1.21.1");
    }

    private static long ParseMinecraftSortKey(string version)
    {
        var parts = version.Split('.').Select(int.Parse).ToArray();
        return parts[0] * 1_000_000L + parts[1] * 1_000L + (parts.Length > 2 ? parts[2] : 0);
    }

    private static bool IsHttpsHost(Uri uri, string host) =>
        uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    [GeneratedRegex(@"^1\.\d{1,2}(?:\.\d{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex MinecraftVersionRegex();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._+\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTokenRegex();

    [GeneratedRegex(
        @"^arclight-(?<loader>fabric|forge|neoforge)-(?<minecraft>1\.\d{1,2}(?:\.\d{1,2})?)-(?<product>\d+\.\d+\.\d+)-(?<commit>[0-9a-fA-F]{7,40})\.jar$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArclightFileNameRegex();

    private sealed record PinnedGitHubArtifact(
        CoreType CoreType,
        string DisplayName,
        string Owner,
        string Repository,
        string MinecraftVersion,
        string ReleaseTag,
        long ReleaseId,
        long AssetId,
        string FileName,
        long Size,
        string Sha256,
        int JavaMajorVersion,
        string? Loader);
}
