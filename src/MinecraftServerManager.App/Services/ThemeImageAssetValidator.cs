using System.Windows.Media.Imaging;

namespace MinecraftServerManager.App.Services;

/// <summary>Validates image content before it is copied into or decoded by the theme system.</summary>
internal static class ThemeImageAssetValidator
{
    internal const long MaximumFileBytes = 64L * 1024 * 1024;
    internal const long MaximumDecodedPixels = 64_000_000;

    private static readonly HashSet<string> BackgroundExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp"
    };

    public static void ValidateBackground(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate(path, BackgroundExtensions, "僅支援 PNG、JPG、JPEG 或 BMP 背景圖片。");
    }

    private static void Validate(string path, IReadOnlySet<string> extensions, string extensionError)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists)
        {
            throw new FileNotFoundException("找不到選取的圖片。", file.FullName);
        }

        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("基於安全考量，不接受符號連結或重新解析點圖片。");
        }

        if (!extensions.Contains(file.Extension))
        {
            throw new InvalidDataException(extensionError);
        }

        if (file.Length <= 0 || file.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("背景圖片必須有內容且不可超過 64 MB。");
        }

        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("圖片沒有可顯示的影格。");
            }

            var frame = decoder.Frames[0];
            var decodedPixels = checked((long)frame.PixelWidth * frame.PixelHeight);
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || decodedPixels > MaximumDecodedPixels)
            {
                throw new InvalidDataException("圖片尺寸無效或解碼後超過 6,400 萬像素。");
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or OverflowException)
        {
            throw new InvalidDataException("選取的檔案不是有效或受支援的圖片。", exception);
        }
    }
}
