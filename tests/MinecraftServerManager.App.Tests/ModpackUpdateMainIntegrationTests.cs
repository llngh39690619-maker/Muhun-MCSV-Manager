using System.IO;
using System.Security;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ModpackUpdateMainIntegrationTests
{
    [Fact]
    public async Task InitializeAsync_MissingLegacyDirectoryWithoutUpdateArtifactsStillLoadsRecord()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var missingRoot = Path.Combine(paths.Servers, "missing-legacy-pack");
        var live = CreateLiveInstance(missingRoot);
        await SaveSettingsAsync(paths, live);

        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            new CandidateWorkflow(CreateCandidateInstance(Path.Combine(paths.Servers, "unused"))),
            onlineModpackDialogService: null);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        var loaded = Assert.Single(viewModel.Servers);
        Assert.Equal(live.Id, loaded.Id);
        Assert.Equal(missingRoot, loaded.DirectoryPath);
    }

    [Fact]
    public async Task InitializeAsync_CommittedUpdateWaitsForRealLaunchHealthBeforeCleanup()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var liveRoot = Path.Combine(paths.Servers, "live-pack");
        var candidateRoot = Path.Combine(paths.Servers, "startup-recovery-candidate");
        Directory.CreateDirectory(Path.Combine(liveRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(candidateRoot, "mods"));
        await File.WriteAllTextAsync(Path.Combine(liveRoot, "old-core.jar"), "old-core");
        await File.WriteAllTextAsync(Path.Combine(liveRoot, "mods", "old.jar"), "old-mod");
        await File.WriteAllTextAsync(Path.Combine(candidateRoot, "new-core.jar"), "new-core");
        await File.WriteAllTextAsync(Path.Combine(candidateRoot, "mods", "new.jar"), "new-mod");

        var live = CreateLiveInstance(liveRoot);
        var candidate = CreateCandidateInstance(candidateRoot);
        await SaveSettingsAsync(paths, live);
        var transactionService = new ModpackUpdateTransactionService();
        var transaction = await transactionService.CommitAsync(live, candidate);
        Assert.True(transactionService.HasPendingArtifacts(live));

        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            new CandidateWorkflow(candidate),
            onlineModpackDialogService: null);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        var recovered = Assert.Single(viewModel.Servers);
        Assert.Equal("new-version", recovered.Model.ModpackVersionId);
        Assert.Equal("1.7.0", recovered.Model.ModpackVersionName);
        Assert.Equal(Path.Combine(liveRoot, "new-core.jar"), recovered.Model.ServerJarPath);
        Assert.Equal("new-core", await File.ReadAllTextAsync(recovered.Model.ServerJarPath));
        Assert.True(Directory.Exists(candidateRoot));
        Assert.True(transactionService.HasPendingArtifacts(recovered.Model));
        Assert.True(viewModel.HasPendingModpackHealthValidation(recovered.Id));

        using var settingsStore = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        var persistedSettings = Assert.IsType<ManagerSettings>(await settingsStore.LoadAsync());
        var persisted = Assert.Single(persistedSettings.Instances);
        Assert.Equal("new-version", persisted.ModpackVersionId);
        Assert.Equal(Path.Combine(liveRoot, "new-core.jar"), persisted.ServerJarPath);

        var sessionId = Guid.NewGuid();
        viewModel.ObservePendingModpackHealthStateChange(new ServerStateChangedEventArgs(
            recovered.Id,
            sessionId,
            ServerState.Stopped,
            ServerState.Starting));
        viewModel.MarkPendingModpackSessionHealthy(recovered.Id, sessionId, "test Done");
        viewModel.ObservePendingModpackHealthStateChange(new ServerStateChangedEventArgs(
            recovered.Id,
            sessionId,
            ServerState.Running,
            ServerState.Stopped,
            exitCode: 0));
        await viewModel.WaitForPendingModpackHealthActionsAsync();

        Assert.False(viewModel.HasPendingModpackHealthValidation(recovered.Id));
        Assert.False(Directory.Exists(candidateRoot));
        Assert.False(transactionService.HasPendingArtifacts(recovered.Model));
    }

    [Fact]
    public async Task FirstLaunchCrashBeforeHealth_RollsBackFilesAndJournalPreviousFields()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var liveRoot = Path.Combine(paths.Servers, "live-pack");
        var candidateRoot = Path.Combine(paths.Servers, "crashing-update-candidate");
        Directory.CreateDirectory(Path.Combine(liveRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(candidateRoot, "mods"));
        await File.WriteAllTextAsync(Path.Combine(liveRoot, "old-core.jar"), "old-core");
        await File.WriteAllTextAsync(Path.Combine(liveRoot, "mods", "old.jar"), "old-mod");
        await File.WriteAllTextAsync(Path.Combine(candidateRoot, "new-core.jar"), "new-core");
        await File.WriteAllTextAsync(Path.Combine(candidateRoot, "mods", "new.jar"), "new-mod");

        var live = CreateLiveInstance(liveRoot);
        var candidate = CreateCandidateInstance(candidateRoot);
        await SaveSettingsAsync(paths, live);
        var transactionService = new ModpackUpdateTransactionService();
        _ = await transactionService.CommitAsync(live, candidate);

        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            new CandidateWorkflow(candidate),
            onlineModpackDialogService: null);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var recovered = Assert.Single(viewModel.Servers);
        Assert.Equal("new-version", recovered.Model.ModpackVersionId);

        var sessionId = Guid.NewGuid();
        viewModel.ObservePendingModpackHealthStateChange(new ServerStateChangedEventArgs(
            recovered.Id,
            sessionId,
            ServerState.Stopped,
            ServerState.Starting));
        viewModel.ObservePendingModpackHealthStateChange(new ServerStateChangedEventArgs(
            recovered.Id,
            sessionId,
            ServerState.Running,
            ServerState.Crashed,
            exitCode: 1));
        await viewModel.WaitForPendingModpackHealthActionsAsync();

        Assert.False(viewModel.HasPendingModpackHealthValidation(recovered.Id));
        Assert.False(transactionService.HasPendingArtifacts(recovered.Model));
        Assert.Equal("old-version", recovered.Model.ModpackVersionId);
        Assert.Equal("1.6.0", recovered.Model.ModpackVersionName);
        Assert.Equal(Path.Combine(liveRoot, "old-core.jar"), recovered.Model.ServerJarPath);
        Assert.Equal("old-core", await File.ReadAllTextAsync(recovered.Model.ServerJarPath));
        Assert.Equal("old-mod", await File.ReadAllTextAsync(Path.Combine(liveRoot, "mods", "old.jar")));
        Assert.False(File.Exists(Path.Combine(liveRoot, "mods", "new.jar")));

        using var settingsStore = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        var persisted = Assert.Single(
            Assert.IsType<ManagerSettings>(await settingsStore.LoadAsync()).Instances);
        Assert.Equal("old-version", persisted.ModpackVersionId);
        Assert.Equal(Path.Combine(liveRoot, "old-core.jar"), persisted.ServerJarPath);
    }

    [Fact]
    public async Task ManualStopBeforeHealth_KeepsPendingTransactionForNextLaunch()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var liveRoot = Path.Combine(paths.Servers, "live-pack");
        var candidateRoot = Path.Combine(paths.Servers, "manually-stopped-update-candidate");
        Directory.CreateDirectory(liveRoot);
        Directory.CreateDirectory(candidateRoot);
        await File.WriteAllTextAsync(Path.Combine(liveRoot, "old-core.jar"), "old-core");
        await File.WriteAllTextAsync(Path.Combine(candidateRoot, "new-core.jar"), "new-core");

        var live = CreateLiveInstance(liveRoot);
        var candidate = CreateCandidateInstance(candidateRoot);
        await SaveSettingsAsync(paths, live);
        var transactionService = new ModpackUpdateTransactionService();
        _ = await transactionService.CommitAsync(live, candidate);

        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            new CandidateWorkflow(candidate),
            onlineModpackDialogService: null);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var recovered = Assert.Single(viewModel.Servers);
        var sessionId = Guid.NewGuid();
        viewModel.ObservePendingModpackHealthStateChange(new ServerStateChangedEventArgs(
            recovered.Id,
            sessionId,
            ServerState.Stopped,
            ServerState.Starting));
        viewModel.ObservePendingModpackHealthStateChange(new ServerStateChangedEventArgs(
            recovered.Id,
            sessionId,
            ServerState.Running,
            ServerState.Stopped,
            exitCode: 0));
        await viewModel.WaitForPendingModpackHealthActionsAsync();

        Assert.True(viewModel.HasPendingModpackHealthValidation(recovered.Id));
        Assert.True(transactionService.HasPendingArtifacts(recovered.Model));
        Assert.Equal("new-version", recovered.Model.ModpackVersionId);
        Assert.Equal("new-core", await File.ReadAllTextAsync(recovered.Model.ServerJarPath));
    }

    [Fact]
    public async Task ApplyModpackUpdateAsync_RejectsCandidateDirectoryClaimedByAnotherServer()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var liveRoot = Path.Combine(paths.Servers, "live-pack");
        var claimedRoot = Path.Combine(paths.Servers, "already-managed-pack");
        Directory.CreateDirectory(liveRoot);
        Directory.CreateDirectory(claimedRoot);
        await File.WriteAllTextAsync(Path.Combine(liveRoot, "old-core.jar"), "old-core");
        await File.WriteAllTextAsync(Path.Combine(claimedRoot, "new-core.jar"), "claimed-core");
        await File.WriteAllTextAsync(Path.Combine(claimedRoot, "owner-data.txt"), "must-survive");

        var live = CreateLiveInstance(liveRoot);
        var alreadyManaged = new ServerInstance
        {
            Name = "Other managed Server",
            DirectoryPath = claimedRoot,
            ServerJarPath = Path.Combine(claimedRoot, "new-core.jar"),
            LaunchKind = ServerLaunchKind.ExecutableJar,
            CoreType = CoreType.NeoForge,
            MinecraftVersion = "1.21.1"
        };
        await SaveSettingsAsync(paths, live, alreadyManaged);

        var candidate = CreateCandidateInstance(claimedRoot);
        var workflow = new CandidateWorkflow(candidate);
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            workflow,
            onlineModpackDialogService: null);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var liveViewModel = Assert.Single(viewModel.Servers, item => item.Id == live.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.ApplyModpackUpdateAsync(
                liveViewModel,
                CreateProject(),
                CreateTargetVersion(),
                CancellationToken.None));

        Assert.Contains("已由另一個 Server 管理", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, workflow.InstallCallCount);
        Assert.True(Directory.Exists(claimedRoot));
        Assert.Equal("claimed-core", await File.ReadAllTextAsync(Path.Combine(claimedRoot, "new-core.jar")));
        Assert.Equal("must-survive", await File.ReadAllTextAsync(Path.Combine(claimedRoot, "owner-data.txt")));
        Assert.Equal("old-core", await File.ReadAllTextAsync(Path.Combine(liveRoot, "old-core.jar")));
        Assert.Equal("old-version", liveViewModel.Model.ModpackVersionId);
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            paths.Servers,
            ".mcsv-modpack-update-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ApplyModpackUpdateAsync_DeletesOwnedUnregisteredCandidateAfterValidationFailure()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var liveRoot = Path.Combine(paths.Servers, "live-pack");
        var candidateRoot = Path.Combine(paths.Servers, "owned-update-candidate");
        Directory.CreateDirectory(liveRoot);
        Directory.CreateDirectory(candidateRoot);
        await File.WriteAllTextAsync(Path.Combine(liveRoot, "old-core.jar"), "old-core");
        await File.WriteAllTextAsync(Path.Combine(candidateRoot, "new-core.jar"), "new-core");

        var live = CreateLiveInstance(liveRoot);
        await SaveSettingsAsync(paths, live);
        var invalidCandidate = CreateCandidateInstance(candidateRoot);
        invalidCandidate.ModpackVersionId = "unexpected-version";
        var workflow = new CandidateWorkflow(invalidCandidate);
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            workflow,
            onlineModpackDialogService: null);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var liveViewModel = Assert.Single(viewModel.Servers);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            viewModel.ApplyModpackUpdateAsync(
                liveViewModel,
                CreateProject(),
                CreateTargetVersion(),
                CancellationToken.None));

        Assert.False(Directory.Exists(candidateRoot));
        Assert.Equal("old-core", await File.ReadAllTextAsync(Path.Combine(liveRoot, "old-core.jar")));
        Assert.Equal("old-version", liveViewModel.Model.ModpackVersionId);
    }

    private static ServerInstance CreateLiveInstance(string root) => new()
    {
        Name = "Live Pack",
        DirectoryPath = root,
        ServerJarPath = Path.Combine(root, "old-core.jar"),
        LaunchKind = ServerLaunchKind.ExecutableJar,
        CoreType = CoreType.Forge,
        MinecraftVersion = "1.20.1",
        ModpackSource = ModpackSourceKind.Modrinth,
        ModpackProjectId = "project",
        ModpackVersionId = "old-version",
        ModpackVersionName = "1.6.0"
    };

    private static ServerInstance CreateCandidateInstance(string root) => new()
    {
        Name = "Candidate Pack",
        DirectoryPath = root,
        ServerJarPath = Path.Combine(root, "new-core.jar"),
        LaunchKind = ServerLaunchKind.ExecutableJar,
        CoreType = CoreType.NeoForge,
        MinecraftVersion = "1.21.1",
        ModpackSource = ModpackSourceKind.Modrinth,
        ModpackProjectId = "project",
        ModpackVersionId = "new-version",
        ModpackVersionName = "1.7.0"
    };

    private static OnlineModpackSearchResult CreateProject()
        => new(
            OnlineModpackProvider.Modrinth,
            "project",
            "Pack",
            "Summary",
            "Author");

    private static OnlineModpackVersion CreateTargetVersion()
        => new(
            OnlineModpackProvider.Modrinth,
            "project",
            "new-version",
            "1.7.0",
            "1.21.1",
            "NeoForge",
            "release",
            DateTimeOffset.UtcNow,
            HasOfficialServerPack: true);

    private static async Task SaveSettingsAsync(
        ApplicationPaths paths,
        params ServerInstance[] instances)
    {
        using var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        await store.SaveAsync(new ManagerSettings { Instances = [.. instances] });
    }

    private sealed class CandidateWorkflow(ServerInstance candidate) : IOnlineModpackWorkflow
    {
        public int InstallCallCount { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            InstallCallCount++;
            return Task.FromResult(candidate);
        }
    }

    private sealed class AlwaysConfirmRemovalService : IServerRemovalConfirmationService
    {
        public bool ConfirmRemoval(string serverName, string directoryPath) => true;
    }
}
