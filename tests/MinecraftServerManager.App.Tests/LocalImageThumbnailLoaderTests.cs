using System.IO;
using System.Windows.Media.Imaging;
using MinecraftServerManager.App.Infrastructure;
using SkiaSharp;

namespace MinecraftServerManager.App.Tests;

public sealed class LocalImageThumbnailLoaderTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task LoadAsync_ReturnsFrozenImageAndDoesNotLockSourceFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "icon.png");
            await File.WriteAllBytesAsync(path, OnePixelPng);
            var loader = new LocalImageThumbnailLoader();

            var result = await loader.LoadAsync(path, 64, 64);

            var bitmap = Assert.IsAssignableFrom<BitmapSource>(result);
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(1, bitmap.PixelWidth);
            Assert.Equal(1, bitmap.PixelHeight);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // Exclusive access proves BitmapCacheOption.OnLoad/Skia fallback released the file.
            }

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.png")]
    [InlineData("https://example.test/image.png")]
    [InlineData("\\\\server\\share\\image.png")]
    public async Task LoadAsync_RejectsMissingOrNonLocalPaths(string? path)
    {
        var loader = new LocalImageThumbnailLoader();

        var result = await loader.LoadAsync(path);

        Assert.Null(result);
        Assert.Equal(0, loader.CachedItemCount);
    }

    [Fact]
    public async Task LoadAsync_InvalidImageReturnsNull()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "invalid.png");
            await File.WriteAllTextAsync(path, "not an image");
            var loader = new LocalImageThumbnailLoader();

            var result = await loader.LoadAsync(path);

            Assert.Null(result);
            Assert.Equal(0, loader.CachedItemCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_DecodesToRequestedBoundsInsteadOfFullSourceSize()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "large.png");
            using (var bitmap = new SKBitmap(new SKImageInfo(800, 400)))
            {
                bitmap.Erase(SKColors.CornflowerBlue);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                await using var output = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                data.SaveTo(output);
            }

            using (var validation = SKBitmap.Decode(path))
            {
                Assert.NotNull(validation);
                Assert.Equal(800, validation.Width);
                Assert.Equal(400, validation.Height);
            }

            var loader = new LocalImageThumbnailLoader();

            var result = Assert.IsAssignableFrom<BitmapSource>(
                await loader.LoadAsync(path, 100, 100));

            Assert.Equal(100, result.PixelWidth);
            Assert.Equal(50, result.PixelHeight);
            Assert.True(result.IsFrozen);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsSourceLargerThanConfiguredSafetyLimit()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "oversized.png");
            await using (var output = new FileStream(
                             path,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                output.SetLength(LocalImageThumbnailLoader.MaximumSourceBytes + 1);
            }

            var loader = new LocalImageThumbnailLoader();

            Assert.Null(await loader.LoadAsync(path));
            Assert.Equal(0, loader.CachedItemCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_UsesBoundedLeastRecentlyUsedCache()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var loader = new LocalImageThumbnailLoader(cacheEntryLimit: 2);
            for (var index = 0; index < 3; index++)
            {
                var path = Path.Combine(directory, $"icon-{index}.png");
                await File.WriteAllBytesAsync(path, OnePixelPng);
                Assert.NotNull(await loader.LoadAsync(path, 32, 32));
            }

            Assert.Equal(2, loader.CachedItemCount);
            Assert.InRange(loader.CachedApproximateBytes, 2, 8);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsCachedFrozenObjectForUnchangedFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "cached.png");
            await File.WriteAllBytesAsync(path, OnePixelPng);
            var loader = new LocalImageThumbnailLoader();

            var first = await loader.LoadAsync(path, 48, 48);
            var second = await loader.LoadAsync(path, 48, 48);

            Assert.Same(first, second);
            Assert.Equal(1, loader.CachedItemCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_PropagatesCallerCancellation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "cancel.png");
            await File.WriteAllBytesAsync(path, OnePixelPng);
            var loader = new LocalImageThumbnailLoader();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => loader.LoadAsync(path, cancellationToken: cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"x-mcsv-local-thumbnail-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
