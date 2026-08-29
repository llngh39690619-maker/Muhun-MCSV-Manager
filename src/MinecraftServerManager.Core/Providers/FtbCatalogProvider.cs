using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Performs user-initiated FTB catalogue queries. Search responses contain only pack IDs, so each
/// result is hydrated through the official pack endpoint before it is returned.
/// </summary>
public sealed class FtbCatalogProvider
{
    private static readonly Uri ApiRoot =
        new("https://api.feed-the-beast.com/v1/modpacks/public/modpack/");
    private const string PublicApiPathPrefix = "/v1/modpacks/public/modpack/";
    private const int MaximumHydrationConcurrency = 4;
    private const int MaximumArtworkEntries = 100;
    private const int MaximumArtworkMirrorsPerEntry = 8;
    // Current official featured packs can exceed 11,000 manifest entries. Keep enough headroom
    // for those public releases while retaining a finite, independently enforced safety limit.
    private const int MaximumManifestFiles = 20_000;
    private const int MaximumManifestMirrorsPerFile = 16;
    private const long MaximumSearchBytes = 1L * 1024 * 1024;
    private const long MaximumFeaturedBytes = 1L * 1024 * 1024;
    private const long MaximumPackBytes = 8L * 1024 * 1024;
    private const long MaximumManifestBytes = 32L * 1024 * 1024;
    private const long MaximumManifestFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumManifestTotalBytes = 16L * 1024 * 1024 * 1024;
    private static readonly IReadOnlySet<string> OfficialFileHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "files.feed-the-beast.com",
            "cdn.feed-the-beast.com",
            "edge.forgecdn.net",
        };

    private readonly HttpClient _httpClient;
    private readonly string _userAgent;

    public FtbCatalogProvider(HttpClient httpClient, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (userAgent.Contains('\r', StringComparison.Ordinal)
            || userAgent.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("User-Agent 不得包含換行字元。", nameof(userAgent));
        }

        _httpClient = httpClient;
        _userAgent = userAgent;
    }

    public async Task<FtbSearchResult> SearchAsync(
        string term,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "FTB 搜尋數量必須介於 1 到 100。");
        }

        var relative = $"search/{limit}?term={Uri.EscapeDataString(term.Trim())}";
        using var document = await GetJsonAsync(relative, MaximumSearchBytes, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessStatus(document.RootElement, "FTB 搜尋");

        if (!document.RootElement.TryGetProperty("packs", out var packsElement)
            || packsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("FTB 搜尋回應缺少 packs 陣列。");
        }

        var packIds = packsElement.EnumerateArray()
            .Where(element => element.TryGetInt32(out var id) && id > 0)
            .Select(element => element.GetInt32())
            .Distinct()
            // The live FTB API rounds some requested result counts upward (for example, 8 to 10).
            // Hydrating only the caller's bounded limit is safe and keeps a normal upstream response
            // from being reported as a catalogue failure.
            .Take(limit)
            .ToArray();
        return await HydratePacksAsync(packIds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FtbSearchResult> GetFeaturedAsync(
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "FTB 推薦數量必須介於 1 到 100。");
        }

        using var document = await GetJsonAsync(
                $"featured/{limit}",
                MaximumFeaturedBytes,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessStatus(document.RootElement, "FTB 熱門推薦");
        if (!document.RootElement.TryGetProperty("packs", out var packsElement)
            || packsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("FTB 熱門推薦回應缺少 packs 陣列。");
        }

        var packIds = packsElement.EnumerateArray()
            .Where(element => element.TryGetInt32(out var id) && id > 0)
            .Select(element => element.GetInt32())
            .Distinct()
            .Take(limit)
            .ToArray();
        return await HydratePacksAsync(packIds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FtbPack> GetPackAsync(
        int packId,
        CancellationToken cancellationToken = default)
    {
        if (packId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packId), "FTB Pack ID 必須是正整數。");
        }

        using var document = await GetJsonAsync(
                packId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MaximumPackBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccessStatus(root, "FTB Pack");

        var responseId = ReadRequiredInt(root, "id", "FTB Pack");
        if (responseId != packId)
        {
            throw new InvalidDataException($"FTB Pack ID 不符，要求 {packId}，回應 {responseId}。");
        }

        var name = RequireBoundedString(root, "name", "FTB Pack", 512);
        var slug = ReadOptionalBoundedString(root, "slug", "FTB Pack", 256) ?? string.Empty;
        var isPrivate = ReadOptionalBoolean(root, "private");
        var synopsis = ReadOptionalBoundedString(root, "synopsis", "FTB Pack", 16_384)
                       ?? ReadOptionalBoundedString(root, "description", "FTB Pack", 16_384);
        var installCount = ReadOptionalLong(root, "installs");
        var artwork = ReadArtwork(root);
        var versions = new List<FtbPackVersion>();

        if (root.TryGetProperty("versions", out var versionsElement)
            && versionsElement.ValueKind == JsonValueKind.Array)
        {
            if (versionsElement.GetArrayLength() > 10_000)
            {
                throw new InvalidDataException("FTB Pack contains too many versions.");
            }

            foreach (var versionElement in versionsElement.EnumerateArray())
            {
                var versionId = ReadRequiredInt(versionElement, "id", "FTB Pack version");
                if (versionId <= 0)
                {
                    throw new InvalidDataException("FTB Pack version ID 必須是正整數。");
                }

                var targets = new List<FtbTarget>();
                if (versionElement.TryGetProperty("targets", out var targetsElement)
                    && targetsElement.ValueKind == JsonValueKind.Array)
                {
                    if (targetsElement.GetArrayLength() > 32)
                    {
                        throw new InvalidDataException("FTB Pack version contains too many targets.");
                    }

                    foreach (var targetElement in targetsElement.EnumerateArray())
                    {
                        var type = ReadOptionalString(targetElement, "type");
                        var targetName = ReadOptionalString(targetElement, "name");
                        var targetVersion = ReadOptionalString(targetElement, "version");
                        if (type?.Length > 64 || targetName?.Length > 128 ||
                            targetVersion?.Length > 128 ||
                            type?.Any(char.IsControl) == true ||
                            targetName?.Any(char.IsControl) == true ||
                            targetVersion?.Any(char.IsControl) == true)
                        {
                            throw new InvalidDataException(
                                "FTB Pack version target is too long or unsafe.");
                        }
                        if (!string.IsNullOrWhiteSpace(type)
                            && !string.IsNullOrWhiteSpace(targetName)
                            && !string.IsNullOrWhiteSpace(targetVersion))
                        {
                            targets.Add(new FtbTarget(type, targetName, targetVersion));
                        }
                    }
                }

                versions.Add(new FtbPackVersion(
                    versionId,
                    RequireBoundedString(versionElement, "name", "FTB Pack version", 512),
                    RequireBoundedString(versionElement, "type", "FTB Pack version", 64),
                    FtbTimestampNormalizer.NormalizeToUnixTimeMilliseconds(
                        ReadOptionalLong(versionElement, "updated")),
                    targets,
                    ReadOptionalBoolean(versionElement, "private")));
            }
        }

        versions.Sort((left, right) => right.Id.CompareTo(left.Id));
        return new FtbPack(
            responseId,
            name,
            slug,
            isPrivate,
            versions,
            synopsis,
            installCount,
            artwork);
    }

    public async Task<FtbPackVersionManifest> GetVersionManifestAsync(
        int packId,
        int versionId,
        CancellationToken cancellationToken = default)
    {
        if (packId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packId), "FTB Pack ID 必須是正整數。");
        }

        if (versionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(versionId), "FTB Pack version ID 必須是正整數。");
        }

        using var document = await GetJsonAsync(
                $"{packId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/" +
                versionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccessStatus(root, "FTB Pack manifest");
        RejectDuplicateJsonProperties(root);

        var responsePackId = ReadRequiredInt(root, "parent", "FTB Pack manifest");
        var responseVersionId = ReadRequiredInt(root, "id", "FTB Pack manifest");
        if (responsePackId != packId || responseVersionId != versionId)
        {
            throw new InvalidDataException(
                $"FTB manifest identity mismatch: expected {packId}/{versionId}, got {responsePackId}/{responseVersionId}.");
        }

        var targets = ReadTargets(root, "FTB Pack manifest");
        var memory = ReadMemorySpecs(root);
        if (!root.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array ||
            filesElement.GetArrayLength() > MaximumManifestFiles)
        {
            throw new InvalidDataException(
                $"FTB Pack manifest files are missing or exceed {MaximumManifestFiles} entries.");
        }

        var files = new List<FtbPackFile>(filesElement.GetArrayLength());
        var destinations = new Dictionary<string, FtbPackFile>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("FTB Pack manifest contains a non-object file entry.");
            }

            var name = ReadRequiredString(fileElement, "name", "FTB Pack file");
            var path = ReadRequiredString(fileElement, "path", "FTB Pack file");
            var collisionKey = NormalizeManifestDestination(path, name);

            var size = ReadRequiredLong(fileElement, "size", "FTB Pack file");
            if (size is < 0 or > MaximumManifestFileBytes)
            {
                throw new InvalidDataException("FTB Pack file size is outside the safe limit.");
            }

            var primary = ReadOfficialFileUri(RequireBoundedString(
                fileElement,
                "url",
                "FTB Pack file",
                4_096));
            var mirrors = ReadManifestMirrors(fileElement);
            if (!fileElement.TryGetProperty("hashes", out var hashesElement) ||
                hashesElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("FTB Pack file hashes are missing.");
            }

            var sha1 = ReadRequiredHash(hashesElement, "sha1", 40);
            var sha256 = ReadRequiredHash(hashesElement, "sha256", 64);
            var sha512 = ReadRequiredHash(hashesElement, "sha512", 128);
            var legacySha1 = ReadOptionalString(fileElement, "sha1");
            if (legacySha1 is not null && !legacySha1.Equals(sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("FTB Pack file SHA-1 fields disagree.");
            }

            var file = new FtbPackFile(
                ReadOptionalLong(fileElement, "id") ?? 0,
                name,
                collisionKey,
                primary,
                mirrors,
                size,
                ReadOptionalBoolean(fileElement, "clientonly"),
                ReadOptionalBoolean(fileElement, "serveronly"),
                ReadOptionalBoolean(fileElement, "optional"),
                RequireBoundedString(fileElement, "type", "FTB Pack file", 64),
                new FtbPackFileHashes(sha1, sha256, sha512));
            if (destinations.TryGetValue(collisionKey, out var existing))
            {
                if (!IsSafeCaseOnlyAlias(existing, file))
                {
                    throw new InvalidDataException(
                        $"FTB Pack manifest contains a conflicting destination: {collisionKey}");
                }

                // Windows resolves these case-only aliases to the same file. The full verified
                // content and install semantics match, so retain one deterministic entry.
                continue;
            }

            totalBytes = checked(totalBytes + size);
            if (totalBytes > MaximumManifestTotalBytes)
            {
                throw new InvalidDataException("FTB Pack manifest total size exceeds the safe limit.");
            }

            destinations.Add(collisionKey, file);
            files.Add(file);
        }

        return new FtbPackVersionManifest(
            responsePackId,
            responseVersionId,
            RequireBoundedString(root, "name", "FTB Pack manifest", 512),
            RequireBoundedString(root, "type", "FTB Pack manifest", 64),
            ReadOptionalBoolean(root, "private"),
            FtbTimestampNormalizer.NormalizeToUnixTimeMilliseconds(ReadOptionalLong(root, "updated")),
            targets,
            memory,
            files);
    }

    internal static bool IsSafeCaseOnlyAlias(FtbPackFile existing, FtbPackFile candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        return !existing.Path.Equals(candidate.Path, StringComparison.Ordinal) &&
               existing.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase) &&
               existing.Size == candidate.Size &&
               existing.ClientOnly == candidate.ClientOnly &&
               existing.ServerOnly == candidate.ServerOnly &&
               existing.Optional == candidate.Optional &&
               existing.Type.Equals(candidate.Type, StringComparison.OrdinalIgnoreCase) &&
               existing.Hashes.Sha1.Equals(candidate.Hashes.Sha1, StringComparison.OrdinalIgnoreCase) &&
               existing.Hashes.Sha256.Equals(candidate.Hashes.Sha256, StringComparison.OrdinalIgnoreCase) &&
               existing.Hashes.Sha512.Equals(candidate.Hashes.Sha512, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<FtbArtwork> ReadArtwork(JsonElement root)
    {
        if (!root.TryGetProperty("art", out var artElement)
            || artElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<FtbArtwork>();
        foreach (var element in artElement.EnumerateArray().Take(MaximumArtworkEntries))
        {
            var type = ReadOptionalString(element, "type")?.Trim().ToLowerInvariant();
            var width = element.TryGetProperty("width", out var widthElement)
                        && widthElement.TryGetInt32(out var parsedWidth)
                ? parsedWidth
                : 0;
            var height = element.TryGetProperty("height", out var heightElement)
                         && heightElement.TryGetInt32(out var parsedHeight)
                ? parsedHeight
                : 0;
            if (type is not ("square" or "splash" or "screenshot")
                || width is <= 0 or > 32768
                || height is <= 0 or > 32768)
            {
                continue;
            }

            var uris = new List<Uri>(MaximumArtworkMirrorsPerEntry + 1);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddSafeArtworkUri(ReadOptionalString(element, "url"), uris, seen);
            if (element.TryGetProperty("mirrors", out var mirrors)
                && mirrors.ValueKind == JsonValueKind.Array)
            {
                foreach (var mirror in mirrors.EnumerateArray().Take(MaximumArtworkMirrorsPerEntry))
                {
                    var mirrorValue = mirror.ValueKind switch
                    {
                        JsonValueKind.String => mirror.GetString(),
                        JsonValueKind.Object => ReadOptionalString(mirror, "url"),
                        _ => null
                    };
                    AddSafeArtworkUri(mirrorValue, uris, seen);
                }
            }

            if (uris.Count > 0)
            {
                result.Add(new FtbArtwork(
                    uris[0],
                    type,
                    width,
                    height,
                    uris.Skip(1).ToArray()));
            }
        }

        return result;
    }

    private static IReadOnlyList<FtbTarget> ReadTargets(JsonElement root, string context)
    {
        if (!root.TryGetProperty("targets", out var targetsElement) ||
            targetsElement.ValueKind != JsonValueKind.Array ||
            targetsElement.GetArrayLength() is 0 or > 32)
        {
            throw new InvalidDataException($"{context} targets are missing or invalid.");
        }

        var targets = new List<FtbTarget>(targetsElement.GetArrayLength());
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetElement in targetsElement.EnumerateArray())
        {
            var type = RequireBoundedString(targetElement, "type", context, 64);
            var name = RequireBoundedString(targetElement, "name", context, 128);
            var version = RequireBoundedString(targetElement, "version", context, 128);
            if (!identities.Add($"{type}\0{name}"))
            {
                throw new InvalidDataException($"{context} contains duplicate target '{type}/{name}'.");
            }

            targets.Add(new FtbTarget(type, name, version));
        }

        return targets;
    }

    private static FtbPackMemorySpecs ReadMemorySpecs(JsonElement root)
    {
        if (!root.TryGetProperty("specs", out var specs) || specs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("FTB Pack manifest memory specs are missing.");
        }

        var minimum = ReadRequiredInt(specs, "minimum", "FTB Pack specs");
        var recommended = ReadRequiredInt(specs, "recommended", "FTB Pack specs");
        if (minimum is < 512 or > 262_144 || recommended < minimum || recommended > 262_144)
        {
            throw new InvalidDataException("FTB Pack memory specs are outside the safe range.");
        }

        return new FtbPackMemorySpecs(minimum, recommended);
    }

    private static IReadOnlyList<Uri> ReadManifestMirrors(JsonElement fileElement)
    {
        if (!fileElement.TryGetProperty("mirrors", out var mirrorsElement))
        {
            return [];
        }

        if (mirrorsElement.ValueKind != JsonValueKind.Array ||
            mirrorsElement.GetArrayLength() > MaximumManifestMirrorsPerFile)
        {
            throw new InvalidDataException("FTB Pack file mirrors are invalid or excessive.");
        }

        var result = new List<Uri>(mirrorsElement.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mirror in mirrorsElement.EnumerateArray())
        {
            var value = mirror.ValueKind switch
            {
                JsonValueKind.String => mirror.GetString(),
                JsonValueKind.Object => ReadOptionalString(mirror, "url"),
                _ => null,
            };
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !IsOfficialFileUri(uri) ||
                !seen.Add(uri.AbsoluteUri))
            {
                continue;
            }

            result.Add(uri);
        }

        return result;
    }

    private static Uri ReadOfficialFileUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsOfficialFileUri(uri))
        {
            throw new InvalidDataException("FTB Pack file URL is not on an allowed official manifest CDN.");
        }

        return uri;
    }

    internal static bool IsOfficialFileUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        OfficialFileHosts.Contains(uri.IdnHost.TrimEnd('.'));

    internal static string NormalizeManifestDestination(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (path.Length > 1_024 || name.Length > 255 ||
            path.Contains('\\') || name.Contains('/') || name.Contains('\\') ||
            name is "." or ".." || HasUnsafeWindowsSegment(name))
        {
            throw new InvalidDataException("FTB Pack file path or name is unsafe.");
        }

        var normalizedPath = path.Normalize(NormalizationForm.FormC);
        var normalizedName = name.Normalize(NormalizationForm.FormC);
        while (normalizedPath.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[2..];
        }

        normalizedPath = normalizedPath.TrimEnd('/');
        if (normalizedPath.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(normalizedPath) ||
            normalizedPath.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException("FTB Pack file path is rooted or malformed.");
        }

        var segments = normalizedPath.Length == 0
            ? Array.Empty<string>()
            : normalizedPath.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment is "" or "." or ".." || HasUnsafeWindowsSegment(segment)))
        {
            throw new InvalidDataException("FTB Pack file path contains an unsafe segment.");
        }

        var destination = string.Join('/', segments.Append(normalizedName));
        if (destination.Length > 2_048)
        {
            throw new InvalidDataException("FTB Pack file destination is too long.");
        }

        return destination;
    }

    private static bool HasUnsafeWindowsSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.EndsWith(' ') || value.EndsWith('.') ||
            value.Any(character => char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
        {
            return true;
        }

        var stem = value.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static string ReadRequiredHash(JsonElement element, string property, int length)
    {
        var value = ReadRequiredString(element, property, "FTB Pack file hash").Trim();
        if (value.Length != length || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"FTB Pack file {property} is invalid.");
        }

        return value.ToLowerInvariant();
    }

    private static string RequireBoundedString(
        JsonElement element,
        string property,
        string context,
        int maximumLength)
    {
        var value = ReadRequiredString(element, property, context).Trim();
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"{context} field {property} is too long or unsafe.");
        }

        return value;
    }

    private static string? ReadOptionalBoundedString(
        JsonElement element,
        string property,
        string context,
        int maximumLength)
    {
        var value = ReadOptionalString(element, property)?.Trim();
        if (value is not null &&
            (value.Length > maximumLength || value.Any(char.IsControl)))
        {
            throw new InvalidDataException(
                string.Concat(context, " field ", property, " is too long or unsafe."));
        }

        return value;
    }

    private static void RejectDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"FTB Pack manifest contains duplicate JSON property '{property.Name}'.");
                }

                RejectDuplicateJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateJsonProperties(item);
            }
        }
    }

    private static void AddSafeArtworkUri(
        string? value,
        ICollection<Uri> destination,
        ISet<string> seen)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(uri.IdnHost, out _))
        {
            return;
        }

        var key = $"https://{uri.IdnHost.TrimEnd('.').ToLowerInvariant()}"
                  + uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        if (seen.Add(key))
        {
            destination.Add(uri);
        }
    }

    private async Task<FtbSearchResult> HydratePacksAsync(
        IReadOnlyList<int> packIds,
        CancellationToken cancellationToken)
    {
        if (packIds.Count == 0)
        {
            return new FtbSearchResult([]);
        }

        using var gate = new SemaphoreSlim(MaximumHydrationConcurrency);
        var tasks = packIds.Select(async packId =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await GetPackAsync(packId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        return new FtbSearchResult(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private async Task<JsonDocument> GetJsonAsync(
        string relativePath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(ApiRoot, relativePath);
        EnsureOfficialApiUri(requestUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureOfficialApiUri(response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("FTB API 回應缺少最終來源 URI。"));
        if (!response.IsSuccessStatusCode)
        {
            var details = await ReadBoundedTextAsync(response.Content, 64 * 1024, cancellationToken)
                .ConfigureAwait(false);
            throw new HttpRequestException(
                $"FTB API 錯誤：HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}");
        }

        if (response.Content.Headers.ContentLength is { } declaredLength
            && declaredLength > maximumBytes)
        {
            throw new InvalidDataException("FTB API 回應超過允許大小。");
        }

        var bytes = await ReadBoundedBytesAsync(response.Content, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("FTB API 回傳了無效 JSON。", exception);
        }
    }

    private static void EnsureOfficialApiUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IdnHost.TrimEnd('.').Equals(
                "api.feed-the-beast.com",
                StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.StartsWith(PublicApiPathPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"FTB API 重新導向到未核准來源：{uri}");
        }
    }

    private static void EnsureSuccessStatus(JsonElement root, string context)
    {
        if (!root.TryGetProperty("status", out var status)
            || !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            var message = ReadOptionalString(root, "message")
                ?? ReadOptionalString(root, "error")
                ?? "未知錯誤";
            throw new InvalidDataException($"{context}回應失敗：{message}");
        }
    }

    private static int ReadRequiredInt(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"{context}回應缺少整數欄位 {property}。");
        }

        return result;
    }

    private static long ReadRequiredLong(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException($"{context}回應缺少整數欄位 {property}。");
        }

        return result;
    }

    private static string ReadRequiredString(JsonElement element, string property, string context)
        => ReadOptionalString(element, property)
            ?? throw new InvalidDataException($"{context}回應缺少文字欄位 {property}。");

    private static string? ReadOptionalString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var result = value.GetString();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static long? ReadOptionalLong(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result)
            ? result
            : null;

    private static bool ReadOptionalBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"FTB API boolean field {property} is invalid."),
        };
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
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

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("FTB API 回應超過允許大小。");
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
}
