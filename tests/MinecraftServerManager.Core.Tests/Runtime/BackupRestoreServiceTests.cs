using System.IO.Compression;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class BackupRestoreServiceTests
{
    [Fact]
    public async Task RestoreAsync_RestoresManagerBackupIntoNewDirectoryAndReturnsResult()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = Path.Combine(temporaryDirectory.Path, "source");
        Directory.CreateDirectory(Path.Combine(source, "world"));
        Directory.CreateDirectory(Path.Combine(source, "plugins"));
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "motd=original");
        await File.WriteAllTextAsync(Path.Combine(source, "world", "level.dat"), "world-data");
        await File.WriteAllTextAsync(Path.Combine(source, "plugins", "sample.jar"), "plugin-data");
        var backup = await new BackupService().CreateBackupAsync(
            source,
            "restore-test",
            new BackupOptions { ArchiveFileName = "restore-test.zip" });
        var destination = Path.Combine(temporaryDirectory.Path, "restored-server");

        var result = await new BackupRestoreService().RestoreAsync(backup.ArchivePath, destination);

        Assert.Equal(Path.GetFullPath(backup.ArchivePath), result.ArchivePath);
        Assert.Equal(Path.GetFullPath(destination), result.DestinationDirectory);
        Assert.Equal(3, result.RestoredFileCount);
        Assert.Equal(2, result.RestoredDirectoryCount);
        Assert.Equal(
            new FileInfo(Path.Combine(source, "server.properties")).Length
            + new FileInfo(Path.Combine(source, "world", "level.dat")).Length
            + new FileInfo(Path.Combine(source, "plugins", "sample.jar")).Length,
            result.RestoredUncompressedBytes);
        Assert.Equal(new FileInfo(backup.ArchivePath).Length, result.ArchiveBytes);
        Assert.Equal("motd=original", await File.ReadAllTextAsync(Path.Combine(destination, "server.properties")));
        Assert.Equal("world-data", await File.ReadAllTextAsync(Path.Combine(destination, "world", "level.dat")));
        Assert.Equal("plugin-data", await File.ReadAllTextAsync(Path.Combine(destination, "plugins", "sample.jar")));
        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(backup.ArchivePath));
        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    [Fact]
    public async Task RestoreAsync_RestoresExplicitEmptyDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "empty-directory.zip");
        CreateZip(archive, ("empty/", [], null));
        var destination = Path.Combine(temporaryDirectory.Path, "restored");

        var result = await new BackupRestoreService().RestoreAsync(archive, destination);

        Assert.Equal(0, result.RestoredFileCount);
        Assert.Equal(1, result.RestoredDirectoryCount);
        Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
    }

    [Fact]
    public async Task RestoreAsync_RefusesExistingDestinationWithoutChangingIt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "backup.zip");
        CreateZip(archive, ("server.properties", "new"u8.ToArray(), null));
        var destination = Path.Combine(temporaryDirectory.Path, "existing-server");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        await Assert.ThrowsAsync<IOException>(() =>
            new BackupRestoreService().RestoreAsync(archive, destination));

        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(destination, "server.properties")));
        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    [Fact]
    public async Task RestoreAsync_ExplicitTrustedRootAllowsCloudReparseAncestor()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var physicalCloudRoot = Path.Combine(temporaryDirectory.Path, "physical-cloud-root");
        var physicalTrustedRoot = Path.Combine(physicalCloudRoot, "managed-servers");
        Directory.CreateDirectory(physicalTrustedRoot);
        var cloudAlias = Path.Combine(temporaryDirectory.Path, "cloud-provider-alias");
        ReparsePointTestHelper.CreateDirectoryLink(cloudAlias, physicalCloudRoot);
        var trustedRoot = Path.Combine(cloudAlias, "managed-servers");
        var archive = Path.Combine(temporaryDirectory.Path, "backup.zip");
        CreateZip(archive, ("server.properties", "motd=cloud"u8.ToArray(), null));
        var destination = Path.Combine(trustedRoot, "restored-server");

        try
        {
            var result = await new BackupRestoreService().RestoreAsync(
                archive,
                destination,
                new BackupRestoreOptions { TrustedDestinationRoot = trustedRoot });

            Assert.Equal(Path.GetFullPath(destination), result.DestinationDirectory);
            Assert.Equal(
                "motd=cloud",
                await File.ReadAllTextAsync(Path.Combine(destination, "server.properties")));
            Assert.True(File.Exists(Path.Combine(
                physicalTrustedRoot,
                "restored-server",
                "server.properties")));
            AssertNoRestoreStagingDirectories(trustedRoot);
        }
        finally
        {
            Directory.Delete(cloudAlias);
        }
    }

    [Fact]
    public async Task RestoreAsync_ExplicitTrustedRootRejectsDestinationOutsideRoot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var trustedRoot = Path.Combine(temporaryDirectory.Path, "managed-servers");
        var outside = Path.Combine(temporaryDirectory.Path, "outside");
        Directory.CreateDirectory(trustedRoot);
        Directory.CreateDirectory(outside);
        var archive = Path.Combine(temporaryDirectory.Path, "backup.zip");
        CreateZip(archive, ("server.properties", "motd=test"u8.ToArray(), null));
        var destination = Path.Combine(outside, "restored-server");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new BackupRestoreService().RestoreAsync(
                archive,
                destination,
                new BackupRestoreOptions { TrustedDestinationRoot = trustedRoot }));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task RestoreAsync_ExplicitTrustedRootRejectsNestedReparseRedirect()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var trustedRoot = Path.Combine(temporaryDirectory.Path, "managed-servers");
        var outside = Path.Combine(temporaryDirectory.Path, "outside");
        Directory.CreateDirectory(trustedRoot);
        Directory.CreateDirectory(outside);
        var redirect = Path.Combine(trustedRoot, "redirect");
        ReparsePointTestHelper.CreateDirectoryLink(redirect, outside);
        var archive = Path.Combine(temporaryDirectory.Path, "backup.zip");
        CreateZip(archive, ("server.properties", "motd=test"u8.ToArray(), null));
        var destination = Path.Combine(redirect, "restored-server");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new BackupRestoreService().RestoreAsync(
                    archive,
                    destination,
                    new BackupRestoreOptions { TrustedDestinationRoot = trustedRoot }));

            Assert.False(Directory.Exists(Path.Combine(outside, "restored-server")));
        }
        finally
        {
            Directory.Delete(redirect);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("folder/file.txt:payload")]
    [InlineData("folder\\file.txt")]
    [InlineData("CON/readme.txt")]
    [InlineData("folder/LPT1.log")]
    [InlineData("folder/COM¹.txt")]
    [InlineData("folder/trailing.")]
    [InlineData("folder/trailing ")]
    public async Task RestoreAsync_RejectsUnsafeOrWindowsReservedPaths(string entryPath)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "unsafe.zip");
        CreateZip(archive, (entryPath, "unsafe"u8.ToArray(), null));
        var destination = Path.Combine(temporaryDirectory.Path, "restored");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new BackupRestoreService().RestoreAsync(archive, destination));

        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, "outside.txt")));
        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    [Theory]
    [MemberData(nameof(CollisionArchives))]
    public async Task RestoreAsync_RejectsCaseInsensitiveUnicodeAndFileDirectoryCollisions(
        (string Path, byte[] Content, int? Attributes)[] entries)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "collision.zip");
        CreateZip(archive, entries);
        var destination = Path.Combine(temporaryDirectory.Path, "restored");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new BackupRestoreService().RestoreAsync(archive, destination));

        Assert.False(Directory.Exists(destination));
        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    public static TheoryData<(string Path, byte[] Content, int? Attributes)[]> CollisionArchives =>
        new()
        {
            new[]
            {
                ("mods/Example.jar", "one"u8.ToArray(), (int?)null),
                ("MODS/example.jar", "two"u8.ToArray(), (int?)null),
            },
            new[]
            {
                ("mods/e\u0301.jar", "one"u8.ToArray(), (int?)null),
                ("mods/é.jar", "two"u8.ToArray(), (int?)null),
            },
            new[]
            {
                ("config", "file"u8.ToArray(), (int?)null),
                ("config/value.txt", "child"u8.ToArray(), (int?)null),
            },
        };

    [Fact]
    public async Task RestoreAsync_RejectsUnixSymlinkAndDosReparseEntries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var symbolicLinkArchive = Path.Combine(temporaryDirectory.Path, "symlink.zip");
        var symbolicLinkAttributes = unchecked((int)((0xA000u | 0x1FFu) << 16));
        CreateZip(symbolicLinkArchive, ("link", "target"u8.ToArray(), symbolicLinkAttributes));
        var reparseArchive = Path.Combine(temporaryDirectory.Path, "reparse.zip");
        CreateZip(
            reparseArchive,
            ("link", "target"u8.ToArray(), (int)FileAttributes.ReparsePoint));
        var upperReparseArchive = Path.Combine(temporaryDirectory.Path, "upper-reparse.zip");
        CreateZip(
            upperReparseArchive,
            ("link", "target"u8.ToArray(), (int)FileAttributes.ReparsePoint << 16));
        var service = new BackupRestoreService();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            symbolicLinkArchive,
            Path.Combine(temporaryDirectory.Path, "symlink-result")));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            reparseArchive,
            Path.Combine(temporaryDirectory.Path, "reparse-result")));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            upperReparseArchive,
            Path.Combine(temporaryDirectory.Path, "upper-reparse-result")));

        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    [Fact]
    public async Task RestoreAsync_EnforcesEntryAndFileCountLimits()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "counts.zip");
        CreateZip(
            archive,
            ("one.txt", "1"u8.ToArray(), null),
            ("two.txt", "2"u8.ToArray(), null));
        var service = new BackupRestoreService();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            archive,
            Path.Combine(temporaryDirectory.Path, "entry-limit"),
            new BackupRestoreOptions { MaxEntryCount = 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            archive,
            Path.Combine(temporaryDirectory.Path, "file-limit"),
            new BackupRestoreOptions { MaxFileCount = 1 }));

        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    [Fact]
    public async Task RestoreAsync_EnforcesPerFileAndTotalUncompressedLimits()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "sizes.zip");
        CreateZip(
            archive,
            ("one.bin", new byte[4], null),
            ("two.bin", new byte[4], null));
        var service = new BackupRestoreService();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            archive,
            Path.Combine(temporaryDirectory.Path, "file-size-limit"),
            new BackupRestoreOptions { MaxFileUncompressedBytes = 3 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            archive,
            Path.Combine(temporaryDirectory.Path, "total-size-limit"),
            new BackupRestoreOptions { MaxTotalUncompressedBytes = 7 }));

        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    [Fact]
    public async Task RestoreAsync_RejectsUnsafeCompressionRatio()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "ratio.zip");
        CreateZip(archive, ("zeros.bin", new byte[128 * 1024], null));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new BackupRestoreService().RestoreAsync(
                archive,
                Path.Combine(temporaryDirectory.Path, "restored"),
                new BackupRestoreOptions { MaxCompressionRatio = 2 }));

        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    [Fact]
    public async Task RestoreAsync_CancellationRemovesStagingAndDoesNotPublishDestination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "cancel.zip");
        CreateZip(archive, ("world/large.bin", RandomBytes(2 * 1024 * 1024), null));
        var destination = Path.Combine(temporaryDirectory.Path, "restored");
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<BackupRestoreProgress>(value =>
        {
            if (value.Stage == BackupRestoreStage.Extracting && value.CompletedBytes > 0)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BackupRestoreService().RestoreAsync(
                archive,
                destination,
                new BackupRestoreOptions { BufferSize = 4 * 1024 },
                progress,
                cancellation.Token));

        Assert.False(Directory.Exists(destination));
        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    [Fact]
    public async Task RestoreAsync_CommitFailureRemovesStagingWithoutMergingIntoDestination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = Path.Combine(temporaryDirectory.Path, "commit-race.zip");
        CreateZip(archive, ("server.properties", "motd=test"u8.ToArray(), null));
        var destination = Path.Combine(temporaryDirectory.Path, "restored");
        var progress = new CallbackProgress<BackupRestoreProgress>(value =>
        {
            if (value.Stage == BackupRestoreStage.Committing && !Directory.Exists(destination))
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "sentinel.txt"), "keep");
            }
        });

        await Assert.ThrowsAsync<IOException>(() =>
            new BackupRestoreService().RestoreAsync(archive, destination, progress: progress));

        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "server.properties")));
        AssertNoRestoreStagingDirectories(temporaryDirectory.Path);
    }

    private static void CreateZip(
        string path,
        params (string Path, byte[] Content, int? Attributes)[] entries)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path, CompressionLevel.SmallestSize);
            if (item.Attributes is { } attributes)
            {
                entry.ExternalAttributes = attributes;
            }

            if (item.Content.Length > 0)
            {
                using var output = entry.Open();
                output.Write(item.Content);
            }
        }
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static void AssertNoRestoreStagingDirectories(string parent)
    {
        Assert.Empty(Directory.EnumerateDirectories(parent, "*.restore-*.partial"));
    }
}
