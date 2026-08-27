using System.IO.Compression;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task CreateBackupAsync_ExcludesBackupsAndCache()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = temporaryDirectory.Path;
        Directory.CreateDirectory(Path.Combine(source, "world"));
        Directory.CreateDirectory(Path.Combine(source, "plugins"));
        Directory.CreateDirectory(Path.Combine(source, "backups"));
        Directory.CreateDirectory(Path.Combine(source, "cache"));
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "motd=test");
        await File.WriteAllTextAsync(Path.Combine(source, "world", "level.dat"), "world");
        await File.WriteAllTextAsync(Path.Combine(source, "plugins", "example.jar"), "plugin");
        await File.WriteAllTextAsync(Path.Combine(source, "backups", "old.zip"), "old");
        await File.WriteAllTextAsync(Path.Combine(source, "cache", "download.tmp"), "cache");
        var service = new BackupService();

        var result = await service.CreateBackupAsync(
            source,
            "test-server",
            new BackupOptions { ArchiveFileName = "snapshot.zip" });

        Assert.True(File.Exists(result.ArchivePath));
        Assert.Equal(3, result.FileCount);
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        var entries = archive.Entries.Select(entry => entry.FullName).Order().ToArray();
        Assert.Equal(
            ["plugins/example.jar", "server.properties", "world/level.dat"],
            entries);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.Combine(source, "backups")),
            path => path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateBackupAsync_CancellationDeletesPartialArchive()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = temporaryDirectory.Path;
        await File.WriteAllBytesAsync(Path.Combine(source, "world.dat"), new byte[256 * 1024]);
        var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<BackupProgress>(value =>
        {
            if (value.Stage == BackupStage.Compressing)
            {
                cancellation.Cancel();
            }
        });
        var service = new BackupService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateBackupAsync(
            source,
            "cancelled",
            new BackupOptions { ArchiveFileName = "cancelled.zip" },
            progress,
            cancellation.Token));

        var backupDirectory = Path.Combine(source, "backups");
        Assert.False(File.Exists(Path.Combine(backupDirectory, "cancelled.zip")));
        Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.partial"));
    }

    [Fact]
    public async Task CreateBackupAsync_ExcludesConfiguredFileNamesAtAnyDepth()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = temporaryDirectory.Path;
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "manager.json"), "manager");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.10.exe"), "current-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.9.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0 Preview 9.exe"), "current-preview-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.9.exe"), "current-preview-gui-alias");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0 Preview 8.exe"), "current-preview-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.8.exe"), "current-preview-gui-alias");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0 Preview 7.exe"), "current-preview-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.7.exe"), "current-preview-gui-alias");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0 Preview 6.exe"), "current-preview-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.6.exe"), "current-preview-gui-alias");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0 Preview 5.exe"), "current-preview-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.5.exe"), "current-preview-gui-alias");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0 Preview 4.exe"), "current-preview-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.4.exe"), "current-preview-gui-alias");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0 Preview 3.exe"), "current-preview-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.3.exe"), "current-preview-gui-alias");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0 Preview 2.exe"), "current-preview-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.2.exe"), "current-preview-gui-alias");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.5.0-preview.1.exe"), "experimental-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.8.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.7.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.6.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.5.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.4.exe"), "current-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.3.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.2.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.1.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.4.0.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.3.1.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.3.0.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "Muhun MCSV Manager 0.2.5.exe"), "previous-gui");
        await File.WriteAllTextAsync(Path.Combine(source, "MinecraftServerManager.exe"), "legacy-gui");
        await File.WriteAllTextAsync(
            Path.Combine(source, ".minecraft-server-manager.lock"),
            "manager-lock");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "MANAGER.JSON"), "nested-manager");
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "motd=test");

        var result = await new BackupService().CreateBackupAsync(
            source,
            "same-root",
            new BackupOptions
            {
                ArchiveFileName = "excluded-files.zip",
                ExcludedFileNames =
                [
                    "manager.json",
                    "Muhun MCSV Manager 0.4.10.exe",
                    "Muhun MCSV Manager 0.4.9.exe",
                    "Muhun MCSV Manager 0.5.0 Preview 9.exe",
                    "Muhun MCSV Manager 0.5.0-preview.9.exe",
                    "Muhun MCSV Manager 0.5.0 Preview 8.exe",
                    "Muhun MCSV Manager 0.5.0-preview.8.exe",
                    "Muhun MCSV Manager 0.5.0 Preview 7.exe",
                    "Muhun MCSV Manager 0.5.0-preview.7.exe",
                    "Muhun MCSV Manager 0.5.0 Preview 6.exe",
                    "Muhun MCSV Manager 0.5.0-preview.6.exe",
                    "Muhun MCSV Manager 0.5.0 Preview 5.exe",
                    "Muhun MCSV Manager 0.5.0-preview.5.exe",
                    "Muhun MCSV Manager 0.5.0 Preview 4.exe",
                    "Muhun MCSV Manager 0.5.0-preview.4.exe",
                    "Muhun MCSV Manager 0.5.0 Preview 3.exe",
                    "Muhun MCSV Manager 0.5.0-preview.3.exe",
                    "Muhun MCSV Manager 0.5.0 Preview 2.exe",
                    "Muhun MCSV Manager 0.5.0-preview.2.exe",
                    "Muhun MCSV Manager 0.5.0-preview.1.exe",
                    "Muhun MCSV Manager 0.4.8.exe",
                    "Muhun MCSV Manager 0.4.7.exe",
                    "Muhun MCSV Manager 0.4.6.exe",
                    "Muhun MCSV Manager 0.4.5.exe",
                    "Muhun MCSV Manager 0.4.4.exe",
                    "Muhun MCSV Manager 0.4.3.exe",
                    "Muhun MCSV Manager 0.4.2.exe",
                    "Muhun MCSV Manager 0.4.1.exe",
                    "Muhun MCSV Manager 0.4.0.exe",
                    "Muhun MCSV Manager 0.3.1.exe",
                    "Muhun MCSV Manager 0.3.0.exe",
                    "Muhun MCSV Manager 0.2.5.exe",
                    "MinecraftServerManager.exe"
                ],
            });

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(
            ["server.properties"],
            archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [Fact]
    public async Task CreateBackupAsync_AlwaysExcludesServerDirectoryLockFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = temporaryDirectory.Path;
        await File.WriteAllTextAsync(
            Path.Combine(source, ".minecraft-server-manager.lock"),
            "manager-lock");
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "motd=test");

        var result = await new BackupService().CreateBackupAsync(
            source,
            "lock-exclusion",
            new BackupOptions
            {
                ArchiveFileName = "lock-exclusion.zip",
                ExcludedFileNames = [],
            });

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(
            ["server.properties"],
            archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [Fact]
    public async Task CreateBackupAsync_ExcludesConfiguredSensitiveFileNameAndTemporaryPrefix()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = temporaryDirectory.Path;
        await File.WriteAllTextAsync(Path.Combine(source, "remote-security.dat"), "vault");
        await File.WriteAllTextAsync(
            Path.Combine(source, ".remote-security.dat.0123456789abcdef.tmp"),
            "temporary-vault");
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "motd=test");

        var result = await new BackupService().CreateBackupAsync(
            source,
            "secret-exclusion",
            new BackupOptions
            {
                ArchiveFileName = "secret-exclusion.zip",
                ExcludedFileNames = ["remote-security.dat"],
                ExcludedFileNamePrefixes = [".remote-security.dat."]
            });

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(
            ["server.properties"],
            archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [Fact]
    public async Task CreateBackupAsync_RejectsArchivePathTraversal()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var service = new BackupService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateBackupAsync(
            temporaryDirectory.Path,
            "test",
            new BackupOptions { ArchiveFileName = "../outside.zip" }));
    }

    [Fact]
    public async Task CreateBackupAsync_DefaultRecoveryPolicyRejectsLinkedDirectoryInsteadOfSilentlyOmittingIt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = Path.Combine(temporaryDirectory.Path, "server");
        var outside = Path.Combine(temporaryDirectory.Path, "outside-world");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "level.dat"), "world");
        var linkedWorld = Path.Combine(source, "world");
        ReparsePointTestHelper.CreateDirectoryLink(linkedWorld, outside);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new BackupService().CreateBackupAsync(
                    source,
                    "linked-world",
                    new BackupOptions { ArchiveFileName = "must-not-publish.zip" }));

            Assert.Contains("不完整恢復點", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(source, "backups", "must-not-publish.zip")));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(source, "backups"), "*.partial"));
        }
        finally
        {
            Directory.Delete(linkedWorld);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_RootRelativeAllowlistIncludesOnlyExplicitFilesAndTrees()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = temporaryDirectory.Path;
        Directory.CreateDirectory(Path.Combine(source, "world", "playerdata"));
        Directory.CreateDirectory(Path.Combine(source, "config"));
        Directory.CreateDirectory(Path.Combine(source, "libraries"));
        await File.WriteAllTextAsync(Path.Combine(source, "world", "level.dat"), "world");
        await File.WriteAllTextAsync(Path.Combine(source, "world", "playerdata", "player.dat"), "player");
        await File.WriteAllTextAsync(Path.Combine(source, "config", "pack.toml"), "config");
        await File.WriteAllTextAsync(Path.Combine(source, "libraries", "loader.jar"), "core");
        await File.WriteAllTextAsync(Path.Combine(source, "server.jar"), "core");

        var result = await new BackupService().CreateBackupAsync(
            source,
            "selected-data",
            new BackupOptions
            {
                ArchiveFileName = "selected-data.zip",
                IncludedRelativePaths = ["world", "config/pack.toml"]
            });

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(
            ["config/pack.toml", "world/level.dat", "world/playerdata/player.dat"],
            archive.Entries.Select(static entry => entry.FullName).Order().ToArray());
    }

    [Fact]
    public async Task CreateBackupAsync_OverlappingAllowlistEntriesDoNotCreateDuplicateZipEntries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = temporaryDirectory.Path;
        Directory.CreateDirectory(Path.Combine(source, "world", "region"));
        await File.WriteAllTextAsync(Path.Combine(source, "world", "level.dat"), "world");
        await File.WriteAllTextAsync(Path.Combine(source, "world", "region", "r.0.0.mca"), "region");

        var result = await new BackupService().CreateBackupAsync(
            source,
            "overlap",
            new BackupOptions
            {
                ArchiveFileName = "overlap.zip",
                IncludedRelativePaths = ["world", "world/region", "world/level.dat"]
            });

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal(
            archive.Entries.Count,
            archive.Entries.Select(static entry => entry.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public async Task CreateBackupAsync_EmptyAllowlistDoesNotFallBackToWholeRoot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "server.jar"), "must-not-copy");

        var result = await new BackupService().CreateBackupAsync(
            temporaryDirectory.Path,
            "empty",
            new BackupOptions
            {
                ArchiveFileName = "empty.zip",
                IncludedRelativePaths = []
            });

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Empty(archive.Entries);
        Assert.Equal(0, result.FileCount);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("world/../libraries")]
    [InlineData("./world")]
    [InlineData("world//region")]
    [InlineData("world/.. ")]
    [InlineData("world./region")]
    public async Task CreateBackupAsync_RejectsUnsafeAllowlistPathWithoutPublishingArchive(string path)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = temporaryDirectory.Path;

        await Assert.ThrowsAsync<ArgumentException>(() => new BackupService().CreateBackupAsync(
            source,
            "unsafe",
            new BackupOptions
            {
                ArchiveFileName = "unsafe.zip",
                IncludedRelativePaths = [path]
            }));

        Assert.False(File.Exists(Path.Combine(source, "backups", "unsafe.zip")));
    }

    [Fact]
    public async Task CreateBackupAsync_RejectsRootedAllowlistPathEvenWhenItExists()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = Path.Combine(temporaryDirectory.Path, "server");
        var outside = Path.Combine(temporaryDirectory.Path, "outside.dat");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(outside, "outside");

        await Assert.ThrowsAsync<ArgumentException>(() => new BackupService().CreateBackupAsync(
            source,
            "unsafe-rooted",
            new BackupOptions
            {
                ArchiveFileName = "unsafe-rooted.zip",
                IncludedRelativePaths = [outside]
            }));
    }

    [Fact]
    public async Task CreateBackupAsync_AllowlistedDescendantCannotTraverseReparseAncestor()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = Path.Combine(temporaryDirectory.Path, "server");
        var outside = Path.Combine(temporaryDirectory.Path, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "level.dat"), "outside-world");
        var linkedDirectory = Path.Combine(source, "linked-world");
        ReparsePointTestHelper.CreateDirectoryLink(linkedDirectory, outside);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new BackupService().CreateBackupAsync(
                    source,
                    "unsafe-link",
                    new BackupOptions
                    {
                        ArchiveFileName = "unsafe-link.zip",
                        IncludedRelativePaths = ["linked-world/level.dat"]
                    }));

            Assert.False(File.Exists(Path.Combine(source, "backups", "unsafe-link.zip")));
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }
}
