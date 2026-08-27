using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Performs user-initiated FTB catalogue queries. Search responses contain only pack IDs, so each
/// result is hydrated through the official pack endpoint before it is returned.
/// </summary>
public sealed class FtbCatalogProvider
{
    private static readonly Uri ApiRoot = new("https://api.feed-the-beast.com/v1/modpacks/modpack/");
    private const int MaximumHydrationConcurrency = 4;
    private const int MaximumArtworkEntries = 100;
    private const int MaximumArtworkMirrorsPerEntry = 8;
    private const long MaximumSearchBytes = 1L * 1024 * 1024;
    private const long MaximumFeaturedBytes = 1L * 1024 * 1024;
    private const long MaximumPackBytes = 8L * 1024 * 1024;

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

        var name = ReadRequiredString(root, "name", "FTB Pack");
        var slug = ReadOptionalString(root, "slug") ?? string.Empty;
        var isPrivate = root.TryGetProperty("private", out var privateElement)
            && privateElement.ValueKind is JsonValueKind.True;
        var synopsis = ReadOptionalString(root, "synopsis")
                       ?? ReadOptionalString(root, "description");
        var installCount = ReadOptionalLong(root, "installs");
        var artwork = ReadArtwork(root);
        var versions = new List<FtbPackVersion>();

        if (root.TryGetProperty("versions", out var versionsElement)
            && versionsElement.ValueKind == JsonValueKind.Array)
        {
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
                    foreach (var targetElement in targetsElement.EnumerateArray())
                    {
                        var type = ReadOptionalString(targetElement, "type");
                        var targetName = ReadOptionalString(targetElement, "name");
                        var targetVersion = ReadOptionalString(targetElement, "version");
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
                    ReadRequiredString(versionElement, "name", "FTB Pack version"),
                    ReadRequiredString(versionElement, "type", "FTB Pack version"),
                    FtbTimestampNormalizer.NormalizeToUnixTimeMilliseconds(
                        ReadOptionalLong(versionElement, "updated")),
                    targets));
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
            || !uri.Host.Equals("api.feed-the-beast.com", StringComparison.OrdinalIgnoreCase))
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
