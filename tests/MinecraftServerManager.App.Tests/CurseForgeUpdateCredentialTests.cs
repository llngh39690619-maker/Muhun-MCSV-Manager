using System.IO;
using System.Security;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class CurseForgeUpdateCredentialTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public async Task CurseForgeUpdate_UsesOneReadOnlyCredentialForVersionAndInstallThenDisposesIt()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var credential = CreateSecureString("test-only-curseforge-key");
        var prompt = new RecordingCredentialPrompt(credential);
        var workflow = new RecordingUpdateWorkflow(OnlineModpackProvider.CurseForge);
        var selection = new FirstUpdateSelectionService();
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            workflow,
            onlineModpackDialogService: null,
            curseForgeUpdateCredentialPrompt: prompt,
            modpackUpdateSelectionService: selection);
        var server = CreateServer(paths, ModpackSourceKind.CurseForge);
        viewModel.Servers.Add(server);

        await Assert.ThrowsAsync<ExpectedInstallException>(() =>
            viewModel.SelectAndUpdateModpackAsync(server, CancellationToken.None));

        Assert.Equal(1, prompt.RequestCount);
        Assert.Equal(1, selection.RequestCount);
        Assert.Same(credential, workflow.VersionCredential);
        Assert.Same(workflow.VersionCredential, workflow.InstallCredential);
        Assert.True(workflow.CredentialWasReadOnlyAtVersionQuery);
        Assert.True(workflow.CredentialWasReadOnlyAtInstall);
        Assert.Throws<ObjectDisposedException>(() => _ = credential.Length);
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public async Task CurseForgeUpdate_CancelledCredentialPromptStopsBeforeCatalogAccess()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var prompt = new RecordingCredentialPrompt(credential: null);
        var workflow = new RecordingUpdateWorkflow(OnlineModpackProvider.CurseForge);
        var selection = new FirstUpdateSelectionService();
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            workflow,
            onlineModpackDialogService: null,
            curseForgeUpdateCredentialPrompt: prompt,
            modpackUpdateSelectionService: selection);
        var server = CreateServer(paths, ModpackSourceKind.CurseForge);
        viewModel.Servers.Add(server);

        await viewModel.SelectAndUpdateModpackAsync(server, CancellationToken.None);

        Assert.Equal(1, prompt.RequestCount);
        Assert.Equal(0, workflow.VersionRequestCount);
        Assert.Equal(0, workflow.InstallRequestCount);
        Assert.Equal(0, selection.RequestCount);
    }

    [Fact]
    public async Task NonCurseForgeUpdate_DoesNotRequestCredentialAndPassesNullToWorkflow()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var prompt = new RecordingCredentialPrompt(CreateSecureString("must-not-be-requested"));
        var workflow = new RecordingUpdateWorkflow(OnlineModpackProvider.Modrinth);
        var selection = new FirstUpdateSelectionService();
        await using var viewModel = new MainWindowViewModel(
            paths,
            new AlwaysConfirmRemovalService(),
            workflow,
            onlineModpackDialogService: null,
            curseForgeUpdateCredentialPrompt: prompt,
            modpackUpdateSelectionService: selection);
        var server = CreateServer(paths, ModpackSourceKind.Modrinth);
        viewModel.Servers.Add(server);

        await Assert.ThrowsAsync<ExpectedInstallException>(() =>
            viewModel.SelectAndUpdateModpackAsync(server, CancellationToken.None));

        Assert.Equal(0, prompt.RequestCount);
        Assert.Null(workflow.VersionCredential);
        Assert.Null(workflow.InstallCredential);
        prompt.DisposeUnclaimed();
    }

    [Fact]
    public void CredentialDialog_UsesDarkPasswordInputWithoutPlainTextBinding()
    {
        var document = XDocument.Load(GetAppSourcePath(Path.Combine(
            "Dialogs",
            "CurseForgeUpdateCredentialDialog.xaml")));
        var window = Assert.Single(document.Elements(Presentation + "Window"));
        Assert.Equal("{StaticResource AppWindowStyle}", (string?)window.Attribute("Style"));
        Assert.NotNull(document.Descendants(Presentation + "PasswordBox").SingleOrDefault());
        Assert.Empty(document.Descendants(Presentation + "TextBox"));
        Assert.All(
            document.Descendants().SelectMany(element => element.Attributes()),
            attribute => Assert.DoesNotContain("Password=", attribute.Value, StringComparison.Ordinal));
    }

    private static ServerInstanceViewModel CreateServer(
        ApplicationPaths paths,
        ModpackSourceKind source)
    {
        var root = Path.Combine(paths.Servers, $"server-{source}");
        Directory.CreateDirectory(root);
        var model = new ServerInstance
        {
            Name = $"{source} Pack",
            DirectoryPath = root,
            ServerJarPath = Path.Combine(root, "server.jar"),
            ModpackSource = source,
            ModpackProjectId = "project",
            ModpackVersionId = "old-version",
            ModpackVersionName = "1.0.0",
        };
        return new ServerInstanceViewModel(model, (_, _) => Task.CompletedTask);
    }

    private static SecureString CreateSecureString(string value)
    {
        var result = new SecureString();
        foreach (var character in value)
        {
            result.AppendChar(character);
        }

        return result;
    }

    private sealed class RecordingCredentialPrompt(SecureString? credential)
        : ICurseForgeUpdateCredentialPrompt
    {
        private SecureString? _credential = credential;

        public int RequestCount { get; private set; }

        public SecureString? RequestCredential(System.Windows.Window? owner)
        {
            RequestCount++;
            var result = _credential;
            _credential = null;
            return result;
        }

        public void DisposeUnclaimed()
        {
            _credential?.Dispose();
            _credential = null;
        }
    }

    private sealed class FirstUpdateSelectionService : IModpackUpdateSelectionService
    {
        public int RequestCount { get; private set; }

        public OnlineModpackVersion? SelectVersion(
            ServerInstance instance,
            IReadOnlyList<OnlineModpackVersion> availableVersions,
            System.Windows.Window? owner)
        {
            RequestCount++;
            return Assert.Single(availableVersions);
        }
    }

    private sealed class RecordingUpdateWorkflow(OnlineModpackProvider provider)
        : IOnlineModpackWorkflow
    {
        public SecureString? VersionCredential { get; private set; }

        public SecureString? InstallCredential { get; private set; }

        public int VersionRequestCount { get; private set; }

        public int InstallRequestCount { get; private set; }

        public bool CredentialWasReadOnlyAtVersionQuery { get; private set; }

        public bool CredentialWasReadOnlyAtInstall { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider requestedProvider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            VersionRequestCount++;
            VersionCredential = transientApiKey;
            CredentialWasReadOnlyAtVersionQuery = transientApiKey?.IsReadOnly() ?? false;
            return Task.FromResult<IReadOnlyList<OnlineModpackVersion>>(
            [
                new(
                    provider,
                    "project",
                    "old-version",
                    "1.0.0",
                    "1.20.1",
                    "Forge",
                    "release",
                    DateTimeOffset.UtcNow.AddDays(-2),
                    HasOfficialServerPack: true),
                new(
                    provider,
                    "project",
                    "new-version",
                    "1.1.0",
                    "1.20.1",
                    "Forge",
                    "release",
                    DateTimeOffset.UtcNow.AddDays(-1),
                    HasOfficialServerPack: true),
            ]);
        }

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            InstallRequestCount++;
            InstallCredential = transientApiKey;
            CredentialWasReadOnlyAtInstall = transientApiKey?.IsReadOnly() ?? false;
            throw new ExpectedInstallException();
        }
    }

    private sealed class AlwaysConfirmRemovalService : IServerRemovalConfirmationService
    {
        public bool ConfirmRemoval(string serverName, string directoryPath) => true;
    }

    private sealed class ExpectedInstallException : Exception
    {
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
