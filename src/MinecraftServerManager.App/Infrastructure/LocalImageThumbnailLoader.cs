using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace MinecraftServerManager.App.Infrastructure;

/// <summary>
/// Loads a bounded thumbnail from an existing local file without retaining a file handle.
/// Decoding runs on a worker thread, results are frozen for cross-thread WPF use, and the
/// in-memory LRU is bounded by both item count and decoded byte size.
/// </summary>
public sealed class LocalImageThumbnailLoader
{
    public const int DefaultDecodeWidth = 480;
    public const int DefaultDecodeHeight = 270;
    public const int MaximumDecodeDimension = 2048;
    public const long MaximumSourceBytes = 16L * 1024 * 1024;
    public const int MaximumSourceDimension = 16384;
    public const long MaximumSourcePixels = 64L * 1024 * 1024;
    public const int DefaultCacheEntryLimit = 64;
    public const long DefaultCacheByteLimit = 64L * 1024 * 1024;
    public const int MaximumConcurrentDecodes = 2;

    private readonly object _cacheSync = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache =
        new(CacheKeyComparer.Instance);
    private readonly LinkedList<CacheEntry> _leastRecentlyUsed = [];
    private readonly SemaphoreSlim _decodeGate =
        new(MaximumConcurrentDecodes, MaximumConcurrentDecodes);
    private readonly int _cacheEntryLimit;
    private readonly long _cacheByteLimit;
    private long _cachedBytes;

    public LocalImageThumbnailLoader(
        int cacheEntryLimit = DefaultCacheEntryLimit,
        long cacheByteLimit = DefaultCacheByteLimit)
    {
        if (cacheEntryLimit is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheEntryLimit),
                cacheEntryLimit,
                "Thumbnail cache entry limit must be between 1 and 256.");
        }

        if (cacheByteLimit is < 1024 * 1024 or > 512L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheByteLimit),
                cacheByteLimit,
                "Thumbnail cache byte limit must be between 1 MiB and 512 MiB.");
        }

        _cacheEntryLimit = cacheEntryLimit;
        _cacheByteLimit = cacheByteLimit;
    }

    /// <summary>
    /// Returns a frozen thumbnail or <see langword="null"/> for a missing, remote, oversized,
    /// unreadable, or unsupported image. A caller cancellation is propagated.
    /// </summary>
    public async Task<ImageSource?> LoadAsync(
        string? localPath,
        int decodePixelWidth = DefaultDecodeWidth,
        int decodePixelHeight = DefaultDecodeHeight,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = await Task.Run(
                () => ResolveLocalFile(localPath),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        var file = resolved.File;
        var stamp = resolved.Stamp;
        var width = Math.Clamp(decodePixelWidth, 1, MaximumDecodeDimension);
        var height = Math.Clamp(decodePixelHeight, 1, MaximumDecodeDimension);
        var key = new CacheKey(file.FullName, width, height);
        if (TryGetCached(key, stamp, out var cached))
        {
            return cached;
        }

        try
        {
            await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // A second request may have populated the cache while this one was queued.
                if (TryGetCached(key, stamp, out cached))
                {
                    return cached;
                }

                var decoded = await Task.Run(
                        () => Decode(file.FullName, width, height, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (decoded is null)
                {
                    return null;
                }

                var currentFile = new FileInfo(file.FullName);
                currentFile.Refresh();
                if (currentFile.Exists
                    && TryCreateStamp(currentFile, out var currentStamp)
                    && currentStamp == stamp)
                {
                    AddToCache(key, stamp, decoded);
                }

                return decoded;
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableImageFailure(exception))
        {
            return null;
        }
    }

    public void ClearCache()
    {
        lock (_cacheSync)
        {
            _cache.Clear();
            _leastRecentlyUsed.Clear();
            _cachedBytes = 0;
        }
    }

    internal int CachedItemCount
    {
        get
        {
            lock (_cacheSync)
            {
                return _cache.Count;
            }
        }
    }

    internal long CachedApproximateBytes
    {
        get
        {
            lock (_cacheSync)
            {
                return _cachedBytes;
            }
        }
    }

    private bool TryGetCached(
        CacheKey key,
        FileStamp stamp,
        out BitmapSource? image)
    {
        lock (_cacheSync)
        {
            if (!_cache.TryGetValue(key, out var node))
            {
                image = null;
                return false;
            }

            if (node.Value.Stamp != stamp)
            {
                RemoveNode(node);
                image = null;
                return false;
            }

            _leastRecentlyUsed.Remove(node);
            _leastRecentlyUsed.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    private void AddToCache(CacheKey key, FileStamp stamp, BitmapSource image)
    {
        var approximateBytes = Math.Max(
            1L,
            (long)image.PixelWidth
            * image.PixelHeight
            * Math.Max(1, (image.Format.BitsPerPixel + 7) / 8));
        if (approximateBytes > _cacheByteLimit)
        {
            return;
        }

        lock (_cacheSync)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                RemoveNode(existing);
            }

            // A changed file must not retain stale thumbnails at other requested sizes.
            var staleNodes = _leastRecentlyUsed
                .Where(entry => string.Equals(
                    entry.Key.Path,
                    key.Path,
                    StringComparison.OrdinalIgnoreCase) && entry.Stamp != stamp)
                .Select(entry => _cache[entry.Key])
                .ToArray();
            foreach (var staleNode in staleNodes)
            {
                RemoveNode(staleNode);
            }

            while (_leastRecentlyUsed.Last is not null
                   && (_cache.Count >= _cacheEntryLimit
                       || _cachedBytes + approximateBytes > _cacheByteLimit))
            {
                RemoveNode(_leastRecentlyUsed.Last);
            }

            var entry = new CacheEntry(key, stamp, image, approximateBytes);
            var node = _leastRecentlyUsed.AddFirst(entry);
            _cache.Add(key, node);
            _cachedBytes += approximateBytes;
        }
    }

    private void RemoveNode(LinkedListNode<CacheEntry> node)
    {
        _cache.Remove(node.Value.Key);
        _leastRecentlyUsed.Remove(node);
        _cachedBytes = Math.Max(0, _cachedBytes - node.Value.ApproximateBytes);
    }

    private static BitmapSource? Decode(
        string path,
        int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int sourceWidth;
        int sourceHeight;
        using (var metadataStream = OpenReadStream(path))
        using (var metadataCodec = SKCodec.Create(metadataStream))
        {
            if (metadataCodec is null
                || !HasSafeSourceDimensions(
                    metadataCodec.Info.Width,
                    metadataCodec.Info.Height))
            {
                return null;
            }

            sourceWidth = metadataCodec.Info.Width;
            sourceHeight = metadataCodec.Info.Height;
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                maximumWidth / (double)sourceWidth,
                maximumHeight / (double)sourceHeight));
        var targetWidth = Math.Clamp(
            (int)Math.Floor(sourceWidth * scale),
            1,
            maximumWidth);
        var targetHeight = Math.Clamp(
            (int)Math.Floor(sourceHeight * scale),
            1,
            maximumHeight);

        // Prefer WIC for native Windows formats. BitmapCacheOption.OnLoad guarantees that the
        // returned image no longer depends on the stream after EndInit.
        try
        {
            using var stream = OpenReadStream(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // IgnoreImageCache is URI-only and throws when StreamSource is used. OnLoad already
            // prevents WPF from retaining either this stream or a URI-backed global cache entry.
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (targetWidth < sourceWidth || targetHeight < sourceHeight)
            {
                if (targetWidth / (double)sourceWidth
                    <= targetHeight / (double)sourceHeight)
                {
                    image.DecodePixelWidth = targetWidth;
                }
                else
                {
                    image.DecodePixelHeight = targetHeight;
                }
            }

            image.StreamSource = stream;
            image.EndInit();
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasSafeDecodedDimensions(image, maximumWidth, maximumHeight))
            {
                return null;
            }

            image.Freeze();
            return image;
        }
        catch (Exception exception) when (IsRecoverableWindowsCodecFailure(exception))
        {
            // Windows Imaging Component does not support every format (notably WebP on some
            // machines). The bundled Skia codec provides a deterministic bounded fallback.
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var fallbackStream = OpenReadStream(path);
        using var fallbackCodec = SKCodec.Create(fallbackStream);
        if (fallbackCodec is null
            || fallbackCodec.Info.Width != sourceWidth
            || fallbackCodec.Info.Height != sourceHeight)
        {
            return null;
        }

        var targetInfo = new SKImageInfo(
            targetWidth,
            targetHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(fallbackCodec, targetInfo);
        cancellationToken.ThrowIfCancellationRequested();
        if (bitmap is null
            || bitmap.Width is <= 0 or > MaximumDecodeDimension
            || bitmap.Height is <= 0 or > MaximumDecodeDimension
            || bitmap.RowBytes <= 0
            || (long)bitmap.RowBytes * bitmap.Height > int.MaxValue)
        {
            return null;
        }

        var source = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            bitmap.GetPixels(),
            checked(bitmap.RowBytes * bitmap.Height),
            bitmap.RowBytes);
        source.Freeze();
        return source;
    }

    private static FileStream OpenReadStream(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);

    private static LocalFile? ResolveLocalFile(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var trimmed = candidate.Trim();
            if (!Path.IsPathFullyQualified(trimmed)
                || trimmed.StartsWith("\\\\", StringComparison.Ordinal)
                || trimmed.StartsWith("\\\\?\\", StringComparison.Ordinal)
                || trimmed.StartsWith("\\\\.\\", StringComparison.Ordinal))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(trimmed);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)
                || new DriveInfo(root).DriveType == DriveType.Network)
            {
                return null;
            }

            var file = new FileInfo(fullPath);
            file.Refresh();
            return file.Exists && TryCreateStamp(file, out var stamp)
                ? new LocalFile(file, stamp)
                : null;
        }
        catch (Exception exception) when (IsRecoverablePathFailure(exception))
        {
            return null;
        }
    }

    private static bool TryCreateStamp(FileInfo file, out FileStamp stamp)
    {
        stamp = default;
        if (file.Length is <= 0 or > MaximumSourceBytes)
        {
            return false;
        }

        stamp = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
        return true;
    }

    private static bool HasSafeSourceDimensions(int width, int height)
        => width is > 0 and <= MaximumSourceDimension
           && height is > 0 and <= MaximumSourceDimension
           && (long)width * height <= MaximumSourcePixels;

    private static bool HasSafeDecodedDimensions(
        BitmapSource image,
        int maximumWidth,
        int maximumHeight)
        => image.PixelWidth is > 0
           && image.PixelHeight is > 0
           && image.PixelWidth <= maximumWidth
           && image.PixelHeight <= maximumHeight
           && (long)image.PixelWidth * image.PixelHeight
           <= (long)maximumWidth * maximumHeight;

    private static bool IsRecoverableWindowsCodecFailure(Exception exception)
        => exception is NotSupportedException
            or ArgumentException
            or FormatException
            or COMException;

    private static bool IsRecoverablePathFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException;

    private static bool IsRecoverableImageFailure(Exception exception)
        => IsRecoverablePathFailure(exception)
           || exception is FormatException
               or COMException
               or DllNotFoundException
               or EntryPointNotFoundException
               or BadImageFormatException
               or TypeInitializationException
               or OverflowException;

    private readonly record struct CacheKey(string Path, int Width, int Height);

    private readonly record struct FileStamp(long Length, long LastWriteUtcTicks);

    private sealed record LocalFile(FileInfo File, FileStamp Stamp);

    private sealed record CacheEntry(
        CacheKey Key,
        FileStamp Stamp,
        BitmapSource Image,
        long ApproximateBytes);

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        public static CacheKeyComparer Instance { get; } = new();

        public bool Equals(CacheKey x, CacheKey y)
            => x.Width == y.Width
               && x.Height == y.Height
               && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(CacheKey obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
                obj.Width,
                obj.Height);
    }
}
