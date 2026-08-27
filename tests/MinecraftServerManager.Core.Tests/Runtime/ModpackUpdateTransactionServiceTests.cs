using System.IO.Compression;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class ModpackUpdateTransactionServiceTests
{
    [Fact]
    public async Task CommitAsync_ReplacesInstallerFilesPreservesLiveDataThenAcknowledgesCleanup()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var service = new ModpackUpdateTransactionService();

        var result = await service.CommitAsync(fixture.Live, fixture.Candidate);

        Assert.True(result.CleanupPending);
        Assert.Equal("1.6.0", result.PreviousLaunchFields.ModpackVersionName);
        Assert.Equal(fixture.LivePath("old-core.jar"), result.PreviousLaunchFields.ServerJarPath);
        Assert.Equal("new-mod", await fixture.ReadLiveAsync("mods/new.jar"));
        Assert.False(File.Exists(fixture.LivePath("mods/old.jar")));
        Assert.Equal("new-config", await fixture.ReadLiveAsync("config/pack.toml"));
        Assert.Equal("new-library", await fixture.ReadLiveAsync("libraries/new-lib.jar"));
        Assert.Equal("new-core", await fixture.ReadLiveAsync("new-core.jar"));
        Assert.False(File.Exists(fixture.LivePath("old-core.jar")));

        await fixture.AssertPreservedLiveDataAsync();
        Assert.Equal("unknown-live-data", await fixture.ReadLiveAsync("custom-user-file.txt"));
        Assert.True(Directory.Exists(fixture.CandidateRoot));
        Assert.NotEmpty(fixture.TransactionArtifacts());

        var originalPort = fixture.Live.Port;
        var originalMemory = fixture.Live.MaximumMemoryMb;
        result.LaunchFields.ApplyTo(fixture.Live);
        Assert.Equal(fixture.LivePath("new-core.jar"), fixture.Live.ServerJarPath);
        Assert.Equal("1.7.0", fixture.Live.ModpackVersionName);
        Assert.Equal(originalPort, fixture.Live.Port);
        Assert.Equal(originalMemory, fixture.Live.MaximumMemoryMb);

        await service.AcknowledgeCommitAsync(fixture.Live, result.TransactionId);

        Assert.False(Directory.Exists(fixture.CandidateRoot));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RollbackCommittedAsync_RevertsFilesystemWhenSettingsPersistenceFails()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var service = new ModpackUpdateTransactionService();
        var result = await service.CommitAsync(fixture.Live, fixture.Candidate);

        await service.RollbackCommittedAsync(fixture.Live, result.TransactionId);

        await fixture.AssertOriginalLiveInstallerFilesAsync();
        await fixture.AssertPreservedLiveDataAsync();
        Assert.Equal("new-core", await fixture.ReadCandidateAsync("new-core.jar"));
        Assert.Equal("new-mod", await fixture.ReadCandidateAsync("mods/new.jar"));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RollbackCommittedAsync_PersistsIntentBeforeLiveProcessLockIsReleased()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var service = new ModpackUpdateTransactionService();
        var result = await service.CommitAsync(fixture.Live, fixture.Candidate);
        var runningLease = ServerDirectoryLease.Acquire(fixture.LiveRoot);
        try
        {
            await Assert.ThrowsAsync<ServerDirectoryLockException>(() =>
                service.RollbackCommittedAsync(fixture.Live, result.TransactionId));
        }
        finally
        {
            await runningLease.DisposeAsync();
        }

        // The first attempt already changed the journal to RollingBack. A retry must continue
        // that durable intent rather than treating it as a different or invalid transaction.
        await service.RollbackCommittedAsync(fixture.Live, result.TransactionId);

        await fixture.AssertOriginalLiveInstallerFilesAsync();
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task CommitAsync_OrdinaryInjectedFailureAutomaticallyRollsBack()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var injected = false;
        var service = new ModpackUpdateTransactionService(
            new MinecraftWorldLayoutResolver(),
            (point, _) =>
            {
                if (!injected && point == ModpackUpdateFaultPoint.CandidateEntryMoved)
                {
                    injected = true;
                    throw new IOException("injected move failure");
                }
            });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            service.CommitAsync(fixture.Live, fixture.Candidate));

        Assert.Contains("injected", error.Message, StringComparison.Ordinal);
        await fixture.AssertOriginalLiveInstallerFilesAsync();
        await fixture.AssertPreservedLiveDataAsync();
        Assert.Equal("new-mod", await fixture.ReadCandidateAsync("mods/new.jar"));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task CommitAsync_OrdinaryFailureAfterCommittedJournalStillAutomaticallyRollsBack()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var service = new ModpackUpdateTransactionService(
            new MinecraftWorldLayoutResolver(),
            (point, _) =>
            {
                if (point == ModpackUpdateFaultPoint.CommitMarked)
                {
                    throw new IOException("injected final-boundary failure");
                }
            });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            service.CommitAsync(fixture.Live, fixture.Candidate));

        Assert.Contains("final-boundary", error.Message, StringComparison.Ordinal);
        await fixture.AssertOriginalLiveInstallerFilesAsync();
        await fixture.AssertPreservedLiveDataAsync();
        Assert.Equal("new-core", await fixture.ReadCandidateAsync("new-core.jar"));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task CommitAsync_CancellationAfterFirstRenameRollsBackWithNonCancelableRecovery()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        var service = new ModpackUpdateTransactionService(
            new MinecraftWorldLayoutResolver(),
            (point, _) =>
            {
                if (!cancelled && point == ModpackUpdateFaultPoint.LiveEntryMoved)
                {
                    cancelled = true;
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CommitAsync(fixture.Live, fixture.Candidate, cancellation.Token));

        await fixture.AssertOriginalLiveInstallerFilesAsync();
        await fixture.AssertPreservedLiveDataAsync();
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RecoverPendingAsync_ApplyingCrashRollsBackWithoutGuessingMoveCount()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var crashed = false;
        var crashingService = new ModpackUpdateTransactionService(
            new MinecraftWorldLayoutResolver(),
            (point, _) =>
            {
                if (!crashed && point == ModpackUpdateFaultPoint.CandidateEntryMoved)
                {
                    crashed = true;
                    throw new ModpackUpdateSimulatedCrashException("simulated process loss");
                }
            });

        await Assert.ThrowsAsync<ModpackUpdateSimulatedCrashException>(() =>
            crashingService.CommitAsync(fixture.Live, fixture.Candidate));
        Assert.NotEmpty(fixture.TransactionArtifacts());

        var recovery = await new ModpackUpdateTransactionService()
            .RecoverPendingAsync(fixture.Live);

        Assert.Equal(ModpackUpdateRecoveryAction.RolledBack, recovery.Action);
        await fixture.AssertOriginalLiveInstallerFilesAsync();
        await fixture.AssertPreservedLiveDataAsync();
        Assert.Equal("new-core", await fixture.ReadCandidateAsync("new-core.jar"));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RecoverPendingAsync_CommittedCrashReturnsFieldsUntilCallerAcknowledges()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var crashingService = new ModpackUpdateTransactionService(
            new MinecraftWorldLayoutResolver(),
            (point, _) =>
            {
                if (point == ModpackUpdateFaultPoint.CommitMarked)
                {
                    throw new ModpackUpdateSimulatedCrashException("crash after durable commit");
                }
            });

        await Assert.ThrowsAsync<ModpackUpdateSimulatedCrashException>(() =>
            crashingService.CommitAsync(fixture.Live, fixture.Candidate));

        var recoveryService = new ModpackUpdateTransactionService();
        var recovery = await recoveryService.RecoverPendingAsync(fixture.Live);

        Assert.Equal(
            ModpackUpdateRecoveryAction.CommittedAwaitingAcknowledgement,
            recovery.Action);
        Assert.NotNull(recovery.TransactionId);
        Assert.NotNull(recovery.LaunchFields);
        Assert.NotNull(recovery.PreviousLaunchFields);
        Assert.True(recovery.CleanupPending);
        Assert.Equal("1.6.0", recovery.PreviousLaunchFields.ModpackVersionName);
        Assert.Equal(
            fixture.LivePath("old-core.jar"),
            recovery.PreviousLaunchFields.ServerJarPath);
        recovery.LaunchFields.ApplyTo(fixture.Live);
        Assert.Equal("1.7.0", fixture.Live.ModpackVersionName);
        Assert.Equal("new-core", await fixture.ReadLiveAsync("new-core.jar"));

        await recoveryService.AcknowledgeCommitAsync(
            fixture.Live,
            recovery.TransactionId.Value);

        Assert.False(Directory.Exists(fixture.CandidateRoot));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task CommitAsync_BackupCallbackRunsInsideBothLeasesBeforeJournalOrMove()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var callbackRan = false;
        string? backupPath = null;
        var service = new ModpackUpdateTransactionService();

        var result = await service.CommitAsync(
            fixture.Live,
            fixture.Candidate,
            async cancellationToken =>
            {
                callbackRan = true;
                Assert.Throws<ServerDirectoryLockException>(() =>
                    ServerDirectoryLease.Acquire(fixture.LiveRoot));
                Assert.Throws<ServerDirectoryLockException>(() =>
                    ServerDirectoryLease.Acquire(fixture.CandidateRoot));
                Assert.Equal("old-mod", File.ReadAllText(fixture.LivePath("mods/old.jar")));
                Assert.Empty(fixture.TransactionArtifacts());
                var plan = await new ModpackUpdateBackupPlanner().CreatePlanAsync(
                    fixture.Live,
                    fixture.Candidate.ModpackVersionName!,
                    cancellationToken);
                var backup = await new BackupService().CreateBackupAsync(
                    fixture.Live,
                    plan.Options,
                    cancellationToken: cancellationToken);
                backupPath = backup.ArchivePath;
            },
            CancellationToken.None);

        Assert.True(callbackRan);
        Assert.NotNull(backupPath);
        Assert.True(File.Exists(backupPath));
        using (var archive = ZipFile.OpenRead(backupPath))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "mods/old.jar");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "old-core.jar");
        }

        await service.AcknowledgeCommitAsync(fixture.Live, result.TransactionId);
    }

    [Fact]
    public async Task CommitAsync_BackupCallbackFailureLeavesNoTransactionArtifactsOrMoves()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var service = new ModpackUpdateTransactionService();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CommitAsync(
                fixture.Live,
                fixture.Candidate,
                _ => throw new InvalidOperationException("backup failed"),
                CancellationToken.None));

        Assert.Contains("backup failed", error.Message, StringComparison.Ordinal);
        await fixture.AssertOriginalLiveInstallerFilesAsync();
        Assert.Equal("new-core", await fixture.ReadCandidateAsync("new-core.jar"));
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task CommitAsync_RejectsSameOrNestedPhysicalDirectory()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var same = fixture.CreateCandidateModel(fixture.LiveRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ModpackUpdateTransactionService().CommitAsync(fixture.Live, same));
    }

    [Fact]
    public async Task CommitAsync_RejectsReparseCandidateRoot()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        var link = Path.Combine(fixture.ParentRoot, "candidate-link");
        ReparsePointTestHelper.CreateDirectoryLink(link, fixture.CandidateRoot);
        try
        {
            var linkedCandidate = fixture.CreateCandidateModel(link);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                new ModpackUpdateTransactionService().CommitAsync(
                    fixture.Live,
                    linkedCandidate));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task CommitAsync_RejectsWorldInsideInstallerOwnedDirectory()
    {
        using var fixture = await TransactionFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.LivePath("server.properties"),
            "level-name=config\n");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModpackUpdateTransactionService().CommitAsync(
                fixture.Live,
                fixture.Candidate));

        Assert.Contains("世界路徑衝突", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.TransactionArtifacts());
    }

    private sealed class TransactionFixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary;

        private TransactionFixture(TemporaryDirectory temporary)
        {
            _temporary = temporary;
            ParentRoot = temporary.Path;
            LiveRoot = Path.Combine(ParentRoot, "live");
            CandidateRoot = Path.Combine(ParentRoot, "candidate");
            Live = new ServerInstance
            {
                Name = "Live Pack",
                DirectoryPath = LiveRoot,
                ServerJarPath = LivePath("old-core.jar"),
                LaunchKind = ServerLaunchKind.ExecutableJar,
                SourceLaunchScriptPath = LivePath("run.bat"),
                CoreType = CoreType.Forge,
                MinecraftVersion = "1.20.1",
                JavaMajorVersion = 17,
                MaximumMemoryMb = 6144,
                Port = 25570,
                ModpackSource = ModpackSourceKind.Modrinth,
                ModpackProjectId = "project",
                ModpackVersionId = "old-version",
                ModpackVersionName = "1.6.0"
            };
            Candidate = CreateCandidateModel(CandidateRoot);
        }

        public string ParentRoot { get; }

        public string LiveRoot { get; }

        public string CandidateRoot { get; }

        public ServerInstance Live { get; }

        public ServerInstance Candidate { get; }

        public static async Task<TransactionFixture> CreateAsync()
        {
            var fixture = new TransactionFixture(new TemporaryDirectory());
            Directory.CreateDirectory(fixture.LiveRoot);
            Directory.CreateDirectory(fixture.CandidateRoot);

            await fixture.WriteLiveAsync("mods/old.jar", "old-mod");
            await fixture.WriteLiveAsync("plugins/old.jar", "old-plugin");
            await fixture.WriteLiveAsync("config/pack.toml", "old-config");
            await fixture.WriteLiveAsync("defaultconfigs/default.toml", "old-default");
            await fixture.WriteLiveAsync("kubejs/server_scripts/main.js", "old-kubejs");
            await fixture.WriteLiveAsync("scripts/recipe.zs", "old-script");
            await fixture.WriteLiveAsync("libraries/old-lib.jar", "old-library");
            await fixture.WriteLiveAsync("versions/old/version.jar", "old-version");
            await fixture.WriteLiveAsync("old-core.jar", "old-core");
            await fixture.WriteLiveAsync("run.bat", "old-run");

            await fixture.WriteLiveAsync("world/level.dat", "live-world");
            await fixture.WriteLiveAsync("world/playerdata/player.dat", "live-player");
            await fixture.WriteLiveAsync("world_nether/level.dat", "live-nether");
            await fixture.WriteLiveAsync("world_the_end/level.dat", "live-end");
            await fixture.WriteLiveAsync("ops.json", "live-ops");
            await fixture.WriteLiveAsync("whitelist.json", "live-whitelist");
            await fixture.WriteLiveAsync("server.properties", "level-name=world\nserver-port=25570\n");
            await fixture.WriteLiveAsync("eula.txt", "eula=true");
            await fixture.WriteLiveAsync("user_jvm_args.txt", "-Xmx6G");
            await fixture.WriteLiveAsync("logs/latest.log", "live-log");
            await fixture.WriteLiveAsync("crash-reports/crash.txt", "live-crash");
            await fixture.WriteLiveAsync("backups/recovery.zip", "live-backup");
            await fixture.WriteLiveAsync("cache/download.tmp", "live-cache");
            await fixture.WriteLiveAsync(".mcsv-runtime/memory.args", "live-runtime");
            await fixture.WriteLiveAsync("custom-user-file.txt", "unknown-live-data");

            await fixture.WriteCandidateAsync("mods/new.jar", "new-mod");
            await fixture.WriteCandidateAsync("plugins/new.jar", "new-plugin");
            await fixture.WriteCandidateAsync("config/pack.toml", "new-config");
            await fixture.WriteCandidateAsync("defaultconfigs/default.toml", "new-default");
            await fixture.WriteCandidateAsync("kubejs/server_scripts/main.js", "new-kubejs");
            await fixture.WriteCandidateAsync("scripts/recipe.zs", "new-script");
            await fixture.WriteCandidateAsync("libraries/new-lib.jar", "new-library");
            await fixture.WriteCandidateAsync("versions/new/version.jar", "new-version");
            await fixture.WriteCandidateAsync("new-core.jar", "new-core");
            await fixture.WriteCandidateAsync("run.bat", "new-run");

            await fixture.WriteCandidateAsync("world/level.dat", "candidate-world-must-not-win");
            await fixture.WriteCandidateAsync("ops.json", "candidate-ops-must-not-win");
            await fixture.WriteCandidateAsync(
                "server.properties",
                "level-name=candidate-world\nserver-port=25565\n");
            await fixture.WriteCandidateAsync("eula.txt", "eula=false");
            await fixture.WriteCandidateAsync("user_jvm_args.txt", "-Xmx2G");
            await fixture.WriteCandidateAsync("logs/latest.log", "candidate-log");
            await fixture.WriteCandidateAsync("cache/download.tmp", "candidate-cache");
            return fixture;
        }

        public ServerInstance CreateCandidateModel(string root) => new()
        {
            Name = "Candidate Pack",
            DirectoryPath = root,
            ServerJarPath = Path.Combine(root, "new-core.jar"),
            LaunchKind = ServerLaunchKind.ExecutableJar,
            SourceLaunchScriptPath = Path.Combine(root, "run.bat"),
            CoreType = CoreType.NeoForge,
            MinecraftVersion = "1.21.1",
            JavaMajorVersion = 21,
            ServerArguments = ["nogui", "--candidate"],
            ModpackSource = ModpackSourceKind.Modrinth,
            ModpackProjectId = "project",
            ModpackVersionId = "new-version",
            ModpackVersionName = "1.7.0"
        };

        public string LivePath(string relativePath) => Combine(LiveRoot, relativePath);

        public string CandidatePath(string relativePath) => Combine(CandidateRoot, relativePath);

        public Task<string> ReadLiveAsync(string relativePath)
            => File.ReadAllTextAsync(LivePath(relativePath));

        public Task<string> ReadCandidateAsync(string relativePath)
            => File.ReadAllTextAsync(CandidatePath(relativePath));

        public async Task AssertOriginalLiveInstallerFilesAsync()
        {
            Assert.Equal("old-mod", await ReadLiveAsync("mods/old.jar"));
            Assert.Equal("old-config", await ReadLiveAsync("config/pack.toml"));
            Assert.Equal("old-library", await ReadLiveAsync("libraries/old-lib.jar"));
            Assert.Equal("old-core", await ReadLiveAsync("old-core.jar"));
            Assert.Equal("old-run", await ReadLiveAsync("run.bat"));
            Assert.False(File.Exists(LivePath("new-core.jar")));
        }

        public async Task AssertPreservedLiveDataAsync()
        {
            Assert.Equal("live-world", await ReadLiveAsync("world/level.dat"));
            Assert.Equal("live-player", await ReadLiveAsync("world/playerdata/player.dat"));
            Assert.Equal("live-nether", await ReadLiveAsync("world_nether/level.dat"));
            Assert.Equal("live-end", await ReadLiveAsync("world_the_end/level.dat"));
            Assert.Equal("live-ops", await ReadLiveAsync("ops.json"));
            Assert.Equal("live-whitelist", await ReadLiveAsync("whitelist.json"));
            Assert.Contains("25570", await ReadLiveAsync("server.properties"), StringComparison.Ordinal);
            Assert.Equal("eula=true", await ReadLiveAsync("eula.txt"));
            Assert.Equal("-Xmx6G", await ReadLiveAsync("user_jvm_args.txt"));
            Assert.Equal("live-log", await ReadLiveAsync("logs/latest.log"));
            Assert.Equal("live-crash", await ReadLiveAsync("crash-reports/crash.txt"));
            Assert.Equal("live-backup", await ReadLiveAsync("backups/recovery.zip"));
            Assert.Equal("live-cache", await ReadLiveAsync("cache/download.tmp"));
            Assert.Equal("live-runtime", await ReadLiveAsync(".mcsv-runtime/memory.args"));
            Assert.True(File.Exists(LivePath(".minecraft-server-manager.lock")));
        }

        public string[] TransactionArtifacts()
            => Directory.EnumerateFileSystemEntries(
                    ParentRoot,
                    ".mcsv-modpack-update-*",
                    SearchOption.TopDirectoryOnly)
                .ToArray();

        private Task WriteLiveAsync(string relativePath, string contents)
            => WriteAsync(LiveRoot, relativePath, contents);

        private Task WriteCandidateAsync(string relativePath, string contents)
            => WriteAsync(CandidateRoot, relativePath, contents);

        private static async Task WriteAsync(string root, string relativePath, string contents)
        {
            var path = Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, contents);
        }

        private static string Combine(string root, string relativePath)
            => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose() => _temporary.Dispose();
    }
}
