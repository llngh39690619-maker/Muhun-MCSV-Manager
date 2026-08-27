using System.IO.Compression;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class AdoptiumRuntimeArchiveTests
{
    [Fact]
    public async Task ExtractZipSafelyAsync_SupportsChinesePathsWithinFreshStaging()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = Path.Combine(temporaryDirectory.Path, "java.zip");
        var destination = Path.Combine(temporaryDirectory.Path, "Java 暫存");
        Directory.CreateDirectory(destination);
        CreateZip(archivePath, ("jdk-21/說明.txt", "安全"u8.ToArray(), null));

        await AdoptiumRuntimeProvider.ExtractZipSafelyAsync(
            archivePath,
            destination,
            CancellationToken.None);

        Assert.Equal(
            "安全",
            await File.ReadAllTextAsync(Path.Combine(destination, "jdk-21", "說明.txt")));
    }

    [Fact]
    public async Task ExtractZipSafelyAsync_RejectsTraversalWithoutWritingOutsideStaging()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = Path.Combine(temporaryDirectory.Path, "traversal.zip");
        var destination = Path.Combine(temporaryDirectory.Path, "staging");
        Directory.CreateDirectory(destination);
        CreateZip(archivePath, ("../outside.txt", "bad"u8.ToArray(), null));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AdoptiumRuntimeProvider.ExtractZipSafelyAsync(
                archivePath,
                destination,
                CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, "outside.txt")));
    }

    [Fact]
    public async Task ExtractZipSafelyAsync_RejectsSymbolicLinkEntry()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = Path.Combine(temporaryDirectory.Path, "link.zip");
        var destination = Path.Combine(temporaryDirectory.Path, "staging");
        Directory.CreateDirectory(destination);
        var symbolicLinkAttributes = unchecked((int)((0xA000u | 0x1FFu) << 16));
        CreateZip(archivePath, ("jdk-21/bin/java.exe", "target"u8.ToArray(), symbolicLinkAttributes));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AdoptiumRuntimeProvider.ExtractZipSafelyAsync(
                archivePath,
                destination,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExtractZipSafelyAsync_AcceptsAdoptiumUnixDirectoryAttributes()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = Path.Combine(temporaryDirectory.Path, "java.zip");
        var destination = Path.Combine(temporaryDirectory.Path, "staging");
        Directory.CreateDirectory(destination);

        // The real Temurin 25.0.4+7 ZIP uses Unix host OS 3 and these exact external
        // attributes. Its directory mode includes Unix set-group-ID (0x0400), which must not
        // be read as Windows ReparsePoint.
        var unixDirectoryAttributes = unchecked((int)0x45F80010u);
        var unixRegularFileAttributes = unchecked((int)0x81F80000u);
        CreateZip(
            archivePath,
            ("jdk-25.0.4+7/", [], unixDirectoryAttributes),
            ("jdk-25.0.4+7/release", "JAVA_VERSION=25"u8.ToArray(), unixRegularFileAttributes));

        await AdoptiumRuntimeProvider.ExtractZipSafelyAsync(
            archivePath,
            destination,
            CancellationToken.None);

        Assert.Equal(
            "JAVA_VERSION=25",
            await File.ReadAllTextAsync(Path.Combine(destination, "jdk-25.0.4+7", "release")));
    }

    [Theory]
    [InlineData(0x00000400)] // Windows/DOS reparse attribute in the lower word.
    [InlineData(0x04000000)] // Raw Windows/DOS attributes stored in the upper word.
    [InlineData(unchecked((int)0x11A40000u))] // Unix FIFO with ordinary permissions.
    [InlineData(unchecked((int)0x21A40000u))] // Unix character device.
    [InlineData(unchecked((int)0x61A40000u))] // Unix block device.
    [InlineData(unchecked((int)0xC1A40000u))] // Unix socket.
    public async Task ExtractZipSafelyAsync_RejectsReparseAndUnixSpecialEntries(int externalAttributes)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = Path.Combine(temporaryDirectory.Path, "unsafe.zip");
        var destination = Path.Combine(temporaryDirectory.Path, "staging");
        Directory.CreateDirectory(destination);
        CreateZip(
            archivePath,
            ("jdk-25.0.4+7/bin/java.exe", "unsafe"u8.ToArray(), externalAttributes));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AdoptiumRuntimeProvider.ExtractZipSafelyAsync(
                archivePath,
                destination,
                CancellationToken.None));
    }

    [Fact]
    public async Task MoveDirectoryWithRetryAsync_RetriesTransientImageLockAndCommitsAtomically()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = Path.Combine(temporaryDirectory.Path, "jdk-25.0.4+7");
        var destination = Path.Combine(temporaryDirectory.Path, "temurin-jdk-25-jdk-25.0.4+7");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "release"), "JAVA_VERSION=25");
        var attempts = 0;
        var delays = new List<TimeSpan>();

        await AdoptiumRuntimeProvider.MoveDirectoryWithRetryAsync(
            source,
            destination,
            CancellationToken.None,
            (from, to) =>
            {
                attempts++;
                if (attempts <= 3)
                {
                    throw new IOException(
                        "The just-executed image is still locked.",
                        unchecked((int)0x80070005));
                }

                Directory.Move(from, to);
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(4, attempts);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(750)],
            delays);
        Assert.False(Directory.Exists(source));
        Assert.Equal("JAVA_VERSION=25", await File.ReadAllTextAsync(Path.Combine(destination, "release")));
    }

    [Fact]
    public async Task MoveDirectoryWithRetryAsync_DoesNotRetryNonTransientIoFailure()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = Path.Combine(temporaryDirectory.Path, "source");
        var destination = Path.Combine(temporaryDirectory.Path, "destination");
        Directory.CreateDirectory(source);
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            AdoptiumRuntimeProvider.MoveDirectoryWithRetryAsync(
                source,
                destination,
                CancellationToken.None,
                (_, _) =>
                {
                    attempts++;
                    throw new IOException(
                        "Destination already exists.",
                        unchecked((int)0x800700B7));
                },
                (_, _) => throw new Xunit.Sdk.XunitException("Non-transient failure must not delay.")));

        Assert.Equal(unchecked((int)0x800700B7), exception.HResult);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task MoveDirectoryWithRetryAsync_RejectsSourceReplacedByJunctionDuringDelay()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = Path.Combine(temporaryDirectory.Path, "verified-jdk");
        var destination = Path.Combine(temporaryDirectory.Path, "committed-jdk");
        var outside = Path.Combine(temporaryDirectory.Path, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(source, "release"), "verified");
        await File.WriteAllTextAsync(Path.Combine(outside, "sentinel.txt"), "outside-must-remain");
        var attempts = 0;

        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AdoptiumRuntimeProvider.MoveDirectoryWithRetryAsync(
                    source,
                    destination,
                    CancellationToken.None,
                    (_, _) =>
                    {
                        attempts++;
                        throw new IOException(
                            "The just-executed image is still locked.",
                            unchecked((int)0x80070005));
                    },
                    (_, _) =>
                    {
                        Directory.Delete(source, recursive: true);
                        ReparsePointTestHelper.CreateDirectoryLink(source, outside);
                        return Task.CompletedTask;
                    }));

            Assert.Equal(1, attempts);
            Assert.False(Directory.Exists(destination));
            Assert.Equal(
                "outside-must-remain",
                await File.ReadAllTextAsync(Path.Combine(outside, "sentinel.txt")));
        }
        finally
        {
            if (Directory.Exists(source)
                && File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(source, recursive: false);
            }
        }
    }

    private static void CreateZip(
        string path,
        params (string Name, byte[] Content, int? ExternalAttributes)[] entries)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
            if (item.ExternalAttributes is { } attributes)
            {
                entry.ExternalAttributes = attributes;
            }

            using var output = entry.Open();
            output.Write(item.Content);
        }
    }
}
