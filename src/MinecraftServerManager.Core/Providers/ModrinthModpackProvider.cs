using System.Net.Http.Headers;
using System.Text.Json;

namespace MinecraftServerManager.Core.Providers;

public sealed record ModrinthModpackSearchRequest(
    string Query = "",
    string? GameVersion = null,
    string? Loader = null,
    int Offset = 0,
    int Limit = 20,
    bool IncludeUnknownEnvironment = false,
    string Index = "relevance",
    string? SourceCategory = null);

public sealed record ModrinthModpackProject(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    Uri? IconUri,
    string License,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Environments,
    long Downloads,
    DateTimeOffset DateModified,
    IReadOnlyList<Uri> GalleryImageUris);

public sealed record ModrinthModpackSearchPage(
    IReadOnlyList<ModrinthModpackProject> Projects,
    int Offset,
    int Limit,
    int TotalHits);

public sealed record ModrinthMrpackFile(
    string FileName,
    Uri DownloadUri,
    long Size,
    string Sha512,
    string? Sha1,
    bool Primary);

public sealed record ModrinthModpackVersion(
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    string VersionType,
    string Status,
    string Environment,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    DateTimeOffset DatePublished,
    ModrinthMrpackFile? MrpackFile);

/// <summary>
/// Read-only Modrinth v2 discovery provider for public modpack projects.
/// Long-lived callers must persist ProjectId and VersionId rather than mutable slugs/version labels.
/// </summary>
public sealed class ModrinthModpackProvider
{
    private static readonly Uri BaseUri = new("https://api.modrinth.com/v2/");
    private const long MaximumApiResponseBytes = 16L * 1024 * 1024;
    private const long MaximumApiErrorBytes = 64L * 1024;
    public const int MaximumGalleryImageUris = 32;
    private const int MaximumGalleryUriInputs = 128;
    private static readonly HashSet<string> SearchIndices = new(StringComparer.Ordinal)
    {
        "relevance", "downloads", "follows", "newest", "updated"
    };

    private static readonly string[] ServerEnvironments =
    [
        "environment:client_and_server",
        "environment:server_only",
        "environment:server_only_client_optional",
        "environment:dedicated_server_only",
        "environment:client_or_server",
        "environment:client_or_server_prefers_both",
        "environment:client_only_server_optional"
    ];

    private readonly HttpClient _httpClient;

    public ModrinthModpackProvider(HttpClient httpClient, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);

        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any(static header =>
                header.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public async Task<ModrinthModpackSearchPage> SearchAsync(
        ModrinthModpackSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0) throw new ArgumentOutOfRangeException(nameof(request), "Offset 不可小於 0。");
        if (request.Limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "Limit 必須介於 1 到 100。");
        if (!SearchIndices.Contains(request.Index)) throw new ArgumentException("不支援的 Modrinth 搜尋排序。", nameof(request));

        var facets = new List<string[]>
        {
            new[] { "project_type:modpack" },
            request.IncludeUnknownEnvironment
                ? ServerEnvironments.Append("environment:unknown").ToArray()
                : ServerEnvironments
        };
        if (!string.IsNullOrWhiteSpace(request.GameVersion))
        {
            facets.Add(new[] { $"versions:{NormalizeFacetValue(request.GameVersion, "遊戲版本")}" });
        }

        if (!string.IsNullOrWhiteSpace(request.Loader))
        {
            facets.Add(new[] { $"categories:{NormalizeFacetValue(request.Loader, "Loader").ToLowerInvariant()}" });
        }

        if (!string.IsNullOrWhiteSpace(request.SourceCategory))
        {
            var category = NormalizeFacetValue(request.SourceCategory, "來源分類").ToLowerInvariant();
            if (!category.Equals(request.Loader?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // Separate facet groups are ANDed by Modrinth. Loader and content category must
                // therefore remain separate rather than being placed in one OR group.
                facets.Add(new[] { $"categories:{category}" });
            }
        }

        var query = new Dictionary<string, string>
        {
            ["query"] = request.Query?.Trim() ?? string.Empty,
            ["facets"] = JsonSerializer.Serialize(facets),
            ["index"] = request.Index,
            ["offset"] = request.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = request.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        using var document = await GetJsonAsync(BuildUri("search", query), cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var projects = new List<ModrinthModpackProject>();
        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray())
            {
                if (!ReadRequiredString(hit, "project_type").Equals("modpack", StringComparison.Ordinal))
                {
                    continue;
                }

                projects.Add(new ModrinthModpackProject(
                    ReadRequiredString(hit, "project_id"),
                    ReadOptionalString(hit, "slug") ?? ReadRequiredString(hit, "project_id"),
                    ReadRequiredString(hit, "title"),
                    ReadOptionalString(hit, "description") ?? string.Empty,
                    ReadOptionalString(hit, "author") ?? string.Empty,
                    ReadOptionalUri(hit, "icon_url"),
                    ReadOptionalString(hit, "license") ?? "unknown",
                    ReadStringArray(hit, "versions"),
                    ReadStringArray(hit, "categories"),
                    ReadStringArray(hit, "environment"),
                    ReadOptionalInt64(hit, "downloads") ?? 0,
                    ReadOptionalDate(hit, "date_modified") ?? DateTimeOffset.MinValue,
                    ReadUriArray(hit, "gallery")));
            }
        }

        return new ModrinthModpackSearchPage(
            projects,
            ReadOptionalInt32(root, "offset") ?? request.Offset,
            ReadOptionalInt32(root, "limit") ?? request.Limit,
            ReadOptionalInt32(root, "total_hits") ?? projects.Count);
    }

    public async Task<IReadOnlyList<ModrinthModpackVersion>> GetVersionsAsync(
        string projectId,
        string? gameVersion = null,
        string? loader = null,
        bool includeUnknownEnvironment = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var query = new Dictionary<string, string> { ["include_changelog"] = "false" };
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query["game_versions"] = JsonSerializer.Serialize(new[] { gameVersion.Trim() });
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            query["loaders"] = JsonSerializer.Serialize(new[] { loader.Trim().ToLowerInvariant() });
        }

        using var document = await GetJsonAsync(
            BuildUri($"project/{Uri.EscapeDataString(projectId.Trim())}/version", query),
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Modrinth 版本回應不是陣列。");
        }

        return document.RootElement.EnumerateArray()
            .Select(ParseVersion)
            .Where(version => IsServerEnvironment(version.Environment, includeUnknownEnvironment))
            .OrderByDescending(static version => version.DatePublished)
            .ThenBy(static version => version.VersionId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ModrinthModpackVersion> GetVersionAsync(
        string versionId,
        bool allowUnknownEnvironment = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        using var document = await GetJsonAsync(
            new Uri(BaseUri, $"version/{Uri.EscapeDataString(versionId.Trim())}"),
            cancellationToken).ConfigureAwait(false);
        var version = ParseVersion(document.RootElement);
        if (!IsServerEnvironment(version.Environment, allowUnknownEnvironment))
        {
            throw new InvalidOperationException($"此 Modrinth 版本不支援專用伺服器：{version.Environment}");
        }

        return version;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        EnsureOfficialApiUri(uri, "Modrinth API request");
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("Modrinth API response is missing its final URI.");
        EnsureOfficialApiUri(finalUri, "Modrinth API response");
        if (!UrisEqual(uri, finalUri))
        {
            throw new InvalidDataException(
                $"Modrinth API redirected unexpectedly; the response was rejected: {finalUri}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var details = await ReadBoundedTextAsync(
                    response.Content,
                    MaximumApiErrorBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new HttpRequestException(
                $"Modrinth API 錯誤：HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}",
                null,
                response.StatusCode);
        }

        var bytes = await ReadBoundedBytesAsync(
                response.Content,
                MaximumApiResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Modrinth API 回傳了無效 JSON。", exception);
        }
    }

    private static void EnsureOfficialApiUri(Uri uri, string context)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IdnHost.Equals(BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(BaseUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{context} is not the official Modrinth v2 HTTPS origin: {uri}");
        }
    }

    private static bool UrisEqual(Uri expected, Uri actual)
        => expected.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            .Equals(
                actual.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                StringComparison.Ordinal);

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declaredLength && declaredLength > maximumBytes)
        {
            throw new InvalidDataException("Modrinth API 回應超過允許大小。");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Modrinth API 回應超過允許大小。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
        => System.Text.Encoding.UTF8.GetString(
            await ReadBoundedBytesAsync(content, maximumBytes, cancellationToken).ConfigureAwait(false));

    private static ModrinthModpackVersion ParseVersion(JsonElement element)
    {
        var files = new List<ModrinthMrpackFile>();
        if (element.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in fileArray.EnumerateArray())
            {
                var fileName = ReadOptionalString(file, "filename");
                var url = ReadOptionalUri(file, "url");
                var size = ReadOptionalInt64(file, "size");
                var hashes = default(JsonElement);
                var hasHashes = file.ValueKind == JsonValueKind.Object
                    && file.TryGetProperty("hashes", out hashes)
                    && hashes.ValueKind == JsonValueKind.Object;
                var sha512 = hasHashes ? ReadOptionalString(hashes, "sha512") : null;
                if (fileName is null || !fileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                    || url is null || size is null or < 0 || !IsHex(sha512, 128))
                {
                    continue;
                }

                files.Add(new ModrinthMrpackFile(
                    fileName,
                    url,
                    size.Value,
                    sha512!,
                    hasHashes && IsHex(ReadOptionalString(hashes, "sha1"), 40)
                        ? ReadOptionalString(hashes, "sha1")
                        : null,
                    file.TryGetProperty("primary", out var primary) && primary.ValueKind is JsonValueKind.True));
            }
        }

        var selected = files.FirstOrDefault(static file => file.Primary) ?? files.FirstOrDefault();
        return new ModrinthModpackVersion(
            ReadRequiredString(element, "project_id"),
            ReadRequiredString(element, "id"),
            ReadRequiredString(element, "name"),
            ReadRequiredString(element, "version_number"),
            ReadRequiredString(element, "version_type"),
            ReadRequiredString(element, "status"),
            ReadRequiredString(element, "environment"),
            ReadStringArray(element, "game_versions"),
            ReadStringArray(element, "loaders"),
            ReadOptionalDate(element, "date_published")
                ?? throw new InvalidDataException("Modrinth 版本缺少 date_published。"),
            selected);
    }

    private static bool IsServerEnvironment(string environment, bool includeUnknown) => environment switch
    {
        "client_and_server" or "server_only" or "server_only_client_optional" or "dedicated_server_only"
            or "client_or_server" or "client_or_server_prefers_both" or "client_only_server_optional" => true,
        "unknown" => includeUnknown,
        _ => false
    };

    private static Uri BuildUri(string relativePath, IReadOnlyDictionary<string, string> query)
    {
        var text = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(BaseUri, $"{relativePath}?{text}");
    }

    private static string ReadRequiredString(JsonElement element, string property)
        => ReadOptionalString(element, property)
           ?? throw new InvalidDataException($"Modrinth 回應缺少 {property}。");

    private static string? ReadOptionalString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? ReadOptionalUri(JsonElement element, string property)
        => Uri.TryCreate(ReadOptionalString(element, property), UriKind.Absolute, out var uri) ? uri : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .ToArray()
            : [];

    private static IReadOnlyList<Uri> ReadUriArray(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Take(MaximumGalleryUriInputs)
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => Uri.TryCreate(item.GetString(), UriKind.Absolute, out var uri) ? uri : null)
                .Where(static uri => uri is not null && IsSafeExternalHttps(uri))
                .Cast<Uri>()
                .DistinctBy(static uri => $"https://{uri.IdnHost.TrimEnd('.').ToLowerInvariant()}"
                                          + uri.GetComponents(
                                              UriComponents.PathAndQuery,
                                              UriFormat.UriEscaped),
                    StringComparer.Ordinal)
                .Take(MaximumGalleryImageUris)
                .ToArray()
            : [];

    private static bool IsSafeExternalHttps(Uri uri)
        => uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           && uri.IsDefaultPort
           && string.IsNullOrEmpty(uri.UserInfo)
           && !string.IsNullOrWhiteSpace(uri.IdnHost)
           && !uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           && !uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
           && !System.Net.IPAddress.TryParse(uri.IdnHost, out _);

    private static string NormalizeFacetValue(string value, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 64
            || normalized.Any(static character => char.IsControl(character)
                                                  || character is ':' or '"' or '\\'))
        {
            throw new ArgumentException($"Modrinth {label} facet 無效。", nameof(value));
        }

        return normalized;
    }

    private static long? ReadOptionalInt64(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var parsed) ? parsed : null;

    private static int? ReadOptionalInt32(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed) ? parsed : null;

    private static DateTimeOffset? ReadOptionalDate(JsonElement element, string property)
        => DateTimeOffset.TryParse(
            ReadOptionalString(element, property),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    private static bool IsHex(string? value, int expectedLength)
        => value?.Length == expectedLength && value.All(Uri.IsHexDigit);
}
