using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Stores catalogue artwork as a bounded, validated local file. Callers should bind only the
/// returned local path to WPF; this service performs no image decoding and never requires the UI
/// thread.
/// </summary>
public interface IOnlineModpackArtworkCache
{
    Task<string?> GetOrCacheAsync(
        OnlineModpackProvider provider,
        Uri? remoteUri,
        CancellationToken cancellationToken = default);
}

public interface IOnlineModpackArtworkUriPolicy
{
    bool IsAllowed(OnlineModpackProvider provider, Uri uri);
}

/// <summary>Exact-host policy for artwork returned by each official catalogue.</summary>
public sealed class OnlineModpackArtworkUriPolicy : IOnlineModpackArtworkUriPolicy
{
    private static readonly IReadOnlyDictionary<OnlineModpackProvider, IReadOnlyCollection<string>>
        DefaultHosts = new Dictionary<OnlineModpackProvider, IReadOnlyCollection<string>>
        {
            [OnlineModpackProvider.Modrinth] = ["cdn.modrinth.com"],
            [OnlineModpackProvider.CurseForge] =
                ["media.forgecdn.net", "mediafilez.forgecdn.net"],
            [OnlineModpackProvider.Ftb] = ["cdn.feed-the-beast.com"]
        };

    private readonly IReadOnlyDictionary<OnlineModpackProvider, IReadOnlySet<string>> _hosts;

    public OnlineModpackArtworkUriPolicy()
        : this(DefaultHosts)
    {
    }

    public OnlineModpackArtworkUriPolicy(
        IReadOnlyDictionary<OnlineModpackProvider, IReadOnlyCollection<string>> hostsByProvider)
    {
        ArgumentNullException.ThrowIfNull(hostsByProvider);
        var normalized = new Dictionary<OnlineModpackProvider, IReadOnlySet<string>>();
        foreach (var provider in Enum.GetValues<OnlineModpackProvider>())
        {
            if (!hostsByProvider.TryGetValue(provider, out var configured) || configured.Count == 0)
            {
                normalized[provider] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in configured)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(host);
                var trimmed = host.Trim().TrimEnd('.');
                if (trimmed.Contains('/', StringComparison.Ordinal)
                    || trimmed.Contains(':', StringComparison.Ordinal)
                    || Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
                {
                    throw new ArgumentException($"圖片 allowlist host 格式無效：{host}", nameof(hostsByProvider));
                }

                hosts.Add(new IdnMapping().GetAscii(trimmed));
            }

            normalized[provider] = hosts;
        }

        _hosts = normalized;
    }

    public bool IsAllowed(OnlineModpackProvider provider, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Enum.IsDefined(provider)
               && uri.IsAbsoluteUri
               && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
               && _hosts.TryGetValue(provider, out var hosts)
               && hosts.Contains(uri.IdnHost.TrimEnd('.'));
    }
}

public sealed class OnlineModpackArtworkCache : IOnlineModpackArtworkCache, IDisposable
{
    public const long MaximumImageBytes = 5L * 1024 * 1024;
    public const int MaximumConcurrentDownloads = 3;
    public const int MaximumImageDimension = 8192;
    public const long MaximumImagePixels = 32L * 1024 * 1024;

    private static readonly IReadOnlySet<string> CacheExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".webp", ".gif" };

    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly IOnlineModpackArtworkUriPolicy _uriPolicy;
    private readonly SemaphoreSlim _downloadGate =
        new(MaximumConcurrentDownloads, MaximumConcurrentDownloads);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>Creates a production client whose redirect handling is deliberately disabled.</summary>
    public OnlineModpackArtworkCache(
        ApplicationPaths paths,
        IOnlineModpackArtworkUriPolicy? uriPolicy = null)
        : this(paths, CreateSecureHttpClient(), uriPolicy, ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Creates a cache over a caller-owned client. The service detects and rejects a client that
    /// followed a redirect, but production callers should still configure AllowAutoRedirect=false.
    /// </summary>
    public OnlineModpackArtworkCache(
        ApplicationPaths paths,
        HttpClient httpClient,
        IOnlineModpackArtworkUriPolicy? uriPolicy = null)
        : this(paths, httpClient, uriPolicy, ownsHttpClient: false)
    {
    }

    private OnlineModpackArtworkCache(
        ApplicationPaths paths,
        HttpClient httpClient,
        IOnlineModpackArtworkUriPolicy? uriPolicy,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _uriPolicy = uriPolicy ?? new OnlineModpackArtworkUriPolicy();
        _cacheDirectory = Path.GetFullPath(paths.OnlineModpackArtworkCache);
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<string?> GetOrCacheAsync(
        OnlineModpackProvider provider,
        Uri? remoteUri,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (remoteUri is null || !_uriPolicy.IsAllowed(provider, remoteUri))
        {
            return null;
        }

        var cacheKey = CreateCacheKey(provider, remoteUri);
        var operation = _inflight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<string?>>(
                () => GetOrDownloadWithRetryAsync(
                    provider,
                    remoteUri,
                    cacheKey,
                    _lifetimeCancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var task = operation.Value;
        _ = task.ContinueWith(
            (_, state) =>
            {
                var cleanup = (InflightCleanup)state!;
                cleanup.Cache._inflight.TryRemove(
                    new KeyValuePair<string, Lazy<Task<string?>>>(cleanup.CacheKey, cleanup.Operation));
            },
            new InflightCleanup(this, cacheKey, operation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _lifetimeCancellation.Dispose();
    }

    private async Task<string?> GetOrDownloadWithRetryAsync(
        OnlineModpackProvider provider,
        Uri remoteUri,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            DownloadOutcome outcome;
            await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Cache validation is disk I/O and stays under the same small gate as downloads;
                // catalogue opening therefore cannot launch an unbounded validation burst.
                var cached = await Task.Run(
                        () => TryFindValidCachedFile(cacheKey),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cached is not null)
                {
                    return cached;
                }

                outcome = await DownloadOnceAsync(
                        provider,
                        remoteUri,
                        cacheKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                outcome = DownloadOutcome.Transient();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 0)
            {
                outcome = DownloadOutcome.Transient();
            }
            finally
            {
                _downloadGate.Release();
            }

            if (outcome.Path is not null || !outcome.IsTransient || attempt > 0)
            {
                return outcome.Path;
            }

            // Backoff intentionally occurs after releasing the global three-download gate.
            await Task.Delay(GetRetryDelay(outcome.RetryAfter), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<DownloadOutcome> DownloadOnceAsync(
        OnlineModpackProvider provider,
        Uri remoteUri,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        EnsureCacheDirectory();
        using var request = new HttpRequestMessage(HttpMethod.Get, remoteUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/gif"));

        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var effectiveUri = response.RequestMessage?.RequestUri;
        if (effectiveUri is null
            || !Uri.Equals(remoteUri, effectiveUri)
            || !_uriPolicy.IsAllowed(provider, effectiveUri)
            || IsRedirect(response.StatusCode))
        {
            return DownloadOutcome.Permanent();
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var hasDeclaredFormat = TryGetExpectedFormat(mediaType, out _);
        // The official FTB CDN currently serves part of its otherwise valid PNG/WebP catalogue
        // artwork as application/octet-stream. Accept that provider-specific quirk only after the
        // exact-host URI policy has passed, then require supported magic bytes and safe dimensions
        // below. Other providers and arbitrary binary responses remain rejected.
        var allowsFtbBinaryContentType = provider == OnlineModpackProvider.Ftb
                                         && mediaType?.Equals(
                                             "application/octet-stream",
                                             StringComparison.OrdinalIgnoreCase) == true;
        if (!response.IsSuccessStatusCode)
        {
            return IsTransientStatus(response.StatusCode)
                ? DownloadOutcome.Transient(GetRetryAfter(response))
                : DownloadOutcome.Permanent();
        }

        if ((!hasDeclaredFormat && !allowsFtbBinaryContentType)
            || response.Content.Headers.ContentLength is <= 0 or > MaximumImageBytes)
        {
            return DownloadOutcome.Permanent();
        }

        var temporaryPath = Path.Combine(_cacheDirectory, $".{cacheKey}.{Guid.NewGuid():N}.tmp");
        try
        {
            var header = new byte[32];
            var headerLength = 0;
            long total = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total = checked(total + read);
                    if (total > MaximumImageBytes)
                    {
                        return DownloadOutcome.Permanent();
                    }

                    if (headerLength < header.Length)
                    {
                        var copy = Math.Min(header.Length - headerLength, read);
                        buffer.AsSpan(0, copy).CopyTo(header.AsSpan(headerLength));
                        headerLength += copy;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            if (total <= 0
                || response.Content.Headers.ContentLength is { } declared && declared != total
                || !TryDetectFormat(header.AsSpan(0, headerLength), out var actualFormat)
                || !HasSafeDimensions(temporaryPath, actualFormat))
            {
                return DownloadOutcome.Permanent();
            }

            var destination = Path.Combine(_cacheDirectory, cacheKey + GetExtension(actualFormat));
            try
            {
                File.Move(temporaryPath, destination, overwrite: false);
            }
            catch (IOException) when (IsValidCachedFile(destination))
            {
                return DownloadOutcome.Success(destination);
            }

            return DownloadOutcome.Success(destination);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int)statusCode is >= 500 and <= 599;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        return null;
    }

    private static TimeSpan GetRetryDelay(TimeSpan? requested)
    {
        if (requested is { } retryAfter)
        {
            return TimeSpan.FromMilliseconds(Math.Clamp(retryAfter.TotalMilliseconds, 100, 2_000));
        }

        return TimeSpan.FromMilliseconds(250 + Random.Shared.Next(0, 251));
    }

    private string? TryFindValidCachedFile(string cacheKey)
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return null;
        }

        try
        {
            if (File.GetAttributes(_cacheDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            foreach (var extension in CacheExtensions)
            {
                var candidate = Path.Combine(_cacheDirectory, cacheKey + extension);
                if (IsValidCachedFile(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return null;
        }

        return null;
    }

    private static bool IsValidCachedFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || file.Length is <= 0 or > MaximumImageBytes)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[16];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = stream.Read(header);
            return TryDetectFormat(header[..read], out var format)
                   && Path.GetExtension(path).Equals(GetExtension(format), StringComparison.OrdinalIgnoreCase)
                   && HasSafeDimensions(path, format);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private void EnsureCacheDirectory()
    {
        Directory.CreateDirectory(_cacheDirectory);
        if (File.GetAttributes(_cacheDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("線上模組包圖片快取不得是符號連結或 junction。");
        }
    }

    private static string CreateCacheKey(OnlineModpackProvider provider, Uri uri)
    {
        var material = Encoding.UTF8.GetBytes($"{provider}\n{uri.AbsoluteUri}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    private static bool TryGetExpectedFormat(string? mediaType, out ArtworkFormat format)
    {
        format = mediaType?.Trim().ToLowerInvariant() switch
        {
            "image/png" => ArtworkFormat.Png,
            "image/jpeg" => ArtworkFormat.Jpeg,
            "image/webp" => ArtworkFormat.WebP,
            "image/gif" => ArtworkFormat.Gif,
            _ => ArtworkFormat.Unknown
        };
        return format != ArtworkFormat.Unknown;
    }

    internal static bool TryDetectFormat(ReadOnlySpan<byte> header, out ArtworkFormat format)
    {
        if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            format = ArtworkFormat.Png;
            return true;
        }

        if (header.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            format = ArtworkFormat.Jpeg;
            return true;
        }

        if (header.Length >= 12
            && header[..4].SequenceEqual("RIFF"u8)
            && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            format = ArtworkFormat.WebP;
            return true;
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            format = ArtworkFormat.Gif;
            return true;
        }

        format = ArtworkFormat.Unknown;
        return false;
    }

    private static bool HasSafeDimensions(string path, ArtworkFormat format)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadDimensions(stream, format, out var width, out var height)
                   && width is > 0 and <= MaximumImageDimension
                   && height is > 0 and <= MaximumImageDimension
                   && (long)width * height <= MaximumImagePixels;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private static bool TryReadDimensions(
        Stream stream,
        ArtworkFormat format,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        Span<byte> header = stackalloc byte[32];
        var read = stream.Read(header);
        var bytes = header[..read];
        switch (format)
        {
            case ArtworkFormat.Png when read >= 24
                                           && bytes.Slice(12, 4).SequenceEqual("IHDR"u8):
                width = ReadBigEndian32(bytes.Slice(16, 4));
                height = ReadBigEndian32(bytes.Slice(20, 4));
                return true;
            case ArtworkFormat.Gif when read >= 10:
                width = bytes[6] | bytes[7] << 8;
                height = bytes[8] | bytes[9] << 8;
                return true;
            case ArtworkFormat.WebP when read >= 30:
                if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
                {
                    width = 1 + ReadLittleEndian24(bytes.Slice(24, 3));
                    height = 1 + ReadLittleEndian24(bytes.Slice(27, 3));
                    return true;
                }

                if (bytes.Slice(12, 4).SequenceEqual("VP8 "u8)
                    && bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                {
                    width = (bytes[26] | bytes[27] << 8) & 0x3fff;
                    height = (bytes[28] | bytes[29] << 8) & 0x3fff;
                    return true;
                }

                if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && bytes[20] == 0x2f)
                {
                    var dimensions = bytes[21]
                                     | bytes[22] << 8
                                     | bytes[23] << 16
                                     | bytes[24] << 24;
                    width = 1 + (dimensions & 0x3fff);
                    height = 1 + (dimensions >> 14 & 0x3fff);
                    return true;
                }

                return false;
            case ArtworkFormat.Jpeg:
                stream.Position = 2;
                return TryReadJpegDimensions(stream, out width, out height);
            default:
                return false;
        }
    }

    private static bool TryReadJpegDimensions(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        while (stream.Position < stream.Length)
        {
            var prefix = stream.ReadByte();
            if (prefix != 0xff)
            {
                continue;
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
            } while (marker == 0xff);
            if (marker < 0 || marker is 0xd9 or 0xda)
            {
                return false;
            }

            if (marker is 0xd8 or >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            var lengthHigh = stream.ReadByte();
            var lengthLow = stream.ReadByte();
            if (lengthHigh < 0 || lengthLow < 0)
            {
                return false;
            }

            var segmentLength = (lengthHigh << 8) | lengthLow;
            if (segmentLength < 2 || segmentLength - 2 > stream.Length - stream.Position)
            {
                return false;
            }

            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7
                or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (segmentLength < 7 || stream.ReadByte() < 0)
                {
                    return false;
                }

                var heightHigh = stream.ReadByte();
                var heightLow = stream.ReadByte();
                var widthHigh = stream.ReadByte();
                var widthLow = stream.ReadByte();
                if (heightHigh < 0 || heightLow < 0 || widthHigh < 0 || widthLow < 0)
                {
                    return false;
                }

                height = heightHigh << 8 | heightLow;
                width = widthHigh << 8 | widthLow;
                return true;
            }

            stream.Position += segmentLength - 2;
        }

        return false;
    }

    private static int ReadBigEndian32(ReadOnlySpan<byte> bytes)
        => bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3];

    private static int ReadLittleEndian24(ReadOnlySpan<byte> bytes)
        => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    private static string GetExtension(ArtworkFormat format) => format switch
    {
        ArtworkFormat.Png => ".png",
        ArtworkFormat.Jpeg => ".jpg",
        ArtworkFormat.WebP => ".webp",
        ArtworkFormat.Gif => ".gif",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or OverflowException
            or ObjectDisposedException
            or OperationCanceledException;

    private static HttpClient CreateSecureHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
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
            // A failed best-effort cleanup must not turn optional artwork into an install failure.
        }
    }

    internal enum ArtworkFormat
    {
        Unknown,
        Png,
        Jpeg,
        WebP,
        Gif
    }

    private readonly record struct DownloadOutcome(
        string? Path,
        bool IsTransient,
        TimeSpan? RetryAfter)
    {
        public static DownloadOutcome Success(string path) => new(path, false, null);

        public static DownloadOutcome Permanent() => new(null, false, null);

        public static DownloadOutcome Transient(TimeSpan? retryAfter = null)
            => new(null, true, retryAfter);
    }

    private sealed record InflightCleanup(
        OnlineModpackArtworkCache Cache,
        string CacheKey,
        Lazy<Task<string?>> Operation);
}
