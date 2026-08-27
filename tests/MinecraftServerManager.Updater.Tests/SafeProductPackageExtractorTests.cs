using System.IO.Compression;
using System.Security.Cryptography;
using System.Diagnostics;

namespace MinecraftServerManager.Updater.Tests;

public sealed class SafeProductPackageExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-UpdaterTests",
        Guid.NewGuid().ToString("N"));

    public SafeProductPackageExtractorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task MatchingPackage_ExtractsAndVerifiesEveryFile()
    {
        var content = "MZ product"u8.ToArray();
        await using var archive = CreateArchive(("Muhun MCSV Manager.exe", content));
        var manifest = ProductUpdateManifestTests.CreateManifest(
            [new ProductUpdateFile(
                "Muhun MCSV Manager.exe",
                content.Length,
                Convert.ToHexString(SHA256.HashData(content)))]) with
        {
            Package = new ProductUpdatePackage(
                "https://updates.example.com/product.zip",
                archive.Length,
                Convert.ToHexString(SHA256.HashData(archive.ToArray()))),
        };

        await new SafeProductPackageExtractor().ExtractAndVerifyAsync(
            archive,
            Path.Combine(_directory, "staging"),
            manifest);

        Assert.Equal(
            content,
            await File.ReadAllBytesAsync(Path.Combine(_directory, "staging", "Muhun MCSV Manager.exe")));
    }

    [Fact]
    public async Task TraversalOrUnexpectedEntry_IsRejected()
    {
        await using var archive = CreateArchive(("../escape.exe", "bad"u8.ToArray()));
        var manifest = ProductUpdateManifestTests.CreateManifest() with
        {
            Package = new ProductUpdatePackage(
                "https://updates.example.com/product.zip",
                archive.Length,
                Convert.ToHexString(SHA256.HashData(archive.ToArray()))),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SafeProductPackageExtractor().ExtractAndVerifyAsync(
                archive,
                Path.Combine(_directory, "staging"),
                manifest));
        Assert.False(File.Exists(Path.Combine(_directory, "escape.exe")));
    }

    [Fact]
    public async Task HashMismatch_IsRejected()
    {
        var content = "MZ product"u8.ToArray();
        await using var archive = CreateArchive(("Muhun MCSV Manager.exe", content));
        var manifest = ProductUpdateManifestTests.CreateManifest(
            [new ProductUpdateFile("Muhun MCSV Manager.exe", content.Length, new string('0', 64))]) with
        {
            Package = new ProductUpdatePackage(
                "https://updates.example.com/product.zip",
                archive.Length,
                Convert.ToHexString(SHA256.HashData(archive.ToArray()))),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SafeProductPackageExtractor().ExtractAndVerifyAsync(
                archive,
                Path.Combine(_directory, "staging"),
                manifest));
    }

    [Fact]
    public async Task PackageHashMismatch_IsRejectedBeforeExtraction()
    {
        await using var archive = CreateArchive(("Muhun MCSV Manager.exe", "MZ product"u8.ToArray()));
        var manifest = ProductUpdateManifestTests.CreateManifest() with
        {
            Package = new ProductUpdatePackage(
                "https://updates.example.com/product.zip",
                archive.Length,
                new string('0', 64)),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SafeProductPackageExtractor().ExtractAndVerifyAsync(
                archive,
                Path.Combine(_directory, "staging"),
                manifest));
        Assert.False(Directory.Exists(Path.Combine(_directory, "staging")));
    }

    [Fact]
    public async Task HighCompressionRatioZipBomb_IsRejectedBeforeExpansion()
    {
        var content = new byte[16 * 1024 * 1024];
        await using var archive = CreateArchive(("Muhun MCSV Manager.exe", content));
        var manifest = ProductUpdateManifestTests.CreateManifest(
            [new ProductUpdateFile(
                "Muhun MCSV Manager.exe",
                content.LongLength,
                Convert.ToHexString(SHA256.HashData(content)))]) with
        {
            Package = new ProductUpdatePackage(
                "https://updates.example.com/product.zip",
                archive.Length,
                Convert.ToHexString(SHA256.HashData(archive.ToArray()))),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SafeProductPackageExtractor().ExtractAndVerifyAsync(
                archive,
                Path.Combine(_directory, "bomb-staging"),
                manifest));
    }

    [Fact]
    public async Task DestinationThroughJunction_IsRejectedWithoutTouchingExternalDirectory()
    {
        var outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.keep");
        await File.WriteAllTextAsync(sentinel, "outside");
        var link = Path.Combine(_directory, "linked-parent");
        CreateDirectoryJunction(link, outside);
        try
        {
            var content = "MZ product"u8.ToArray();
            await using var archive = CreateArchive(("Muhun MCSV Manager.exe", content));
            var manifest = ProductUpdateManifestTests.CreateManifest(
                [new ProductUpdateFile(
                    "Muhun MCSV Manager.exe",
                    content.LongLength,
                    Convert.ToHexString(SHA256.HashData(content)))]) with
            {
                Package = new ProductUpdatePackage(
                    "https://updates.example.com/product.zip",
                    archive.Length,
                    Convert.ToHexString(SHA256.HashData(archive.ToArray()))),
            };

            await Assert.ThrowsAsync<IOException>(() =>
                new SafeProductPackageExtractor().ExtractAndVerifyAsync(
                    archive,
                    Path.Combine(link, "staging"),
                    manifest));

            Assert.Equal("outside", await File.ReadAllTextAsync(sentinel));
            Assert.False(Directory.Exists(Path.Combine(outside, "staging")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static MemoryStream CreateArchive(params (string Path, byte[] Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                using var output = entry.Open();
                output.Write(file.Content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
        }) ?? throw new InvalidOperationException("Could not create test junction.");
        process.WaitForExit();
        if (process.ExitCode != 0 ||
            !File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Could not create test reparse point.");
        }
    }
}
