using System.IO;
using System.Net;
using System.Security;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientCurseForgeCatalogTests
{
    [Fact]
    public async Task SavedCredential_SearchesCurseForgeWithTheSelectedFiltersAndSort()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Missing);
        var workflow = new RecordingWorkflow
        {
            Results = [CreateProject()],
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);
        viewModel.CatalogSearchText = "  sky factory  ";
        viewModel.SelectedCatalogGameVersion = new ClientCatalogGameVersionChoice(
            "1.20.1",
            "Minecraft 1.20.1");
        viewModel.SelectedCatalogLoader = Assert.Single(
            viewModel.CatalogLoaders,
            choice => choice.Loader == MinecraftClientLoader.Fabric);
        viewModel.SelectedCatalogSort = Assert.Single(
            viewModel.CatalogSortOptions,
            choice => choice.Sort == ModrinthClientModpackSort.Updated);
        viewModel.CatalogResultLimit = 40;

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");

        await WaitUntilAsync(() =>
            workflow.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 1);
        var request = Assert.Single(workflow.Requests);
        Assert.Equal(OnlineModpackProvider.CurseForge, request.Provider);
        Assert.Equal("sky factory", request.Query);
        Assert.Equal(OnlineModpackSort.RecentlyUpdated, request.Sort);
        Assert.Equal("1.20.1", request.GameVersion);
        Assert.Equal("Fabric", request.Loader);
        Assert.Null(request.SourceCategory);
        Assert.Equal(0, request.Offset);
        Assert.Equal(40, request.Limit);
        Assert.True(workflow.CredentialWasSupplied);
        Assert.True(workflow.CredentialWasReadOnly);
        Assert.Equal("curseforge", viewModel.CatalogSourceId);
        Assert.Equal("curseforge-project", viewModel.CatalogProjects[0].ProjectId);
        Assert.Equal(0, importer.ImportCount);
    }

    [Fact]
    public async Task MissingCredential_DoesNotCallTheCurseForgeWorkflow()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: false);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Missing);
        var workflow = new RecordingWorkflow();
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");

        await WaitUntilAsync(() => importer.ImportCount == 1);
        Assert.Equal("curseforge", viewModel.CatalogSourceId);
        Assert.False(viewModel.HasCurseForgeCredential);
        Assert.Empty(workflow.Requests);
        Assert.Empty(viewModel.CatalogProjects);
        Assert.Equal(
            LocalizationService.Current.Get("client.vm.catalog.curseForge.credentialRequired"),
            viewModel.CatalogStatusText);
    }

    [Fact]
    public async Task SelectingCurseForge_ImportsTheOneTimeSettingsFileAndSearchesAutomatically()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: false);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Imported);
        var workflow = new RecordingWorkflow
        {
            Results = [CreateProject()],
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);
        viewModel.CatalogSearchText = "automation";

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");

        await WaitUntilAsync(() =>
            workflow.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 1);
        Assert.Equal(1, importer.ImportCount);
        Assert.True(viewModel.HasCurseForgeCredential);
        Assert.Equal("automation", Assert.Single(workflow.Requests).Query);
        Assert.True(workflow.CredentialWasSupplied);
    }

    [Fact]
    public async Task SelectingCurseForgeProject_ListsOnlyReleaseVersionsMatchingTheSelectedFilters()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Missing);
        var project = CreateProject();
        var workflow = new RecordingWorkflow
        {
            Results = [project],
            Versions =
            [
                CreateVersion(
                    project.ProjectId,
                    "matching-release",
                    "release",
                    minecraftVersion: "1.20.1",
                    loader: "Neo-Forge"),
                CreateVersion(
                    project.ProjectId,
                    "wrong-game",
                    "release",
                    minecraftVersion: "1.21.1",
                    loader: "NeoForge"),
                CreateVersion(
                    project.ProjectId,
                    "wrong-loader",
                    "release",
                    minecraftVersion: "1.20.1",
                    loader: "Fabric"),
                CreateVersion(
                    project.ProjectId,
                    "matching-beta",
                    "beta",
                    minecraftVersion: "1.20.1",
                    loader: "NeoForge"),
            ],
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);
        viewModel.SelectedCatalogGameVersion = new ClientCatalogGameVersionChoice(
            "1.20.1",
            "Minecraft 1.20.1");
        viewModel.SelectedCatalogLoader = Assert.Single(
            viewModel.CatalogLoaders,
            choice => choice.Loader == MinecraftClientLoader.NeoForge);

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");
        await WaitUntilAsync(() =>
            workflow.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 1);

        viewModel.SelectedCatalogProject = Assert.Single(viewModel.CatalogProjects);

        await WaitUntilAsync(() =>
            workflow.VersionRequestCount == 1 &&
            viewModel.CatalogVersions.Count == 1);
        var displayedVersion = Assert.Single(viewModel.CatalogVersions).CurseForgeVersion;
        Assert.NotNull(displayedVersion);
        Assert.Equal("matching-release", displayedVersion.VersionId);
        Assert.Equal("release", displayedVersion.ReleaseChannel);
        Assert.Same(viewModel.CatalogVersions[0], viewModel.SelectedCatalogVersion);
    }

    [Fact]
    public async Task SavedSettingsFile_IsImportedByDebouncedSearchWithoutReselectingCurseForge()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: false);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Missing);
        var workflow = new RecordingWorkflow
        {
            Results = [CreateProject()],
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");
        await WaitUntilAsync(() => importer.ImportCount == 1);
        viewModel.OpenCatalogCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.IsCatalogPage &&
            viewModel.OpenCatalogCommand.CanExecute(null));
        importer.Result = CurseForgeCredentialImportResult.Imported;

        viewModel.CatalogSearchText = "saved settings";

        await WaitUntilAsync(() =>
            workflow.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 1);
        Assert.True(viewModel.HasCurseForgeCredential);
        Assert.True(importer.ImportCount >= 3);
        Assert.Equal("saved settings", Assert.Single(workflow.Requests).Query);
    }

    [Fact]
    public async Task SupersededCurseForgeFailure_DoesNotOverwriteTheCurrentSearchState()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: false);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Missing);
        var staleRequest = new TaskCompletionSource<IReadOnlyList<OnlineModpackSearchResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new RecordingWorkflow
        {
            BrowseHandler = (request, _) => string.IsNullOrEmpty(request.Query)
                ? staleRequest.Task
                : Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>(
                    [CreateProject("current-project")]),
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");
        await WaitUntilAsync(() => importer.ImportCount == 1);
        credentialStore.MarkImported();
        viewModel.OpenCatalogCommand.Execute(null);
        await WaitUntilAsync(() =>
            workflow.Requests.Count == 1 &&
            !viewModel.OpenCatalogCommand.CanExecute(null));

        viewModel.CatalogSearchText = "current";

        await WaitUntilAsync(() =>
            workflow.Requests.Count == 2 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 1);
        var currentStatus = viewModel.CatalogStatusText;
        Assert.Equal("current-project", Assert.Single(viewModel.CatalogProjects).ProjectId);

        staleRequest.SetException(new CurseForgeApiException(
            CurseForgeApiErrorCode.InvalidApiKey,
            HttpStatusCode.Unauthorized));
        await WaitUntilAsync(() => viewModel.OpenCatalogCommand.CanExecute(null));

        Assert.Equal(currentStatus, viewModel.CatalogStatusText);
        Assert.Equal(string.Empty, viewModel.ErrorText);
        Assert.Equal("current-project", Assert.Single(viewModel.CatalogProjects).ProjectId);
    }

    [Fact]
    public async Task InvalidCredentialResponse_IsVisibleInTheCatalogStatusAndError()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Missing);
        var workflow = new RecordingWorkflow
        {
            BrowseError = new CurseForgeApiException(
                CurseForgeApiErrorCode.InvalidApiKey,
                HttpStatusCode.Unauthorized),
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");

        var expected = LocalizationService.Current.Get(
            "client.vm.catalog.curseForge.invalidCredential");
        await WaitUntilAsync(() =>
            workflow.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogStatusText == expected);
        Assert.Equal(expected, viewModel.ErrorText);
        Assert.Empty(viewModel.CatalogProjects);
    }

    [Fact]
    public void CurseForgeCredentialUi_UsesASettingsFileWithoutAPasswordInput()
    {
        var xaml = File.ReadAllText(
            TestRepositoryPaths.AppSource("Views", "ClientWorkspaceView.xaml"));

        Assert.DoesNotContain("<PasswordBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CurseForgeApiKeyBox", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding OpenCurseForgeCredentialFileCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding DeleteCurseForgeCredentialCommand}\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static ClientWorkspaceViewModel CreateViewModel(
        string root,
        IOnlineModpackWorkflow workflow,
        ICurseForgeCredentialStore credentialStore,
        ICurseForgeCredentialFileImportService importer) =>
        new(
            new ApplicationPaths(root),
            static () => new NewMinecraftClientDefaultsSettings(),
            releaseCatalog: null,
            loaderCatalogs: [],
            onlineModpackWorkflow: workflow,
            curseForgeCredentialStore: credentialStore,
            curseForgeCredentialFileImportService: importer);

    private static OnlineModpackSearchResult CreateProject(
        string projectId = "curseforge-project") =>
        new(
            OnlineModpackProvider.CurseForge,
            projectId,
            "CurseForge test project",
            "A deterministic test result.",
            "Test author",
            new Uri("https://www.curseforge.com/minecraft/modpacks/test-project"),
            downloadCount: 42,
            updatedAtUtc: DateTimeOffset.Parse("2026-01-02T03:04:05Z"));

    private static OnlineModpackVersion CreateVersion(
        string projectId,
        string versionId,
        string releaseChannel,
        string minecraftVersion = "1.20.1",
        string loader = "Fabric") =>
        new(
            OnlineModpackProvider.CurseForge,
            projectId,
            versionId,
            versionId,
            minecraftVersion,
            loader,
            releaseChannel,
            DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            HasOfficialServerPack: false);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate());
    }

    private sealed class RecordingWorkflow : IOnlineModpackWorkflow
    {
        public List<OnlineModpackBrowseRequest> Requests { get; } = [];

        public IReadOnlyList<OnlineModpackSearchResult> Results { get; init; } = [];

        public IReadOnlyList<OnlineModpackVersion> Versions { get; init; } = [];

        public Exception? BrowseError { get; init; }

        public Func<
            OnlineModpackBrowseRequest,
            CancellationToken,
            Task<IReadOnlyList<OnlineModpackSearchResult>>>? BrowseHandler { get; init; }

        public int VersionRequestCount { get; private set; }

        public bool CredentialWasSupplied { get; private set; }

        public bool CredentialWasReadOnly { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseAsync(
            OnlineModpackBrowseRequest request,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            CredentialWasSupplied = transientApiKey is not null;
            CredentialWasReadOnly = transientApiKey?.IsReadOnly() == true;
            if (BrowseHandler is not null)
            {
                return BrowseHandler(request, cancellationToken);
            }

            return BrowseError is null
                ? Task.FromResult(Results)
                : Task.FromException<IReadOnlyList<OnlineModpackSearchResult>>(BrowseError);
        }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VersionRequestCount++;
            return Task.FromResult(Versions);
        }

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentialStore(bool hasCredential) : ICurseForgeCredentialStore
    {
        public bool HasCredential { get; private set; } = hasCredential;

        public SecureString? AcquireReadOnly()
        {
            if (!HasCredential)
            {
                return null;
            }

            var credential = new SecureString();
            credential.AppendChar('x');
            credential.MakeReadOnly();
            return credential;
        }

        public void Save(SecureString credential) => HasCredential = true;

        public bool Delete()
        {
            var deleted = HasCredential;
            HasCredential = false;
            return deleted;
        }

        public void MarkImported() => HasCredential = true;
    }

    private sealed class FakeCredentialImporter(
        FakeCredentialStore credentialStore,
        CurseForgeCredentialImportResult result) : ICurseForgeCredentialFileImportService
    {
        public CurseForgeCredentialImportResult Result { get; set; } = result;

        public string ImportFilePath { get; } = Path.Combine(
            Path.GetTempPath(),
            "x-mcsv-curseforge-test.import.txt");

        public int ImportCount { get; private set; }

        public string PrepareEditableFile() => ImportFilePath;

        public CurseForgeCredentialImportResult ImportIfPresent()
        {
            ImportCount++;
            if (Result == CurseForgeCredentialImportResult.Imported)
            {
                credentialStore.MarkImported();
            }

            return Result;
        }
    }
}
