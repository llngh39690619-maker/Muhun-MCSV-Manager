using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackMainIntegrationTests
{
    [Fact]
    public void MainWindow_OffersOnlineInstallInSidebarAndEmptyState()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(presentation + "Button")
            .Where(element => (string?)element.Attribute("Content")
                              == "{DynamicResource L10n.main.onlineModpack}")
            .ToArray();

        Assert.Equal(2, buttons.Length);
        Assert.All(
            buttons,
            button => Assert.Equal(
                "{Binding InstallOnlineModpackCommand}",
                (string?)button.Attribute("Command")));
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{DynamicResource L10n.main.modpackUpdate.hint}");
        Assert.Contains(
            "FTB／Modrinth",
            ProductLocalizationCatalog.GetDocument("zh-TW").Strings["main.modpackUpdate.hint"],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            document.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains(
                "CurseForge",
                StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task SuccessfulInstall_PreservesDefaultPortUntilLaunchPersistsSelectsAndReportsStatus()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        var serverDirectory = Path.Combine(paths.Servers, "Online Pack 中文");
        Directory.CreateDirectory(serverDirectory);
        File.WriteAllBytes(Path.Combine(serverDirectory, "server.jar"), [0x50, 0x4B, 0x03, 0x04]);
        await File.WriteAllTextAsync(
            Path.Combine(serverDirectory, "server.properties"),
            "motd=Online Pack\nserver-port=25565\n");
        var installed = new ServerInstance
        {
            Name = "Online Pack",
            DirectoryPath = serverDirectory,
            ServerJarPath = Path.Combine(serverDirectory, "server.jar"),
            CoreType = CoreType.Forge,
            MinecraftVersion = "1.20.1",
            JavaMajorVersion = 17,
            Port = ServerPortAllocator.DefaultPreferredPort
        };
        var workflow = new FakeWorkflow();
        var dialog = new FakeDialogService(installed);
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            workflow,
            dialog);

        await viewModel.InstallOnlineModpackAsync();

        Assert.Equal(1, dialog.CallCount);
        Assert.Null(dialog.LastOwner);
        var added = Assert.Single(viewModel.Servers);
        Assert.Same(added, viewModel.SelectedServer);
        Assert.Same(installed, added.Model);
        Assert.Equal(ServerPortAllocator.DefaultPreferredPort, added.Port);
        Assert.Contains(installed.Name, viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(
            added.ConsoleLines,
            line => line.Text.Contains("線上模組包安裝流程", StringComparison.Ordinal)
                    && line.Text.Contains("尚未啟動", StringComparison.Ordinal));

        var properties = await File.ReadAllTextAsync(Path.Combine(serverDirectory, "server.properties"));
        Assert.True(ServerPropertiesPortEditor.TryReadServerPort(properties, out var persistedPort));
        Assert.Equal(ServerPortAllocator.DefaultPreferredPort, persistedPort);
        using var settingsStore = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        var settings = Assert.IsType<ManagerSettings>(await settingsStore.LoadAsync());
        var persisted = Assert.Single(settings.Instances);
        Assert.Equal(installed.Id, persisted.Id);
        Assert.Equal(ServerPortAllocator.DefaultPreferredPort, persisted.Port);
    }

    [Fact]
    public async Task CancelledDialog_DoesNotAddOrPersistAnInstance()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        var workflow = new FakeWorkflow();
        var dialog = new FakeDialogService(null);
        paths.EnsureCreated();
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            workflow,
            dialog);

        await viewModel.InstallOnlineModpackAsync();

        Assert.Empty(viewModel.Servers);
        Assert.Contains("已取消", viewModel.StatusMessage, StringComparison.Ordinal);
        using var settingsStore = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile);
        var settings = await settingsStore.LoadAsync();
        Assert.True(settings is null || settings.Instances.Count == 0);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheOwnedOrInjectedOnlineWorkflow()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var workflow = new FakeWorkflow();
        var viewModel = new MainWindowViewModel(
            new ApplicationPaths(directory.Path),
            new AlwaysConfirmRemovalService(),
            workflow,
            new FakeDialogService(null));

        await viewModel.DisposeAsync();

        Assert.True(workflow.IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_FailedAttemptCanBeRetriedAndSuccessRemainsIdempotent()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var workflow = new FakeWorkflow { ThrowOnFirstDispose = true };
        var viewModel = new MainWindowViewModel(
            new ApplicationPaths(directory.Path),
            new AlwaysConfirmRemovalService(),
            workflow,
            new FakeDialogService(null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.DisposeAsync().AsTask());

        Assert.Equal(1, workflow.DisposeCount);
        Assert.False(workflow.IsDisposed);

        await viewModel.DisposeAsync();
        Assert.Equal(2, workflow.DisposeCount);
        Assert.True(workflow.IsDisposed);

        await viewModel.DisposeAsync();
        Assert.Equal(2, workflow.DisposeCount);
    }

    [Fact]
    public async Task PublicComposition_CreatesAProductionOnlineWorkflow()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var viewModel = new MainWindowViewModel(new ApplicationPaths(directory.Path));
        var workflowField = typeof(MainWindowViewModel).GetField(
            "_onlineModpackWorkflow",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(workflowField);
        Assert.IsType<OnlineModpackWorkflow>(workflowField!.GetValue(viewModel));

        await viewModel.DisposeAsync();
    }

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

    private sealed class FakeDialogService(ServerInstance? result) : IOnlineModpackDialogService
    {
        public int CallCount { get; private set; }
        public Window? LastOwner { get; private set; }

        public ServerInstance? ShowInstallDialog(Window? owner)
        {
            CallCount++;
            LastOwner = owner;
            return result;
        }
    }

    private sealed class AlwaysConfirmRemovalService : IServerRemovalConfirmationService
    {
        public bool ConfirmRemoval(string serverName, string directoryPath) => true;
    }

    private sealed class FakeWorkflow : IOnlineModpackWorkflow, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public bool ThrowOnFirstDispose { get; init; }
        public int DisposeCount { get; private set; }

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

        public void Dispose()
        {
            DisposeCount++;
            if (ThrowOnFirstDispose && DisposeCount == 1)
            {
                throw new InvalidOperationException("Transient workflow disposal failure.");
            }

            IsDisposed = true;
        }
    }
}
