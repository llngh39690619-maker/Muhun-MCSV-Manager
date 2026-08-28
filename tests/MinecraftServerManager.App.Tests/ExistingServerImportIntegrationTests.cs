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

public sealed class ExistingServerImportIntegrationTests
{
    [Fact]
    public void MainWindow_UsesOneUnifiedImportEntryPerVisibleActionArea()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(presentation + "Button").ToArray();

        var unifiedButtons = buttons
            .Where(button => (string?)button.Attribute("Command") == "{Binding ImportExistingServerCommand}")
            .ToArray();

        Assert.Equal(2, unifiedButtons.Length);
        Assert.All(
            unifiedButtons,
            button => Assert.Equal(
                "{DynamicResource L10n.main.importServer}",
                (string?)button.Attribute("Content")));
        Assert.DoesNotContain(
            buttons,
            button => (string?)button.Attribute("Command") == "{Binding ImportServerFolderCommand}");
        Assert.DoesNotContain(
            buttons,
            button => (string?)button.Attribute("Command") == "{Binding ImportServerCommand}");
    }

    [Fact]
    public Task Coordinator_FolderChoice_RoutesOnlyToExistingFolderImportFlow()
        => AssertCoordinatorRoutingAsync(ExistingServerImportKind.ServerFolder, 1, 0);

    [Fact]
    public Task Coordinator_JarChoice_RoutesOnlyToExistingJarImportFlow()
        => AssertCoordinatorRoutingAsync(ExistingServerImportKind.ServerJar, 0, 1);

    private static async Task AssertCoordinatorRoutingAsync(
        ExistingServerImportKind choice,
        int expectedFolderCalls,
        int expectedJarCalls)
    {
        var choiceService = new FakeChoiceService(choice);
        var folderCalls = 0;
        var jarCalls = 0;
        var coordinator = new ExistingServerImportCoordinator(
            choiceService,
            () =>
            {
                folderCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                jarCalls++;
                return Task.CompletedTask;
            });
        var result = await coordinator.ChooseAndImportAsync(owner: null);

        Assert.Equal(choice, result);
        Assert.Equal(1, choiceService.CallCount);
        Assert.Null(choiceService.LastOwner);
        Assert.Equal(expectedFolderCalls, folderCalls);
        Assert.Equal(expectedJarCalls, jarCalls);
    }

    [Fact]
    public async Task Coordinator_Cancel_DoesNotStartEitherImportFlow()
    {
        var choiceService = new FakeChoiceService(null);
        var actionCalls = 0;
        var coordinator = new ExistingServerImportCoordinator(
            choiceService,
            () =>
            {
                actionCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                actionCalls++;
                return Task.CompletedTask;
            });

        var result = await coordinator.ChooseAndImportAsync(owner: null);

        Assert.Null(result);
        Assert.Equal(0, actionCalls);
    }

    [Fact]
    public async Task ViewModel_CancelledUnifiedImport_ReportsStatusWithoutChangingServers()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var choiceService = new FakeChoiceService(null);
        var workflow = new NoOpOnlineWorkflow();
        await using var viewModel = new MainWindowViewModel(
            new ApplicationPaths(directory.Path),
            new AlwaysConfirmRemovalService(),
            workflow,
            new NoOpOnlineDialogService(),
            choiceService);

        await viewModel.ImportExistingServerAsync();

        Assert.Equal(1, choiceService.CallCount);
        Assert.Empty(viewModel.Servers);
        Assert.Equal("已取消匯入現有 Server", viewModel.StatusMessage);
        Assert.True(viewModel.ImportExistingServerCommand.CanExecute(null));
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);

    private sealed class FakeChoiceService(ExistingServerImportKind? result)
        : IExistingServerImportChoiceService
    {
        public int CallCount { get; private set; }
        public Window? LastOwner { get; private set; }

        public ExistingServerImportKind? ShowChoice(Window? owner)
        {
            CallCount++;
            LastOwner = owner;
            return result;
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
