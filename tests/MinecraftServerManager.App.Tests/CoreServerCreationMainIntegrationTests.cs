using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class CoreServerCreationMainIntegrationTests
{
    [Fact]
    public void MainWindow_UsesOnlyTheUnifiedCoreLauncherEntryInBothActionAreas()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(presentation + "Button").ToArray();

        var coreLauncherButtons = buttons
            .Where(button => (string?)button.Attribute("Command") == "{Binding CreateCoreServerCommand}")
            .ToArray();

        Assert.Equal(2, coreLauncherButtons.Length);
        Assert.All(
            coreLauncherButtons,
            button => Assert.Equal(
                "{DynamicResource L10n.main.createCore}",
                (string?)button.Attribute("Content")));
        Assert.DoesNotContain(
            buttons,
            button => (string?)button.Attribute("Command") == "{Binding CreatePaperServerCommand}");
        Assert.DoesNotContain(
            buttons,
            button => ((string?)button.Attribute("Content"))?.Contains(
                "建立 Paper",
                StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task CancelledDialog_DoesNotAddOrPersistAnInstance()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var dialog = new FakeCoreCreationDialogService();
        await using var viewModel = CreateViewModel(paths, dialog);

        await viewModel.CreateCoreServerAsync();

        Assert.Equal(1, dialog.CallCount);
        Assert.Empty(viewModel.Servers);
        Assert.Equal("已取消建立核心 Server", viewModel.StatusMessage);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.True(viewModel.CreateCoreServerCommand.CanExecute(null));
    }

    [Fact]
    public async Task SuccessfulVelocityCreation_PreservesDefaultPortUntilLaunchThenPersistsAndSelects()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        var serverDirectory = Path.Combine(paths.Servers, "Velocity 中文");
        Directory.CreateDirectory(serverDirectory);
        var jarPath = Path.Combine(serverDirectory, "velocity.jar");
        await File.WriteAllBytesAsync(jarPath, [0x50, 0x4B, 0x03, 0x04]);
        var created = new ServerInstance
        {
            Name = "Velocity Proxy",
            DirectoryPath = serverDirectory,
            ServerJarPath = jarPath,
            CoreType = CoreType.Velocity,
            MinecraftVersion = "3.4.0-SNAPSHOT",
            JavaMajorVersion = 17,
            ServerArguments = ["--show-config", "--port=25565"],
            StopCommand = "shutdown",
            Port = ServerPortAllocator.DefaultPreferredPort
        };
        var dialog = new FakeCoreCreationDialogService(created);
        await using var viewModel = CreateViewModel(paths, dialog);

        await viewModel.CreateCoreServerAsync();

        var added = Assert.Single(viewModel.Servers);
        Assert.Same(added, viewModel.SelectedServer);
        Assert.Same(created, added.Model);
        Assert.Equal(ServerPortAllocator.DefaultPreferredPort, added.Port);
        Assert.Equal("shutdown", created.StopCommand);
        Assert.Equal(["--show-config", "--port=25565"], created.ServerArguments);
        Assert.False(File.Exists(Path.Combine(serverDirectory, "server.properties")));
        Assert.Contains(created.Name, viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(
            added.ConsoleLines,
            line => line.Text.Contains("受驗證來源", StringComparison.Ordinal)
                    && line.Text.Contains("尚未啟動", StringComparison.Ordinal));

        using var settingsStore = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        var settings = Assert.IsType<ManagerSettings>(await settingsStore.LoadAsync());
        var persisted = Assert.Single(settings.Instances);
        Assert.Equal(created.Id, persisted.Id);
        Assert.Equal(ServerPortAllocator.DefaultPreferredPort, persisted.Port);
        Assert.Equal(CoreType.Velocity, persisted.CoreType);
        Assert.Equal("shutdown", persisted.StopCommand);
        Assert.Equal(created.ServerArguments, persisted.ServerArguments);
    }

    [Fact]
    public async Task MissingCreatedDirectory_IsRejectedWithoutAddingOrPersisting()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var missingDirectory = Path.Combine(paths.Servers, "workflow-did-not-create-this");
        var created = CreatePaperInstance(missingDirectory, "Missing");
        var dialog = new FakeCoreCreationDialogService(created);
        await using var viewModel = CreateViewModel(paths, dialog);

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            viewModel.CreateCoreServerAsync);

        Assert.Contains(missingDirectory, exception.Message, StringComparison.Ordinal);
        Assert.Empty(viewModel.Servers);
        Assert.False(Directory.Exists(missingDirectory));
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public async Task DuplicateDirectory_IsRejectedWithoutDeletingExistingDataOrChangingPersistence()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        var serverDirectory = Path.Combine(paths.Servers, "Shared Core Directory");
        Directory.CreateDirectory(serverDirectory);
        var sentinelPath = Path.Combine(serverDirectory, "keep-me.txt");
        await File.WriteAllTextAsync(sentinelPath, "existing server data");
        var first = CreatePaperInstance(serverDirectory, "First");
        var duplicate = CreatePaperInstance(
            serverDirectory + Path.DirectorySeparatorChar,
            "Duplicate");
        var dialog = new FakeCoreCreationDialogService(first, duplicate);
        await using var viewModel = CreateViewModel(paths, dialog);

        await viewModel.CreateCoreServerAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            viewModel.CreateCoreServerAsync);

        Assert.Contains("已存在", exception.Message, StringComparison.Ordinal);
        Assert.Contains("不會被刪除", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal("existing server data", await File.ReadAllTextAsync(sentinelPath));
        var onlyServer = Assert.Single(viewModel.Servers);
        Assert.Equal(first.Id, onlyServer.Id);
        Assert.Equal(2, dialog.CallCount);

        using var settingsStore = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        var settings = Assert.IsType<ManagerSettings>(await settingsStore.LoadAsync());
        var persisted = Assert.Single(settings.Instances);
        Assert.Equal(first.Id, persisted.Id);
        Assert.NotEqual(duplicate.Id, persisted.Id);
    }

    private static MainWindowViewModel CreateViewModel(
        ApplicationPaths paths,
        ICoreServerCreationDialogService coreCreationDialog)
        => new(
            paths,
            new AlwaysConfirmRemovalService(),
            new NoOpOnlineWorkflow(),
            new NoOpOnlineDialogService(),
            coreServerCreationDialogService: coreCreationDialog);

    private static ServerInstance CreatePaperInstance(string serverDirectory, string name)
        => new()
        {
            Name = name,
            DirectoryPath = serverDirectory,
            ServerJarPath = Path.Combine(serverDirectory, "server.jar"),
            CoreType = CoreType.Paper,
            MinecraftVersion = "1.21.8",
            JavaMajorVersion = 21
        };

    private static string GetAppSourcePath(
        string relativePath,
        [CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "MinecraftServerManager.App",
            relativePath));

    private sealed class FakeCoreCreationDialogService(params ServerInstance?[] results)
        : ICoreServerCreationDialogService
    {
        private readonly Queue<ServerInstance?> _results = new(results);

        public int CallCount { get; private set; }

        public ServerInstance? ShowCreateDialog(Window? owner)
        {
            CallCount++;
            return _results.Count == 0 ? null : _results.Dequeue();
        }
    }

    private sealed class NoOpOnlineDialogService : IOnlineModpackDialogService
    {
        public ServerInstance? ShowInstallDialog(Window? owner) => null;
    }

    private sealed class AlwaysConfirmRemovalService : IServerRemovalConfirmationService
    {
        public bool ConfirmRemoval(string serverName, string directoryPath) => true;
    }

    private sealed class NoOpOnlineWorkflow : IOnlineModpackWorkflow
    {
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
            => throw new NotSupportedException();
    }
}
