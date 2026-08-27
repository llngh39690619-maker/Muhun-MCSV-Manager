using System.Net;
using System.Security.Cryptography;

namespace MinecraftServerManager.Core.Providers;

internal sealed record FirstPartyDownloadHeaders(
    Uri ResponseUri,
    long Size,
    string FileName);

/// <summary>
/// Small fail-closed HTTP primitive shared by the hybrid catalog and BuildTools provider. The
/// supplied clients must have automatic redirects disabled; every response is checked so a
/// misconfigured client fails instead of silently bypassing the per-hop host policy.
/// </summary>
internal static class FirstPartyArtifactHttp
{
    internal const long MaximumJsonBytes = 8L * 1024 * 1024;
    internal const long MaximumArtifactBytes = 512L * 1024 * 1024;
    private const int MaximumRedirects = 3;

    public static async Task<byte[]> GetBoundedBytesAsync(
        HttpClient client,
        Uri source,
        long maximumBytes,
        Func<Uri, bool> uriPolicy,
        string context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        EnsureAllowed(source, uriPolicy, context);
        if (maximumBytes is < 1 or > MaximumJsonBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureNoAutomaticRedirect(source, response, context);
        if (IsRedirect(response.StatusCode))
        {
            throw new InvalidDataException($"{context} 不允許重新導向。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{context} 失敗：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > maximumBytes)
        {
            throw new InvalidDataException($"{context} 回應超過 {maximumBytes:N0} bytes 上限。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream(
            response.Content.Headers.ContentLength is > 0 and <= int.MaxValue
                ? (int)response.Content.Headers.ContentLength.Value
                : 0);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException($"{context} 回應超過 {maximumBytes:N0} bytes 上限。");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    public static async Task<FirstPartyDownloadHeaders> ProbeDownloadAsync(
        HttpClient client,
        Uri source,
        Func<Uri, bool> uriPolicy,
        Func<Uri, Uri, bool> redirectPolicy,
        long maximumBytes,
        string? expectedFileName,
        string context,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingAllowedRedirectsAsync(
                client,
                source,
                uriPolicy,
                redirectPolicy,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var size = RequireExactDownloadHeaders(response, maximumBytes, expectedFileName, context);
        return new FirstPartyDownloadHeaders(
            response.RequestMessage!.RequestUri!,
            size,
            expectedFileName ?? string.Empty);
    }

    public static async Task<string> DownloadVerifiedSha256Async(
        HttpClient client,
        Uri source,
        string destinationPath,
        string expectedSha256,
        long expectedSize,
        Func<Uri, bool> uriPolicy,
        Func<Uri, Uri, bool> redirectPolicy,
        string? expectedFileName,
        string context,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateSha256(expectedSha256, context);
        if (expectedSize is < 1 or > MaximumArtifactBytes)
        {
            throw new InvalidDataException($"{context} 檔案大小不在安全範圍內。");
        }

        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("下載目的路徑沒有父目錄。");
        Directory.CreateDirectory(parent);
        if (File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("下載目的資料夾不得是符號連結或 junction。");
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"目的檔案已存在，為避免覆寫已取消：{destination}");
        }

        var partial = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            using var response = await SendFollowingAllowedRedirectsAsync(
                    client,
                    source,
                    uriPolicy,
                    redirectPolicy,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            var declaredSize = RequireExactDownloadHeaders(
                response,
                expectedSize,
                expectedFileName,
                context);
            if (declaredSize != expectedSize)
            {
                throw new InvalidDataException(
                    $"{context} Content-Length 與 catalog 的 asset size 不符。");
            }

            long total = 0;
            byte[] actual;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false))
            await using (var output = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total = checked(total + read);
                    if (total > expectedSize)
                    {
                        throw new InvalidDataException($"{context} 內容超過 catalog 的 asset size。");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    progress?.Report(Math.Clamp((double)total / expectedSize, 0d, 1d));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                actual = hash.GetHashAndReset();
            }

            if (total != expectedSize)
            {
                throw new InvalidDataException(
                    $"{context} 大小不符，預期 {expectedSize} bytes，實際 {total} bytes。");
            }

            var expected = Convert.FromHexString(expectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException($"{context} SHA-256 驗證失敗。");
            }

            File.Move(partial, destination, overwrite: false);
            progress?.Report(1d);
            return destination;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendFollowingAllowedRedirectsAsync(
        HttpClient client,
        Uri source,
        Func<Uri, bool> uriPolicy,
        Func<Uri, Uri, bool> redirectPolicy,
        string context,
        CancellationToken cancellationToken)
    {
        EnsureAllowed(source, uriPolicy, context);
        var current = source;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                EnsureNoAutomaticRedirect(current, response, context);
                if (!IsRedirect(response.StatusCode))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"{context} 失敗：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。",
                            null,
                            response.StatusCode);
                    }

                    return response;
                }

                if (redirectCount >= MaximumRedirects)
                {
                    throw new InvalidDataException($"{context} 重新導向次數超過安全上限。");
                }

                var location = response.Headers.Location
                    ?? throw new InvalidDataException($"{context} 重新導向缺少 Location。");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                EnsureAllowed(next, uriPolicy, context);
                if (!redirectPolicy(current, next))
                {
                    throw new InvalidDataException(
                        $"{context} 拒絕重新導向至未授權來源：{next.Host}。");
                }

                current = next;
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
        }
    }

    private static long RequireExactDownloadHeaders(
        HttpResponseMessage response,
        long maximumBytes,
        string? expectedFileName,
        string context)
    {
        var length = response.Content.Headers.ContentLength
            ?? throw new InvalidDataException($"{context} 未提供 Content-Length。");
        if (length is < 1 || length > maximumBytes || length > MaximumArtifactBytes)
        {
            throw new InvalidDataException($"{context} Content-Length 不在安全範圍內。");
        }

        if (expectedFileName is not null)
        {
            var disposition = response.Content.Headers.ContentDisposition;
            var actualName = disposition?.FileNameStar ?? disposition?.FileName;
            actualName = actualName?.Trim().Trim('"');
            if (!string.Equals(actualName, expectedFileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{context} Content-Disposition 檔名不符。");
            }
        }

        return length;
    }

    private static void EnsureNoAutomaticRedirect(
        Uri requested,
        HttpResponseMessage response,
        string context)
    {
        var responseUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException($"{context} 回應缺少 request URI。");
        if (!UriEquals(requested, responseUri))
        {
            throw new InvalidDataException(
                $"{context} 的 HttpClient 啟用了自動重新導向；此安全契約要求停用。");
        }
    }

    private static void EnsureAllowed(Uri uri, Func<Uri, bool> policy, string context)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !policy(uri))
        {
            throw new InvalidDataException($"{context} URI 不在官方來源 allowlist：{uri}。");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool UriEquals(Uri left, Uri right) =>
        left.AbsoluteUri.Equals(right.AbsoluteUri, StringComparison.Ordinal);

    private static void ValidateSha256(string value, string context)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{context} SHA-256 格式無效。");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original download/verification exception.
        }
    }
}
