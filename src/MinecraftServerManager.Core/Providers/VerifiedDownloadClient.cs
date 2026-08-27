using System.Net;
using System.Security.Cryptography;

namespace MinecraftServerManager.Core.Providers;

public sealed class VerifiedDownloadClient(HttpClient httpClient)
{
    private const long MaximumUnspecifiedDownloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;

    public async Task DownloadAsync(
        Uri source,
        string partialPath,
        HashAlgorithmName hashAlgorithm,
        string expectedHashHex,
        long? expectedSize = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHashHex);
        if (expectedSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHashHex.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("來源提供了無效的雜湊值。", exception);
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(partialPath))
            ?? throw new InvalidOperationException("下載暫存路徑沒有父目錄。");
        Directory.CreateDirectory(parent);

        if (File.Exists(partialPath))
        {
            throw new IOException($"下載暫存檔已存在：{partialPath}");
        }

        try
        {
            using var response = await SendWithRetryAsync(source, cancellationToken).ConfigureAwait(false);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hash = IncrementalHash.CreateHash(hashAlgorithm);
            var buffer = new byte[128 * 1024];
            long total = 0;
            var reportedLength = expectedSize ?? response.Content.Headers.ContentLength;
            var maximumBytes = expectedSize
                ?? response.Content.Headers.ContentLength
                ?? throw new InvalidDataException("下載來源未提供可驗證的檔案大小。");
            if (maximumBytes is < 1 or > MaximumUnspecifiedDownloadBytes)
            {
                throw new InvalidDataException("下載檔案大小超過安全上限。");
            }

            if (response.Content.Headers.ContentLength is { } declaredLength
                && declaredLength > maximumBytes)
            {
                throw new InvalidDataException("下載 Content-Length 超過預期檔案大小。");
            }

            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                total = checked(total + count);
                if (total > maximumBytes)
                {
                    throw new InvalidDataException("下載內容超過預期檔案大小。");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);

                if (reportedLength is > 0)
                {
                    progress?.Report(Math.Clamp((double)total / reportedLength.Value, 0d, 1d));
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);

            if (expectedSize is { } size && total != size)
            {
                throw new InvalidDataException($"檔案大小不符，預期 {size} bytes，實際 {total} bytes。");
            }

            var actual = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException("下載檔案的雜湊驗證失敗，檔案不會被使用。");
            }

            progress?.Report(1d);
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Uri source, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var retryable = response.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

            if (!retryable || attempt >= 2)
            {
                var body = await ReadBoundedErrorTextAsync(response.Content, cancellationToken)
                    .ConfigureAwait(false);
                response.Dispose();
                throw new HttpRequestException($"下載失敗：HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }

            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt));
            retryAfter = TimeSpan.FromMilliseconds(Math.Clamp(
                retryAfter.TotalMilliseconds,
                0,
                TimeSpan.FromSeconds(30).TotalMilliseconds));
            response.Dispose();
            await Task.Delay(retryAfter, cancellationToken).ConfigureAwait(false);
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

    private static void TryDeleteFile(string path)
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
            // The original failure is more useful to the caller than cleanup failure.
        }
    }
}
