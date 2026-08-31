using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Read-only Modrinth v2 catalog for public client-compatible modpacks. Only stable, listed
/// <c>release</c> versions with a hash-addressed .mrpack on the official CDN are installable.
/// </summary>
public sealed class ModrinthClientModpackCatalog : IModrinthClientModpackCatalog
{
    private static readonly Uri ApiRoot = new("https://api.modrinth.com/v2/");
    private const long MaximumResponseBytes = 16L * 1024 * 1024;
    private const long MaximumErrorBytes = 64L * 1024;
    private const int MaximumArrayItems = 1_024;
    private const int MaximumGalleryImages = 32;
    private readonly HttpClient _httpClient;

    public ModrinthClientModpackCatalog(HttpClient httpClient, string userAgent)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any(header =>
                string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public async Task<ModrinthClientModpackSearchPage> SearchAsync(
        ModrinthClientModpackSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSearchRequest(request);
        var facets = new List<string[]>
        {
            new[] { "project_type:modpack" },
            new[]
            {
                "environment:client_and_server",
                "environment:client_only",
                "environment:client_only_server_optional",
                "environment:singleplayer_only",
                "environment:server_only_client_optional",
                "environment:client_or_server",
                "environment:client_or_server_prefers_both",
            },
        };
        if (!string.IsNullOrWhiteSpace(request.GameVersion))
        {
            facets.Add(new[] { $"versions:{NormalizeFacet(request.GameVersion, "game version")}" });
        }

        if (request.Loader is { } loader)
        {
            facets.Add(new[] { $"categories:{GetLoaderFacet(loader)}" });
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            facets.Add(new[]
            {
                $"categories:{NormalizeFacet(request.Category, "category").ToLowerInvariant()}",
            });
        }

        var uri = BuildUri(
            "search",
            new Dictionary<string, string>
            {
                ["query"] = request.Query.Trim(),
                ["facets"] = JsonSerializer.Serialize(facets),
                ["index"] = GetSortName(request.Sort),
                ["offset"] = request.Offset.ToString(CultureInfo.InvariantCulture),
                ["limit"] = request.Limit.ToString(CultureInfo.InvariantCulture),
            });
        using var document = await GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Modrinth search response must be an object.");
        }

        var projects = new List<ModrinthClientModpackProject>();
        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray().Take(MaximumArrayItems))
            {
                if (!string.Equals(ReadOptionalString(hit, "project_type"), "modpack", StringComparison.Ordinal))
                {
                    continue;
                }

                var project = ParseSearchProject(hit);
                if (project.Environments.Any(IsClientEnvironment))
                {
                    projects.Add(project);
                }
            }
        }

        return new ModrinthClientModpackSearchPage(
            projects,
            Math.Max(0, ReadOptionalInt32(root, "offset") ?? request.Offset),
            Math.Clamp(ReadOptionalInt32(root, "limit") ?? request.Limit, 1, 100),
            Math.Max(0, ReadOptionalInt32(root, "total_hits") ?? projects.Count));
    }

    public Task<ModrinthClientModpackSearchPage> GetPopularAsync(
        ModrinthClientModpackSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SearchAsync(
            request with { Query = string.Empty, Sort = ModrinthClientModpackSort.Downloads },
            cancellationToken);
    }

    public async Task<ModrinthClientModpackProject> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var id = ValidateIdentifier(projectId, nameof(projectId));
        using var document = await GetJsonAsync(
                new Uri(ApiRoot, $"project/{Uri.EscapeDataString(id)}"),
                cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (!string.Equals(ReadRequiredString(root, "project_type"), "modpack", StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(root, "status"), "approved", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected Modrinth project is not an approved public modpack.");
        }

        var environments = ReadStringArray(root, "environment");
        if (!environments.Any(IsClientEnvironment))
        {
            throw new InvalidOperationException("The selected Modrinth project is not client compatible.");
        }

        var gallery = ReadProjectGallery(root);
        var featured = ReadOfficialCdnUri(root, "featured_gallery") ?? gallery.FirstOrDefault();
        var description = ReadOptionalString(root, "description") ?? string.Empty;
        return new ModrinthClientModpackProject(
            ValidateIdentifier(ReadRequiredString(root, "id"), "project id"),
            ReadOptionalString(root, "slug") ?? ReadRequiredString(root, "id"),
            ReadRequiredString(root, "title"),
            description,
            string.Empty,
            ReadOfficialCdnUri(root, "icon_url"),
            featured,
            gallery,
            ReadStringArray(root, "game_versions"),
            ReadStringArray(root, "categories"),
            environments,
            Math.Max(0, ReadOptionalInt64(root, "downloads") ?? 0),
            Math.Max(0, ReadOptionalInt64(root, "followers") ?? 0),
            ReadOptionalDate(root, "updated") ?? DateTimeOffset.MinValue,
            ReadOptionalString(root, "body") ?? description);
    }

    public async Task<IReadOnlyList<ModrinthClientModpackVersion>> GetStableVersionsAsync(
        string projectId,
        string? gameVersion = null,
        MinecraftClientLoader? loader = null,
        CancellationToken cancellationToken = default)
    {
        var id = ValidateIdentifier(projectId, nameof(projectId));
        var query = new Dictionary<string, string> { ["include_changelog"] = "false" };
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query["game_versions"] = JsonSerializer.Serialize(
                new[] { NormalizeFacet(gameVersion, "game version") });
        }

        if (loader is { } selectedLoader)
        {
            query["loaders"] = JsonSerializer.Serialize(new[] { GetLoaderFacet(selectedLoader) });
        }

        using var document = await GetJsonAsync(
                BuildUri($"project/{Uri.EscapeDataString(id)}/version", query),
                cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Modrinth version response must be an array.");
        }

        return document.RootElement.EnumerateArray()
            .Take(MaximumArrayItems)
            .Select(ParseStableVersion)
            .Where(static version => version is not null)
            .Cast<ModrinthClientModpackVersion>()
            .Where(version => string.Equals(version.ProjectId, id, StringComparison.Ordinal))
            .OrderByDescending(static version => version.DatePublished)
            .ThenBy(static version => version.VersionId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ModrinthClientModpackVersion> GetStableVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var id = ValidateIdentifier(versionId, nameof(versionId));
        using var document = await GetJsonAsync(
                new Uri(ApiRoot, $"version/{Uri.EscapeDataString(id)}"),
                cancellationToken)
            .ConfigureAwait(false);
        var version = ParseStableVersion(document.RootElement);
        if (version is null || !string.Equals(version.VersionId, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected Modrinth version is not a listed, client-compatible stable release with an official .mrpack.");
        }

        return version;
    }

    internal static bool IsOfficialCdnUri(Uri? uri)
        => uri is { IsAbsoluteUri: true }
           && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           && uri.IsDefaultPort
           && string.IsNullOrEmpty(uri.UserInfo)
           && uri.IdnHost.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal);

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        EnsureApiUri(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("Modrinth API response is missing its request URI.");
        EnsureApiUri(finalUri);
        if (!UrisEqual(uri, finalUri))
        {
            throw new InvalidDataException("Modrinth API redirects are not accepted.");
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidDataException("Modrinth API redirects are not accepted.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = Encoding.UTF8.GetString(
                await ReadBoundedAsync(response.Content, MaximumErrorBytes, cancellationToken)
                    .ConfigureAwait(false));
            throw new HttpRequestException(
                $"Modrinth API returned HTTP {(int)response.StatusCode}: {detail}",
                null,
                response.StatusCode);
        }

        var bytes = await ReadBoundedAsync(response.Content, MaximumResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Modrinth API returned invalid JSON.", exception);
        }
    }

    private static ModrinthClientModpackProject ParseSearchProject(JsonElement root)
    {
        var gallery = ReadUriArray(root, "gallery");
        return new ModrinthClientModpackProject(
            ValidateIdentifier(ReadRequiredString(root, "project_id"), "project id"),
            ReadOptionalString(root, "slug") ?? ReadRequiredString(root, "project_id"),
            ReadRequiredString(root, "title"),
            ReadOptionalString(root, "description") ?? string.Empty,
            ReadOptionalString(root, "author") ?? string.Empty,
            ReadOfficialCdnUri(root, "icon_url"),
            ReadOfficialCdnUri(root, "featured_gallery") ?? gallery.FirstOrDefault(),
            gallery,
            ReadStringArray(root, "versions"),
            ReadStringArray(root, "categories"),
            ReadStringArray(root, "environment"),
            Math.Max(0, ReadOptionalInt64(root, "downloads") ?? 0),
            Math.Max(0, ReadOptionalInt64(root, "follows") ?? 0),
            ReadOptionalDate(root, "date_modified") ?? DateTimeOffset.MinValue);
    }

    private static ModrinthClientModpackVersion? ParseStableVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !string.Equals(ReadOptionalString(root, "version_type"), "release", StringComparison.Ordinal) ||
            !string.Equals(ReadOptionalString(root, "status"), "listed", StringComparison.Ordinal) ||
            !IsClientEnvironment(ReadOptionalString(root, "environment")))
        {
            return null;
        }

        var files = new List<ModrinthClientMrpackFile>();
        if (root.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in fileArray.EnumerateArray().Take(64))
            {
                var fileName = ReadOptionalString(file, "filename");
                var uri = ReadOfficialCdnUri(file, "url");
                var size = ReadOptionalInt64(file, "size");
                var sha512 = TryReadHash(file, "sha512", 128);
                var sha1 = TryReadHash(file, "sha1", 40);
                if (fileName is null || fileName.Length > 255 ||
                    !fileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                    uri is null || size is null or < 1 || sha512 is null)
                {
                    continue;
                }

                files.Add(new ModrinthClientMrpackFile(
                    fileName,
                    uri,
                    size.Value,
                    sha512,
                    sha1,
                    file.TryGetProperty("primary", out var primary) &&
                    primary.ValueKind == JsonValueKind.True));
            }
        }

        var selected = files.FirstOrDefault(static file => file.Primary) ?? files.FirstOrDefault();
        if (selected is null)
        {
            return null;
        }

        return new ModrinthClientModpackVersion(
            ValidateIdentifier(ReadRequiredString(root, "project_id"), "project id"),
            ValidateIdentifier(ReadRequiredString(root, "id"), "version id"),
            ReadRequiredString(root, "name"),
            ReadRequiredString(root, "version_number"),
            ReadRequiredString(root, "environment"),
            ReadStringArray(root, "game_versions"),
            ReadStringArray(root, "loaders"),
            ReadOptionalDate(root, "date_published")
                ?? throw new InvalidDataException("Modrinth version is missing date_published."),
            Math.Max(0, ReadOptionalInt64(root, "downloads") ?? 0),
            selected);
    }

    private static string? TryReadHash(JsonElement file, string name, int length)
    {
        if (!file.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hash = ReadOptionalString(hashes, name)?.Trim();
        return hash is not null && hash.Length == length && hash.All(Uri.IsHexDigit)
            ? hash.ToLowerInvariant()
            : null;
    }

    private static IReadOnlyList<Uri> ReadProjectGallery(JsonElement root)
    {
        if (!root.TryGetProperty("gallery", out var gallery) || gallery.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<Uri>();
        foreach (var item in gallery.EnumerateArray().Take(MaximumGalleryImages * 4))
        {
            var uri = item.ValueKind switch
            {
                JsonValueKind.String => ParseOfficialCdnUri(item.GetString()),
                JsonValueKind.Object => ReadOfficialCdnUri(item, "url"),
                _ => null,
            };
            if (uri is not null && !result.Contains(uri))
            {
                result.Add(uri);
                if (result.Count >= MaximumGalleryImages)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<Uri> ReadUriArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Take(MaximumGalleryImages * 4)
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => ParseOfficialCdnUri(item.GetString()))
            .Where(static uri => uri is not null)
            .Cast<Uri>()
            .Distinct()
            .Take(MaximumGalleryImages)
            .ToArray();
    }

    private static Uri? ReadOfficialCdnUri(JsonElement root, string property)
        => ParseOfficialCdnUri(ReadOptionalString(root, property));

    private static Uri? ParseOfficialCdnUri(string? text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsOfficialCdnUri(uri)
            ? uri
            : null;

    private static bool IsClientEnvironment(string? environment) => environment is
        "client_and_server" or
        "client_only" or
        "client_only_server_optional" or
        "singleplayer_only" or
        "server_only_client_optional" or
        "client_or_server" or
        "client_or_server_prefers_both";

    private static string GetLoaderFacet(MinecraftClientLoader loader) => loader switch
    {
        MinecraftClientLoader.Vanilla => "minecraft",
        MinecraftClientLoader.Fabric => "fabric",
        MinecraftClientLoader.Forge => "forge",
        MinecraftClientLoader.NeoForge => "neoforge",
        MinecraftClientLoader.Quilt => "quilt",
        _ => throw new ArgumentOutOfRangeException(nameof(loader), loader, "Unsupported Modrinth loader filter."),
    };

    private static string GetSortName(ModrinthClientModpackSort sort) => sort switch
    {
        ModrinthClientModpackSort.Relevance => "relevance",
        ModrinthClientModpackSort.Downloads => "downloads",
        ModrinthClientModpackSort.Follows => "follows",
        ModrinthClientModpackSort.Newest => "newest",
        ModrinthClientModpackSort.Updated => "updated",
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unsupported Modrinth sort."),
    };

    private static void ValidateSearchRequest(ModrinthClientModpackSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Query is null || request.Query.Length > 256 || request.Offset < 0 ||
            request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Modrinth search bounds are invalid.");
        }

        _ = GetSortName(request.Sort);
        if (request.Loader is { } loader)
        {
            _ = GetLoaderFacet(loader);
        }

        if (!string.IsNullOrWhiteSpace(request.GameVersion))
        {
            _ = NormalizeFacet(request.GameVersion, "game version");
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            _ = NormalizeFacet(request.Category, "category");
        }
    }

    private static string NormalizeFacet(string value, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 64 ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException($"Modrinth {label} filter is invalid.", nameof(value));
        }

        return normalized;
    }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 64 || !normalized.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Modrinth identifier is invalid.", parameterName);
        }

        return normalized;
    }

    private static Uri BuildUri(string relativePath, IReadOnlyDictionary<string, string> query)
    {
        var queryText = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(ApiRoot, $"{relativePath}?{queryText}");
    }

    private static void EnsureApiUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IdnHost.Equals(ApiRoot.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(ApiRoot.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Only the official Modrinth v2 HTTPS API is allowed: {uri}");
        }
    }

    private static bool UrisEqual(Uri expected, Uri actual)
        => string.Equals(
            expected.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
            actual.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
            StringComparison.Ordinal);

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > maximumBytes)
        {
            throw new InvalidDataException("Modrinth API response exceeded the safe size limit.");
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
                throw new InvalidDataException("Modrinth API response exceeded the safe size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ReadRequiredString(JsonElement root, string property)
        => ReadOptionalString(root, property)
           ?? throw new InvalidDataException($"Modrinth response is missing {property}.");

    private static string? ReadOptionalString(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Take(MaximumArrayItems)
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .Where(static item => !string.IsNullOrWhiteSpace(item) && item.Length <= 128)
                .ToArray()
            : [];

    private static int? ReadOptionalInt32(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? ReadOptionalInt64(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;

    private static DateTimeOffset? ReadOptionalDate(JsonElement root, string property)
        => DateTimeOffset.TryParse(
            ReadOptionalString(root, property),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date
            : null;
}
