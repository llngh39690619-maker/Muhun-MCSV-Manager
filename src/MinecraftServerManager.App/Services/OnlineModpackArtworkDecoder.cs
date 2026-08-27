using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace MinecraftServerManager.App.Services;

public interface IOnlineModpackArtworkDecoder
{
    Task<ImageSource?> DecodePreviewAsync(
        string? localPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Decodes a previously validated local cache file on a worker thread, scales it to card size,
/// closes the file handle, and freezes the result before it crosses back to the WPF UI thread.
/// </summary>
public sealed class OnlineModpackArtworkDecoder : IOnlineModpackArtworkDecoder
{
    public const int MaximumConcurrentDecodes = 2;
    public const int PreviewDecodeWidth = 480;
    public const int PreviewDecodeHeight = 270;
    public const int ServerIconWidth = 256;
    public const int ServerIconHeight = 256;
    public const int ServerPreviewWidth = 1280;
    public const int ServerPreviewHeight = 720;

    private static readonly SemaphoreSlim DecodeGate =
        new(MaximumConcurrentDecodes, MaximumConcurrentDecodes);

    public async Task<ImageSource?> DecodePreviewAsync(
        string? localPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        try
        {
            await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run<ImageSource?>(
                        () => DecodeBitmap(
                            localPath,
                            PreviewDecodeWidth,
                            PreviewDecodeHeight,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                DecodeGate.Release();
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

    /// <summary>
    /// Produces a bounded PNG for persistent server artwork. The caller supplies a new staging
    /// path and remains responsible for atomically promoting it into its confined destination.
    /// </summary>
    public async Task<bool> WriteScaledPngAsync(
        string? localPath,
        string stagingPath,
        int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        if (string.IsNullOrWhiteSpace(localPath)
            || maximumWidth is < 32 or > ServerPreviewWidth
            || maximumHeight is < 32 or > ServerPreviewHeight)
        {
            return false;
        }

        try
        {
            await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var image = DecodeBitmap(
                        localPath,
                        maximumWidth,
                        maximumHeight,
                        cancellationToken);
                    if (image is null)
                    {
                        return false;
                    }

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = new FileStream(
                        Path.GetFullPath(stagingPath),
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        FileOptions.SequentialScan);
                    encoder.Save(output);
                    if (output.Length is <= 0 or > OnlineModpackArtworkCache.MaximumImageBytes)
                    {
                        return false;
                    }

                    output.Flush(flushToDisk: true);
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DecodeGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableImageFailure(exception))
        {
            return false;
        }
    }

    private static BitmapSource? DecodeBitmap(
        string localPath,
        int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = new FileInfo(Path.GetFullPath(localPath));
        if (!file.Exists || file.Length is <= 0 or > OnlineModpackArtworkCache.MaximumImageBytes)
        {
            return null;
        }

        if (file.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeWithSkia(file, maximumWidth, maximumHeight, cancellationToken);
        }

        try
        {
            return DecodeWithWindowsImaging(file, maximumWidth, maximumHeight, cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableWindowsCodecFailure(exception))
        {
            // WebP is optional in Windows Imaging Component and therefore differs by machine.
            // The bundled Skia codec makes provider artwork deterministic on every supported
            // Windows installation while the WIC fast path remains available for native formats.
            return DecodeWithSkia(file, maximumWidth, maximumHeight, cancellationToken);
        }
    }

    private static BitmapSource? DecodeWithWindowsImaging(
        FileInfo file,
        int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        var metadataDecoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        var metadataFrame = metadataDecoder.Frames.FirstOrDefault();
        var originalWidth = metadataFrame?.PixelWidth ?? 0;
        var originalHeight = metadataFrame?.PixelHeight ?? 0;
        if (originalWidth <= 0
            || originalHeight <= 0
            || originalWidth > OnlineModpackArtworkCache.MaximumImageDimension
            || originalHeight > OnlineModpackArtworkCache.MaximumImageDimension
            || (long)originalWidth * originalHeight > OnlineModpackArtworkCache.MaximumImagePixels)
        {
            return null;
        }

        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (originalWidth > maximumWidth || originalHeight > maximumHeight)
        {
            var widthScale = maximumWidth / (double)originalWidth;
            var heightScale = maximumHeight / (double)originalHeight;
            if (widthScale <= heightScale)
            {
                image.DecodePixelWidth = Math.Max(1, (int)Math.Floor(originalWidth * widthScale));
            }
            else
            {
                image.DecodePixelHeight = Math.Max(1, (int)Math.Floor(originalHeight * heightScale));
            }
        }

        image.StreamSource = stream;
        image.EndInit();
        cancellationToken.ThrowIfCancellationRequested();
        if (image.PixelWidth <= 0
            || image.PixelHeight <= 0
            || image.PixelWidth > maximumWidth
            || image.PixelHeight > maximumHeight
            || (long)image.PixelWidth * image.PixelHeight > (long)maximumWidth * maximumHeight)
        {
            return null;
        }

        image.Freeze();
        var normalized = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        normalized.Freeze();
        return normalized;
    }

    private static BitmapSource? DecodeWithSkia(
        FileInfo file,
        int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var codec = SKCodec.Create(stream);
        if (codec is null || !HasSafeSourceDimensions(codec.Info.Width, codec.Info.Height))
        {
            return null;
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                maximumWidth / (double)codec.Info.Width,
                maximumHeight / (double)codec.Info.Height));
        var scaledDimensions = codec.GetScaledDimensions((float)scale);
        var targetWidth = Math.Clamp(scaledDimensions.Width, 1, maximumWidth);
        var targetHeight = Math.Clamp(scaledDimensions.Height, 1, maximumHeight);
        var targetInfo = new SKImageInfo(
            targetWidth,
            targetHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(codec, targetInfo);
        cancellationToken.ThrowIfCancellationRequested();
        if (bitmap is null
            || bitmap.Width <= 0
            || bitmap.Height <= 0
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

    private static bool HasSafeSourceDimensions(int width, int height)
        => width is > 0 and <= OnlineModpackArtworkCache.MaximumImageDimension
           && height is > 0 and <= OnlineModpackArtworkCache.MaximumImageDimension
           && (long)width * height <= OnlineModpackArtworkCache.MaximumImagePixels;

    private static bool IsRecoverableWindowsCodecFailure(Exception exception) =>
        exception is NotSupportedException
            or ArgumentException
            or FormatException
            or System.Runtime.InteropServices.COMException;

    private static bool IsRecoverableImageFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or FormatException
            or System.Runtime.InteropServices.COMException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException;
}
