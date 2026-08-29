using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Strict read-only client-content catalog backed by Modrinth v2. The catalog accepts only public,
/// listed release versions and never follows an API redirect silently.
/// </summary>
public sealed class ModrinthClientContentCatalog : IModrinthClientContentCatalog
{
    private static readonly Uri ApiRoot = new("https://api.modrinth.com/v2/");
    private const long MaximumResponseBytes = 16L * 1024 * 1024;
    private const long MaximumErrorBytes = 64L * 1024;
    private const int MaximumArrayItems = 1_024;
    private readonly HttpClient _httpClient;

    public ModrinthClientContentCatalog(HttpClient httpClient, string userAgent)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any(static header =>
                string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public async Task<ModrinthClientContentSearchPage> SearchAsync(
        ModrinthClientContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSearchRequest(request);
        var facets = new List<string[]>
        {
            new[] { $"project_type:{GetProjectType(request.Kind)}" },
        };
        if (!string.IsNullOrWhiteSpace(request.GameVersion))
        {
            var gameVersion = NormalizeFacet(request.GameVersion, "game version");
            facets.Add(new[] { $"versions:{gameVersion}" });
        }

        if (request.Loader is { } loader)
        {
            facets.Add(new[] { $"categories:{GetLoaderName(loader)}" });
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

        var projects = new List<ModrinthClientContentProject>();
        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray().Take(MaximumArrayItems))
            {
                if (!TryGetContentKind(ReadOptionalString(hit, "project_type"), out var kind) ||
                    kind != request.Kind || IsClientUnsupported(hit))
                {
                    continue;
                }

                projects.Add(ParseSearchProject(hit, kind));
            }
        }

        return new ModrinthClientContentSearchPage(
            projects,
            Math.Max(0, ReadOptionalInt32(root, "offset") ?? request.Offset),
            Math.Clamp(ReadOptionalInt32(root, "limit") ?? request.Limit, 1, 100),
            Math.Max(0, ReadOptionalInt32(root, "total_hits") ?? projects.Count));
    }

    public async Task<ModrinthClientContentProject> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var id = ValidateIdentifier(projectId, nameof(projectId));
        using var document = await GetJsonAsync(
                new Uri(ApiRoot, $"project/{Uri.EscapeDataString(id)}"),
                cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (!TryGetContentKind(ReadOptionalString(root, "project_type"), out var kind) ||
            !string.Equals(ReadOptionalString(root, "status"), "approved", StringComparison.Ordinal) ||
            IsClientUnsupported(root))
        {
            throw new InvalidOperationException(
                "The selected Modrinth project is not approved client content.");
        }

        var returnedId = ValidateIdentifier(ReadRequiredString(root, "id"), "project id");
        if (!string.Equals(returnedId, id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Modrinth returned a different project than requested.");
        }

        return ParseProject(root, kind);
    }

    public async Task<IReadOnlyList<ModrinthClientContentVersion>> GetStableVersionsAsync(
        string projectId,
        string gameVersion,
        MinecraftClientLoader? loader = null,
        CancellationToken cancellationToken = default)
    {
        var id = ValidateIdentifier(projectId, nameof(projectId));
        var normalizedVersion = NormalizeFacet(gameVersion, "game version");
        var query = new Dictionary<string, string>
        {
            ["include_changelog"] = "false",
            ["game_versions"] = JsonSerializer.Serialize(new[] { normalizedVersion }),
        };
        string? loaderName = null;
        if (loader is { } selectedLoader)
        {
            loaderName = GetLoaderName(selectedLoader);
            query["loaders"] = JsonSerializer.Serialize(new[] { loaderName });
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
            .Cast<ModrinthClientContentVersion>()
            .Where(version =>
                string.Equals(version.ProjectId, id, StringComparison.Ordinal) &&
                version.GameVersions.Contains(normalizedVersion, StringComparer.Ordinal) &&
                (loaderName is null || version.Loaders.Contains(loaderName, StringComparer.Ordinal)))
            .OrderByDescending(static version => version.DatePublished)
            .ThenBy(static version => version.VersionId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ModrinthClientContentVersion> GetStableVersionAsync(
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
                "The selected Modrinth version is not a listed stable release.");
        }

        return version;
    }

    public async Task<ModrinthClientContentVersion> SelectStableVersionAsync(
        string projectId,
        string gameVersion,
        MinecraftClientLoader? loader = null,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetStableVersionsAsync(
                projectId,
                gameVersion,
                loader,
                cancellationToken)
            .ConfigureAwait(false);
        return versions.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No listed stable Modrinth release matches the selected Minecraft version and loader.");
    }

    internal static bool IsOfficialCdnUri(Uri? uri)
        => uri is { IsAbsoluteUri: true }
           && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           && uri.IsDefaultPort
           && string.IsNullOrEmpty(uri.UserInfo)
           && uri.IdnHost.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal);

    internal static string GetLoaderName(MinecraftClientLoader loader) => loader switch
    {
        MinecraftClientLoader.Vanilla => "minecraft",
        MinecraftClientLoader.Fabric => "fabric",
        MinecraftClientLoader.Forge => "forge",
        MinecraftClientLoader.NeoForge => "neoforge",
        MinecraftClientLoader.Quilt => "quilt",
        _ => throw new ArgumentOutOfRangeException(
            nameof(loader),
            loader,
            "This launcher type is not a Modrinth content loader."),
    };

    internal static bool TryGetLoader(string value, out MinecraftClientLoader loader)
    {
        loader = value switch
        {
            "minecraft" => MinecraftClientLoader.Vanilla,
            "fabric" => MinecraftClientLoader.Fabric,
            "forge" => MinecraftClientLoader.Forge,
            "neoforge" => MinecraftClientLoader.NeoForge,
            "quilt" => MinecraftClientLoader.Quilt,
            _ => default,
        };
        return value is "minecraft" or "fabric" or "forge" or "neoforge" or "quilt";
    }

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
        if (!UrisEqual(uri, finalUri) || (int)response.StatusCode is >= 300 and < 400)
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

    private static ModrinthClientContentProject ParseSearchProject(
        JsonElement root,
        MinecraftClientContentKind kind)
    {
        var id = ValidateIdentifier(ReadRequiredString(root, "project_id"), "project id");
        var slug = ValidateSlug(ReadOptionalString(root, "slug") ?? id);
        return new ModrinthClientContentProject(
            id,
            slug,
            kind,
            ReadRequiredString(root, "title"),
            ReadOptionalString(root, "description") ?? string.Empty,
            ReadOptionalString(root, "author") ?? string.Empty,
            ParseOfficialCdnUri(ReadOptionalString(root, "icon_url")),
            ReadStringArray(root, "versions"),
            ReadStringArray(root, "categories"),
            Math.Max(0, ReadOptionalInt64(root, "downloads") ?? 0),
            ReadOptionalDate(root, "date_modified") ?? DateTimeOffset.MinValue,
            BuildProjectPageUri(kind, slug));
    }

    private static ModrinthClientContentProject ParseProject(
        JsonElement root,
        MinecraftClientContentKind kind)
    {
        var id = ValidateIdentifier(ReadRequiredString(root, "id"), "project id");
        var slug = ValidateSlug(ReadOptionalString(root, "slug") ?? id);
        var loaders = ReadStringArray(root, "loaders");
        if (loaders.Count == 0)
        {
            loaders = ReadStringArray(root, "categories");
        }

        return new ModrinthClientContentProject(
            id,
            slug,
            kind,
            ReadRequiredString(root, "title"),
            ReadOptionalString(root, "description") ?? string.Empty,
            ReadOptionalString(root, "team") ?? string.Empty,
            ParseOfficialCdnUri(ReadOptionalString(root, "icon_url")),
            ReadStringArray(root, "game_versions"),
            loaders,
            Math.Max(0, ReadOptionalInt64(root, "downloads") ?? 0),
            ReadOptionalDate(root, "updated") ?? DateTimeOffset.MinValue,
            BuildProjectPageUri(kind, slug));
    }

    private static ModrinthClientContentVersion? ParseStableVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !string.Equals(ReadOptionalString(root, "version_type"), "release", StringComparison.Ordinal) ||
            !string.Equals(ReadOptionalString(root, "status"), "listed", StringComparison.Ordinal))
        {
            return null;
        }

        var files = new List<ModrinthClientContentFile>();
        if (root.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in fileArray.EnumerateArray().Take(64))
            {
                var fileName = ReadOptionalString(file, "filename");
                var uri = ParseSafeHttpsUri(ReadOptionalString(file, "url"));
                var size = ReadOptionalInt64(file, "size");
                if (!IsSafeFileName(fileName) || uri is null || size is null or < 1)
                {
                    continue;
                }

                files.Add(new ModrinthClientContentFile(
                    fileName!,
                    uri,
                    size.Value,
                    TryReadHash(file, "sha512", 128),
                    TryReadHash(file, "sha1", 40),
                    file.TryGetProperty("primary", out var primary) &&
                    primary.ValueKind == JsonValueKind.True));
            }
        }

        var dependencies = new List<ModrinthClientContentDependency>();
        if (root.TryGetProperty("dependencies", out var dependencyArray) &&
            dependencyArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var dependency in dependencyArray.EnumerateArray().Take(256))
            {
                var kind = ReadOptionalString(dependency, "dependency_type") switch
                {
                    "required" => ModrinthClientDependencyKind.Required,
                    "optional" => ModrinthClientDependencyKind.Optional,
                    "incompatible" => ModrinthClientDependencyKind.Incompatible,
                    "embedded" => ModrinthClientDependencyKind.Embedded,
                    _ => (ModrinthClientDependencyKind?)null,
                };
                if (kind is null)
                {
                    continue;
                }

                dependencies.Add(new ModrinthClientContentDependency(
                    TryValidateIdentifier(ReadOptionalString(dependency, "project_id")),
                    TryValidateIdentifier(ReadOptionalString(dependency, "version_id")),
                    IsSafeFileName(ReadOptionalString(dependency, "file_name"))
                        ? ReadOptionalString(dependency, "file_name")
                        : null,
                    kind.Value));
            }
        }

        return new ModrinthClientContentVersion(
            ValidateIdentifier(ReadRequiredString(root, "project_id"), "project id"),
            ValidateIdentifier(ReadRequiredString(root, "id"), "version id"),
            ReadRequiredString(root, "name"),
            ReadRequiredString(root, "version_number"),
            ReadStringArray(root, "game_versions"),
            ReadStringArray(root, "loaders"),
            ReadOptionalDate(root, "date_published")
                ?? throw new InvalidDataException("Modrinth version is missing date_published."),
            files,
            dependencies);
    }

    private static bool IsClientUnsupported(JsonElement root)
        => string.Equals(ReadOptionalString(root, "client_side"), "unsupported", StringComparison.Ordinal);

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

    private static bool TryGetContentKind(string? projectType, out MinecraftClientContentKind kind)
    {
        kind = projectType switch
        {
            "mod" => MinecraftClientContentKind.Mod,
            "resourcepack" => MinecraftClientContentKind.ResourcePack,
            "shader" => MinecraftClientContentKind.ShaderPack,
            _ => default,
        };
        return projectType is "mod" or "resourcepack" or "shader";
    }

    private static string GetProjectType(MinecraftClientContentKind kind) => kind switch
    {
        MinecraftClientContentKind.Mod => "mod",
        MinecraftClientContentKind.ResourcePack => "resourcepack",
        MinecraftClientContentKind.ShaderPack => "shader",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "Only mods, resource packs and shader packs can be downloaded from Modrinth."),
    };

    private static Uri BuildProjectPageUri(MinecraftClientContentKind kind, string slug)
        => new($"https://modrinth.com/{GetProjectType(kind)}/{Uri.EscapeDataString(slug)}");

    private static Uri? ParseOfficialCdnUri(string? text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsOfficialCdnUri(uri)
            ? uri
            : null;

    private static Uri? ParseSafeHttpsUri(string? text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
           uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
           uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;

    private static string GetSortName(ModrinthClientContentSort sort) => sort switch
    {
        ModrinthClientContentSort.Relevance => "relevance",
        ModrinthClientContentSort.Downloads => "downloads",
        ModrinthClientContentSort.Follows => "follows",
        ModrinthClientContentSort.Newest => "newest",
        ModrinthClientContentSort.Updated => "updated",
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unsupported Modrinth sort."),
    };

    private static void ValidateSearchRequest(ModrinthClientContentSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = GetProjectType(request.Kind);
        if (request.Query is null || request.Query.Length > 256 || request.Offset < 0 ||
            request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Modrinth search bounds are invalid.");
        }

        if (request.GameVersion is not null)
        {
            _ = NormalizeFacet(request.GameVersion, "game version");
        }

        if (request.Loader is { } loader)
        {
            _ = GetLoaderName(loader);
        }
    }

    private static string NormalizeFacet(string value, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 64 || normalized.Any(static character =>
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

    private static string? TryValidateIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return ValidateIdentifier(value, nameof(value));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ValidateSlug(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 128 || normalized.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException("Modrinth project slug is invalid.");
        }

        return normalized;
    }

    private static bool IsSafeFileName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && name is not "." and not ".." &&
           string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) &&
           !name.Any(static character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character));

    private static Uri BuildUri(string relativePath, IReadOnlyDictionary<string, string> query)
    {
        var queryText = string.Join('&', query.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(ApiRoot, $"{relativePath}?{queryText}");
    }

    private static void EnsureApiUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
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
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Take(MaximumArrayItems)
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .Where(static item => !string.IsNullOrWhiteSpace(item) && item.Length <= 128)
                .Distinct(StringComparer.Ordinal)
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
