using System.Net;
using System.Security.Cryptography;

namespace MinecraftServerManager.Core.Providers;

public interface IModrinthModpackHttpTransport
{
    Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>
/// The supplied HttpClient must have automatic redirects disabled. This wrapper rejects a client
/// that followed one transparently so every hop remains visible to the URI policy.
/// </summary>
public sealed class HttpClientModrinthModpackHttpTransport : IModrinthModpackHttpTransport
{
    private readonly HttpClient _httpClient;

    public HttpClientModrinthModpackHttpTransport(HttpClient httpClient)
        => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var effectiveUri = response.RequestMessage?.RequestUri;
        if (effectiveUri is not null && effectiveUri != uri)
        {
            response.Dispose();
            throw new InvalidOperationException(
                "HttpClient 已自動跟隨重新導向；請使用 AllowAutoRedirect=false，讓每一跳都能接受安全檢查。");
        }

        return response;
    }
}

public interface IModrinthModpackUriPolicy
{
    void EnsureAllowed(Uri uri, bool isRedirect);
}

/// <summary>Strict host policy from the Modrinth pack format allow-list.</summary>
public sealed class OfficialModrinthModpackUriPolicy : IModrinthModpackUriPolicy
{
    private static readonly HashSet<string> SourceHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com",
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com"
    };
    private static readonly HashSet<string> GithubRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    public void EnsureAllowed(Uri uri, bool isRedirect)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !(SourceHosts.Contains(uri.IdnHost)
                 || isRedirect && GithubRedirectHosts.Contains(uri.IdnHost)))
        {
            var kind = isRedirect ? "重新導向" : "下載";
            throw new InvalidDataException($"Modrinth {kind}網址不在允許的 HTTPS 網域清單：{uri}");
        }
    }
}

public sealed class ModrinthModpackArtifactDownloader
{
    private const int MaximumErrorBytes = 64 * 1024;
    private readonly IModrinthModpackHttpTransport _transport;
    private readonly IModrinthModpackUriPolicy _uriPolicy;
    private readonly int _maxRedirects;

    public ModrinthModpackArtifactDownloader(
        IModrinthModpackHttpTransport transport,
        IModrinthModpackUriPolicy? uriPolicy = null,
        int maxRedirects = 5)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (maxRedirects is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(maxRedirects));
        _transport = transport;
        _uriPolicy = uriPolicy ?? new OfficialModrinthModpackUriPolicy();
        _maxRedirects = maxRedirects;
    }

    public IModrinthModpackUriPolicy UriPolicy => _uriPolicy;

    public async Task DownloadAsync(
        IReadOnlyList<Uri> mirrors,
        string destinationPath,
        long expectedSize,
        string expectedSha512,
        string? expectedSha1 = null,
        IProgress<long>? byteProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mirrors);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (mirrors.Count == 0) throw new ArgumentException("至少需要一個下載鏡像。", nameof(mirrors));
        if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));
        var expected512 = ParseHash(expectedSha512, 64, "SHA-512");
        var expected1 = expectedSha1 is null ? null : ParseHash(expectedSha1, 20, "SHA-1");

        var fullDestination = Path.GetFullPath(destinationPath);
        if (File.Exists(fullDestination) || Directory.Exists(fullDestination))
        {
            throw new IOException($"下載目的地已存在：{fullDestination}");
        }

        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException("下載目的地沒有父目錄。");
        Directory.CreateDirectory(parent);
        var partialPath = fullDestination + ".partial-" + Guid.NewGuid().ToString("N");
        var failures = new List<Exception>();

        foreach (var mirror in mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _uriPolicy.EnsureAllowed(mirror, isRedirect: false);
                await DownloadMirrorAsync(
                    mirror,
                    partialPath,
                    expectedSize,
                    expected512,
                    expected1,
                    byteProgress,
                    cancellationToken).ConfigureAwait(false);
                File.Move(partialPath, fullDestination, overwrite: false);
                return;
            }
            catch (OperationCanceledException)
            {
                TryDelete(partialPath);
                throw;
            }
            catch (Exception exception)
            {
                TryDelete(partialPath);
                failures.Add(new IOException($"鏡像下載失敗：{mirror}", exception));
            }
        }

        throw new IOException("所有 Modrinth 下載鏡像都失敗，沒有檔案被使用。", new AggregateException(failures));
    }

    private async Task DownloadMirrorAsync(
        Uri source,
        string partialPath,
        long expectedSize,
        byte[] expectedSha512,
        byte[]? expectedSha1,
        IProgress<long>? byteProgress,
        CancellationToken cancellationToken)
    {
        using var response = await FollowRedirectsAsync(source, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = await ReadBoundedErrorTextAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {details}",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedSize)
        {
            throw new InvalidDataException($"HTTP Content-Length 不符，預期 {expectedSize}，實際 {contentLength}。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        using var sha1 = expectedSha1 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedSize)
            {
                throw new InvalidDataException("下載內容超過 manifest/API 宣告的檔案大小。");
            }

            sha512.AppendData(buffer, 0, read);
            sha1?.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            byteProgress?.Report(total);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        if (total != expectedSize)
        {
            throw new InvalidDataException($"下載檔案大小不符，預期 {expectedSize}，實際 {total}。");
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSha512, sha512.GetHashAndReset()))
        {
            throw new InvalidDataException("下載檔案 SHA-512 驗證失敗。");
        }

        if (expectedSha1 is not null
            && !CryptographicOperations.FixedTimeEquals(expectedSha1, sha1!.GetHashAndReset()))
        {
            throw new InvalidDataException("下載檔案 SHA-1 驗證失敗。");
        }

        byteProgress?.Report(total);
    }

    private async Task<HttpResponseMessage> FollowRedirectsAsync(Uri source, CancellationToken cancellationToken)
    {
        var current = source;
        for (var redirects = 0; ; redirects++)
        {
            var response = await _transport.GetAsync(current, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode)) return response;

            if (redirects >= _maxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("Modrinth 下載重新導向次數過多。");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("重新導向回應缺少 Location。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            _uriPolicy.EnsureAllowed(current, isRedirect: true);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static byte[] ParseHash(string value, int expectedBytes, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            var bytes = Convert.FromHexString(value.Trim());
            if (bytes.Length != expectedBytes) throw new FormatException();
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{name} 雜湊格式無效。", exception);
        }
    }

    private static async Task<string> ReadBoundedErrorTextAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > MaximumErrorBytes)
        {
            return "Error response exceeded the safe display limit.";
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumErrorBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the original validation/download error.
        }
    }
}
