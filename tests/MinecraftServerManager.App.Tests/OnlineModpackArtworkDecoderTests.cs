using System.IO;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackArtworkDecoderTests
{
    [Fact]
    public async Task DecodePreviewAsync_LoadsAndFreezesValidatedLocalImage()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "preview.png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var decoder = new OnlineModpackArtworkDecoder();

            var image = await decoder.DecodePreviewAsync(path);

            Assert.NotNull(image);
            Assert.True(image.IsFrozen);
            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DecodePreviewAsync_InvalidImageReturnsNull()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "invalid.png");
            await File.WriteAllTextAsync(path, "not an image");
            var decoder = new OnlineModpackArtworkDecoder();

            var image = await decoder.DecodePreviewAsync(path);

            Assert.Null(image);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DecodePreviewAsync_DecodesWebpWithoutDependingOnWindowsOptionalCodec()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "preview.webp");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(
                "UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA=="));
            var decoder = new OnlineModpackArtworkDecoder();

            var image = await decoder.DecodePreviewAsync(path);

            Assert.NotNull(image);
            Assert.True(image.IsFrozen);
            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteScaledPngAsync_CreatesFrozenCompatiblePngWithoutKeepingSourceOpen()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(directory, "source.png");
            var destination = Path.Combine(directory, "staged.tmp");
            await File.WriteAllBytesAsync(source, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var decoder = new OnlineModpackArtworkDecoder();

            var written = await decoder.WriteScaledPngAsync(
                source,
                destination,
                OnlineModpackArtworkDecoder.ServerIconWidth,
                OnlineModpackArtworkDecoder.ServerIconHeight);

            Assert.True(written);
            Assert.True(File.Exists(destination));
            Assert.Equal(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a },
                (await File.ReadAllBytesAsync(destination))[..8]);
            File.Delete(source);
            File.Delete(destination);
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
            $"mcsv-artwork-decoder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
