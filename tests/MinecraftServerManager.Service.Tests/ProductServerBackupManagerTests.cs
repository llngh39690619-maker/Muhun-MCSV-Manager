using System.IO.Compression;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Data;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerBackupManagerTests
{
    [Fact]
    public async Task CreateAndList_ExposeOpaqueMetadataWithoutAServicePath()
    {
        await using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.ServerDirectory, "world.dat"), "before");

        var created = await fixture.Backups.CreateAsync(fixture.Registration.Id);
        var listed = fixture.Backups.List(fixture.Registration.Id, 0, 50);

        var backup = Assert.Single(listed.Backups);
        Assert.Equal(created.Backup.BackupId, backup.BackupId);
        Assert.Equal(64, backup.BackupId.Length);
        Assert.DoesNotContain(fixture.Layout.Root, backup.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", backup.BackupId, StringComparison.Ordinal);
        Assert.False(listed.HasMore);
    }

    [Fact]
    public async Task Restore_ReplacesStoppedTreeAndCreatesPreRestoreSafetyZip()
    {
        await using var fixture = await Fixture.CreateAsync();
        var world = Path.Combine(fixture.ServerDirectory, "world.dat");
        File.WriteAllText(world, "old-world");
        var original = await fixture.Backups.CreateAsync(fixture.Registration.Id);
        File.WriteAllText(world, "new-world");

        var restored = await fixture.Backups.RestoreAsync(
            fixture.Registration.Id,
            original.Backup.BackupId);

        Assert.Equal(original.Backup.BackupId, restored.BackupId);
        Assert.Equal("old-world", File.ReadAllText(world));
        Assert.True(File.Exists(Path.Combine(fixture.ServerDirectory, "server.jar")));
        Assert.Contains(
            fixture.Backups.List(fixture.Registration.Id, 0, 50).Backups,
            backup => backup.FileName.StartsWith("pre-restore-", StringComparison.OrdinalIgnoreCase));
        var stored = fixture.Runtime.GetRegistration(fixture.Registration.Id);
        Assert.Equal(fixture.Registration.Id, stored.Id);
        Assert.Equal(fixture.Registration.ServerDirectory, stored.ServerDirectory);
    }

    [Fact]
    public async Task Restore_OnlyAcceptsAnIdFromTheServiceOwnedCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var world = Path.Combine(fixture.ServerDirectory, "world.dat");
        File.WriteAllText(world, "unchanged");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Backups.RestoreAsync(
            fixture.Registration.Id,
            new string('a', 64)));

        Assert.Equal("unchanged", File.ReadAllText(world));
    }

    [Fact]
    public async Task Restore_RejectsBackupMissingConfiguredLaunchFileBeforeSwap()
    {
        await using var fixture = await Fixture.CreateAsync();
        var world = Path.Combine(fixture.ServerDirectory, "world.dat");
        File.WriteAllText(world, "current");
        var backupDirectory = Path.Combine(
            fixture.Layout.Backups,
            fixture.Registration.Id.ToString("D"));
        Directory.CreateDirectory(backupDirectory);
        var archivePath = Path.Combine(backupDirectory, "malformed.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("world.dat");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("malformed");
        }
        var malformed = Assert.Single(fixture.Backups.List(
            fixture.Registration.Id,
            0,
            50).Backups);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Backups.RestoreAsync(
            fixture.Registration.Id,
            malformed.BackupId));

        Assert.Equal("current", File.ReadAllText(world));
        Assert.True(File.Exists(Path.Combine(fixture.ServerDirectory, "server.jar")));
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.Layout.Servers,
            $".{Path.GetFileName(fixture.ServerDirectory)}.restore-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task BackupMutations_FailClosedWhileServerIsRunning()
    {
        await using var fixture = await Fixture.CreateAsync();
        var backup = await fixture.Backups.CreateAsync(fixture.Registration.Id);
        await fixture.Runtime.StartAsync(fixture.Registration.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Backups.CreateAsync(fixture.Registration.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Backups.RestoreAsync(
                fixture.Registration.Id,
                backup.Backup.BackupId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.UpsertAsync(
            fixture.Registration with { Name = "must-not-change" }));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Runtime.RemoveAsync(fixture.Registration.Id));
        Assert.Equal(fixture.Registration.Name, fixture.Runtime.GetRegistration(fixture.Registration.Id).Name);
    }

    [Fact]
    public async Task RemoteBackend_ListsPathFreeServiceBackupsAndRestoresOnlyWhileStopped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var world = Path.Combine(fixture.ServerDirectory, "world.dat");
        File.WriteAllText(world, "catalog-world");
        var created = await fixture.Backups.CreateAsync(fixture.Registration.Id);
        File.WriteAllText(world, "changed-world");

        var listed = await fixture.RemoteBackend.GetBackupsAsync(
            fixture.Registration.Id.ToString("D"),
            CancellationToken.None);

        Assert.NotNull(listed);
        var remote = Assert.Single(listed.Backups);
        Assert.Equal(created.Backup.BackupId, remote.BackupId);
        Assert.Equal(64, remote.BackupId.Length);
        Assert.DoesNotContain(fixture.Layout.Root, remote.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", remote.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("/", remote.DisplayName, StringComparison.Ordinal);

        var restored = await fixture.RemoteBackend.RestoreBackupAsync(
            fixture.Registration.Id.ToString("D"),
            remote.BackupId,
            CancellationToken.None);
        Assert.True(restored.Accepted);
        Assert.Equal("catalog-world", File.ReadAllText(world));

        await fixture.Runtime.StartAsync(fixture.Registration.Id);
        File.WriteAllText(world, "running-world");
        var rejected = await fixture.RemoteBackend.RestoreBackupAsync(
            fixture.Registration.Id.ToString("D"),
            remote.BackupId,
            CancellationToken.None);
        Assert.False(rejected.Accepted);
        Assert.Equal("running-world", File.ReadAllText(world));
    }

    [Fact]
    public void RemoteBackupProjection_ReplacesPathLikeDisplayNames()
    {
        var projected = ProductRemoteControlBackend.MapBackup(new ProductServerBackupSummary(
            new string('a', 64),
            @"C:\service\backups\secret.zip",
            100,
            DateTimeOffset.Parse("2026-08-27T01:02:03Z")));

        Assert.Equal(new string('a', 64), projected.BackupId);
        Assert.Equal("backup-20260827-010203.zip", projected.DisplayName);
    }

    [Fact]
    public async Task Mutations_PublishSuccessRestoreAndSanitizedFailureDomainEvents()
    {
        await using var fixture = await Fixture.CreateAsync();
        var database = new ProductDatabase(Path.Combine(fixture.Layout.Data, "notifications.db"));
        await database.InitializeAsync();
        var vault = new MemoryProductSecretVault();
        var discord = new ProductDiscordWebhookSettings(
            vault,
            new ProductNotificationSecretResolver(vault));
        var outbox = new NotificationOutboxStore(database);
        var publisher = new ProductNotificationPublisher(
            new ProductSequenceStore(database),
            outbox,
            discord,
            new ProductNotificationPreferenceStore(fixture.Layout));
        var backups = new ProductServerBackupManager(
            fixture.Layout,
            fixture.Runtime,
            new BackupService(),
            publisher,
            TimeProvider.System);
        File.WriteAllText(Path.Combine(fixture.ServerDirectory, "world.dat"), "one");

        var created = await backups.CreateAsync(fixture.Registration.Id);
        File.WriteAllText(Path.Combine(fixture.ServerDirectory, "world.dat"), "two");
        await backups.RestoreAsync(fixture.Registration.Id, created.Backup.BackupId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => backups.RestoreAsync(
            fixture.Registration.Id,
            new string('f', 64)));

        var eventTypes = await ReadEventTypesAsync(database.DatabasePath);
        Assert.Contains("backup.completed", eventTypes);
        Assert.Contains("backup.restored", eventTypes);
        Assert.Contains("backup.failed", eventTypes);
    }

    private static async Task<IReadOnlyList<string>> ReadEventTypesAsync(string databasePath)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type FROM notification_events ORDER BY sequence;";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            ProductDataLayout layout,
            ProductServerRegistration registration,
            ProductServerRegistry registry,
            ServerProcessManager processManager,
            ProductServerRuntime runtime)
        {
            Layout = layout;
            Registration = registration;
            Runtime = runtime;
            Backups = new ProductServerBackupManager(layout, runtime, new BackupService());
            PlayerTracker = new ProductPlayerPresenceTracker(
                processManager,
                registry,
                TimeProvider.System);
            RemoteBackend = new ProductRemoteControlBackend(
                runtime,
                registry,
                PlayerTracker,
                Backups);
            ServerDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
        }

        public ProductDataLayout Layout { get; }
        public ProductServerRegistration Registration { get; }
        public ProductServerRuntime Runtime { get; }
        public ProductServerBackupManager Backups { get; }
        public ProductPlayerPresenceTracker PlayerTracker { get; }
        public ProductRemoteControlBackend RemoteBackend { get; }
        public string ServerDirectory { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var layout = ProductServerRegistryTests.CreateLayout();
            var registration = ProductServerRegistryTests.Registration();
            var serverDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
            var javaPath = Path.Combine(
                layout.Runtimes,
                registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(serverDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
            File.WriteAllText(Path.Combine(serverDirectory, "server.jar"), "server-core");
            File.WriteAllText(javaPath, "java");

            var registry = new ProductServerRegistry(layout);
            await registry.LoadAsync();
            await registry.UpsertAsync(registration);
            var manager = new ServerProcessManager(
                new ServerProcessManagerOptions
                {
                    ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                    GracefulStopTimeout = TimeSpan.FromMilliseconds(250),
                    ForcedKillWaitTimeout = TimeSpan.FromMilliseconds(250),
                    MonitorDrainTimeout = TimeSpan.FromMilliseconds(250),
                },
                new ProductServerTestProcessFactory());
            var runtime = new ProductServerRuntime(
                registry,
                layout,
                manager,
                new ProductDesiredRunIntentStore(layout));
            return new Fixture(layout, registration, registry, manager, runtime);
        }

        public async ValueTask DisposeAsync()
        {
            RemoteBackend.Dispose();
            PlayerTracker.Dispose();
            await Runtime.DisposeAsync();
        }
    }
}
