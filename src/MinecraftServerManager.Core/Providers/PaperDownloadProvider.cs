using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MinecraftServerManager.Core.Providers;

public sealed record PaperBuildInfo(
    string MinecraftVersion,
    int BuildId,
    string Channel,
    string FileName,
    Uri DownloadUri,
    string Sha256,
    long Size);

public sealed class PaperDownloadProvider
{
    private static readonly Uri BaseUri = new("https://fill.papermc.io/");
    private readonly HttpClient _httpClient;
    private readonly VerifiedDownloadClient _downloadClient;

    public PaperDownloadProvider(HttpClient httpClient, string userAgent)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= BaseUri;
        // Large server artifacts may take longer than metadata requests. Callers own cancellation.
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _downloadClient = new VerifiedDownloadClient(_httpClient);
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("v3/projects/paper", cancellationToken).ConfigureAwait(false);
        await EnsurePaperSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = new List<string>();
        if (!document.RootElement.TryGetProperty("versions", out var groups))
        {
            return result;
        }

        foreach (var group in groups.EnumerateObject())
        {
            foreach (var version in group.Value.EnumerateArray())
            {
                var value = version.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<PaperBuildInfo?> GetLatestStableBuildAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        var request = $"v3/projects/paper/versions/{Uri.EscapeDataString(minecraftVersion)}/builds?channel=STABLE";
        using var response = await _httpClient.GetAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsurePaperSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        PaperBuildInfo? best = null;
        foreach (var build in document.RootElement.EnumerateArray())
        {
            var channel = build.GetProperty("channel").GetString() ?? string.Empty;
            if (!channel.Equals("STABLE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var buildId = build.GetProperty("id").GetInt32();
            if (!build.TryGetProperty("downloads", out var downloads)
                || !downloads.TryGetProperty("server:default", out var artifact))
            {
                continue;
            }

            var candidate = new PaperBuildInfo(
                minecraftVersion,
                buildId,
                channel,
                artifact.GetProperty("name").GetString() ?? $"paper-{minecraftVersion}-{buildId}.jar",
                new Uri(artifact.GetProperty("url").GetString() ?? throw new InvalidDataException("Paper 回應缺少下載 URL。")),
                artifact.GetProperty("checksums").GetProperty("sha256").GetString()
                    ?? throw new InvalidDataException("Paper 回應缺少 SHA-256。"),
                artifact.GetProperty("size").GetInt64());

            if (best is null || candidate.BuildId > best.BuildId)
            {
                best = candidate;
            }
        }

        return best;
    }

    public async Task<PaperBuildInfo> DownloadLatestStableAsync(
        string minecraftVersion,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var build = await GetLatestStableBuildAsync(minecraftVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Paper {minecraftVersion} 目前沒有 Stable build。");

        var fullDestination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException("核心目的路徑沒有父目錄。");
        Directory.CreateDirectory(parent);
        if (File.Exists(fullDestination))
        {
            throw new IOException($"目的核心已存在，為避免覆寫已取消：{fullDestination}");
        }

        var partial = fullDestination + $".{Guid.NewGuid():N}.partial";
        await _downloadClient.DownloadAsync(
            build.DownloadUri,
            partial,
            HashAlgorithmName.SHA256,
            build.Sha256,
            build.Size,
            progress,
            cancellationToken).ConfigureAwait(false);

        File.Move(partial, fullDestination, overwrite: false);
        return build;
    }

    private static async Task EnsurePaperSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.ReasonPhrase ?? "未知錯誤";
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var detail))
            {
                message = detail.GetString() ?? message;
            }
        }
        catch
        {
            // Preserve the HTTP status when the error body is not JSON.
        }

        throw new HttpRequestException($"Paper API 錯誤：HTTP {(int)response.StatusCode}，{message}");
    }
}
