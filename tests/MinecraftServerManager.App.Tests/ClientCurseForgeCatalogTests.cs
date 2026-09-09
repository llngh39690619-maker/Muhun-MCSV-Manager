using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed partial class ClientCurseForgeCatalogTests
{
    [Fact]
    public async Task InitialCatalogSource_IsCurseForge()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: false);
        await using var viewModel = CreateViewModel(
            directory.Path,
            new RecordingWorkflow(),
            credentialStore,
            new FakeCredentialImporter(credentialStore, CurseForgeCredentialImportResult.Missing));

        Assert.Equal("curseforge", viewModel.CatalogSourceId);
        Assert.True(viewModel.IsCurseForgeCatalogSource);
        Assert.False(viewModel.IsModrinthCatalogSource);
    }

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
    public async Task SavedCredential_PageCommandUsesCurseForgeOffsetAndReplacesResults()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Missing);
        var workflow = new RecordingWorkflow
        {
            BrowseHandler = (request, _) =>
            {
                var count = request.Offset == 0 ? request.Limit : 5;
                return Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>(
                    Enumerable.Range(request.Offset, count)
                        .Select(index => CreateProject($"curseforge-project-{index}"))
                        .ToArray());
            },
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");
        await WaitUntilAsync(() =>
            workflow.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 20);

        Assert.True(viewModel.NextCatalogPageCommand.CanExecute(null));
        viewModel.GoToCatalogPageCommand.Execute(2);
        await WaitUntilAsync(() =>
            workflow.Requests.Count == 2 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogCurrentPage == 2);

        Assert.Equal(20, workflow.Requests[1].Offset);
        Assert.Equal(5, viewModel.CatalogProjects.Count);
        Assert.Equal("curseforge-project-20", viewModel.CatalogProjects[0].ProjectId);
        Assert.DoesNotContain(
            viewModel.CatalogProjects,
            static project => project.ProjectId == "curseforge-project-0");
        Assert.Equal(25, viewModel.CatalogTotalHits);
        Assert.False(viewModel.NextCatalogPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task SavedCredential_OfficialTotalShowsFarPagesAndJumpUsesExactOffset()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var importer = new FakeCredentialImporter(
            credentialStore,
            CurseForgeCredentialImportResult.Missing);
        const int totalHits = 2_500;
        var workflow = new RecordingWorkflow
        {
            BrowsePageHandler = (request, _) =>
            {
                var count = Math.Min(request.Limit, Math.Max(0, totalHits - request.Offset));
                IReadOnlyList<OnlineModpackSearchResult> projects = Enumerable
                    .Range(request.Offset, count)
                    .Select(index => CreateProject($"curseforge-project-{index}"))
                    .ToArray();
                return Task.FromResult(new OnlineModpackBrowsePage(
                    projects,
                    request.Offset,
                    request.Limit,
                    totalHits));
            },
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            importer);

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");
        await WaitUntilAsync(() =>
            workflow.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 20);

        Assert.Equal(totalHits, viewModel.CatalogTotalHits);
        Assert.Equal(125, viewModel.CatalogPageCount);
        Assert.Equal(
            new[] { "1", "2", "3", "4", "5", "…", "125" },
            viewModel.CatalogPaginationItems
                .Select(static item => item.DisplayText)
                .ToArray());

        viewModel.GoToCatalogPageCommand.Execute(125);
        await WaitUntilAsync(() =>
            workflow.Requests.Count == 2 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogCurrentPage == 125);

        Assert.Equal(2_480, workflow.Requests[1].Offset);
        Assert.Equal("curseforge-project-2480", viewModel.CatalogProjects[0].ProjectId);
        Assert.Equal(totalHits, viewModel.CatalogTotalHits);
        Assert.False(viewModel.NextCatalogPageCommand.CanExecute(null));
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
    public async Task DistributionAllowed_ShowsVerifiedDirectInstallInsteadOfOfficialPageAction()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var project = CreateProject("481262", allowsThirdPartyDistribution: true);
        var workflow = new RecordingWorkflow
        {
            Versions = [CreateVersion(project.ProjectId, "4612979", "release")],
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            new FakeCredentialImporter(credentialStore, CurseForgeCredentialImportResult.Missing));

        viewModel.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);

        await WaitUntilAsync(() => viewModel.SelectedCatalogVersion is not null);
        Assert.False(viewModel.IsSelectedCurseForgeManualDownloadRequired);
        Assert.True(viewModel.ShowsCatalogInstallOptions);
        Assert.Equal(
            LocalizationService.Current.Get("client.action.install"),
            viewModel.CatalogInstallActionText);
        Assert.True(viewModel.InstallCatalogPackCommand.CanExecute(null));
    }

    [Fact]
    public async Task DistributionForbidden_ShowsOnlyTheExplicitOfficialPageFallback()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var project = CreateProject("481262", allowsThirdPartyDistribution: false);
        var workflow = new RecordingWorkflow
        {
            Versions = [CreateVersion(project.ProjectId, "4612979", "release")],
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            new FakeCredentialImporter(credentialStore, CurseForgeCredentialImportResult.Missing));

        viewModel.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);

        await WaitUntilAsync(() => viewModel.SelectedCatalogVersion is not null);
        Assert.True(viewModel.IsSelectedCurseForgeManualDownloadRequired);
        Assert.False(viewModel.ShowsCatalogInstallOptions);
        Assert.Equal(
            LocalizationService.Current.Get("client.catalog.curseForgeOpenProject"),
            viewModel.CatalogInstallActionText);
        Assert.True(viewModel.InstallCatalogPackCommand.CanExecute(null));
    }

    [Fact]
    public async Task DirectInstall_PassesExactCurseForgeIdsExpectedGameVersionAndReadOnlyCredential()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var project = CreateProject("481262", allowsThirdPartyDistribution: true);
        var workflow = new RecordingWorkflow
        {
            Versions =
            [
                CreateVersion(
                    project.ProjectId,
                    "4612979",
                    "release",
                    minecraftVersion: "1.12.2",
                    loader: "Forge"),
            ],
        };
        var installer = new RecordingCurseForgeInstaller(directory.Path);
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            new FakeCredentialImporter(credentialStore, CurseForgeCredentialImportResult.Missing),
            releaseCatalog: new StaticReleaseCatalog("1.12.2"),
            curseForgeInstaller: installer,
            resolveCatalogJavaAsync: static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(@"C:\test-java\bin\java.exe");
            });
        await viewModel.InitializeForDiagnosticsAsync();
        viewModel.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
        await WaitUntilAsync(() => viewModel.SelectedCatalogVersion is not null);

        viewModel.InstallCatalogPackCommand.Execute(null);

        await WaitUntilAsync(() =>
            installer.Invocations.Count == 1 &&
            viewModel.CatalogInstallJobs.SingleOrDefault()?.IsTerminal == true);
        var invocation = Assert.Single(installer.Invocations);
        Assert.Equal(481262, invocation.Request.ModId);
        Assert.Equal(4612979, invocation.Request.FileId);
        Assert.Equal("1.12.2", invocation.Request.ExpectedMinecraftVersion);
        Assert.Equal("x", invocation.Credential);
        Assert.True(invocation.CredentialWasReadOnly);
        Assert.Equal(@"C:\test-java\bin\java.exe", invocation.JavaExecutablePath);
        Assert.False(Assert.Single(viewModel.CatalogInstallJobs).IsFailed);
        Assert.Single(viewModel.Instances);
    }

    [Fact]
    public async Task DistributionUnavailable_FallbackIsScopedToExactProjectAndFileAndSurvivesSelectionChanges()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var firstProject = CreateProject("481262", allowsThirdPartyDistribution: true);
        var secondProject = CreateProject("777777", allowsThirdPartyDistribution: true);
        const string sharedFileId = "4612979";
        var workflow = new RecordingWorkflow
        {
            Versions =
            [
                CreateVersion(firstProject.ProjectId, sharedFileId, "release"),
                CreateVersion(secondProject.ProjectId, sharedFileId, "release"),
            ],
        };
        var installer = new RecordingCurseForgeInstaller(directory.Path)
        {
            Error = new CurseForgeServerPackException(
                CurseForgeServerPackResolutionStatus.DistributionUnavailable,
                "Third-party distribution is unavailable."),
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            new FakeCredentialImporter(credentialStore, CurseForgeCredentialImportResult.Missing),
            releaseCatalog: new StaticReleaseCatalog("1.20.1"),
            curseForgeInstaller: installer,
            resolveCatalogJavaAsync: static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(@"C:\test-java\bin\java.exe");
            });
        await viewModel.InitializeForDiagnosticsAsync();
        var firstItem = new ClientModpackProjectItemViewModel(firstProject);
        var secondItem = new ClientModpackProjectItemViewModel(secondProject);
        viewModel.SelectedCatalogProject = firstItem;
        await WaitUntilAsync(() =>
            viewModel.SelectedCatalogVersion?.CurseForgeVersion?.ProjectId == firstProject.ProjectId);

        viewModel.InstallCatalogPackCommand.Execute(null);

        await WaitUntilAsync(() =>
            installer.Invocations.Count == 1 &&
            viewModel.IsSelectedCurseForgeManualDownloadRequired &&
            viewModel.CatalogInstallJobs.SingleOrDefault()?.IsTerminal == true);
        Assert.Equal(
            LocalizationService.Current.Get("client.catalog.curseForgeOpenProject"),
            viewModel.CatalogInstallActionText);

        viewModel.SelectedCatalogProject = secondItem;
        await WaitUntilAsync(() =>
            viewModel.SelectedCatalogVersion?.CurseForgeVersion?.ProjectId == secondProject.ProjectId);
        Assert.False(viewModel.IsSelectedCurseForgeManualDownloadRequired);
        Assert.True(viewModel.ShowsCatalogInstallOptions);
        Assert.Equal(
            LocalizationService.Current.Get("client.action.install"),
            viewModel.CatalogInstallActionText);

        viewModel.SelectedCatalogProject = firstItem;
        await WaitUntilAsync(() =>
            viewModel.SelectedCatalogVersion?.CurseForgeVersion?.ProjectId == firstProject.ProjectId);
        Assert.True(viewModel.IsSelectedCurseForgeManualDownloadRequired);
        Assert.False(viewModel.ShowsCatalogInstallOptions);
    }

    [Fact]
    public async Task DirectInstall_InvalidApiKeyShowsCredentialMessageAndDisablesCredentialState()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var credentialStore = new FakeCredentialStore(hasCredential: true);
        var project = CreateProject("481262", allowsThirdPartyDistribution: true);
        var workflow = new RecordingWorkflow
        {
            Versions = [CreateVersion(project.ProjectId, "4612979", "release")],
        };
        var installer = new RecordingCurseForgeInstaller(directory.Path)
        {
            Error = new CurseForgeApiException(
                CurseForgeApiErrorCode.InvalidApiKey,
                HttpStatusCode.Unauthorized),
        };
        await using var viewModel = CreateViewModel(
            directory.Path,
            workflow,
            credentialStore,
            new FakeCredentialImporter(credentialStore, CurseForgeCredentialImportResult.Missing),
            releaseCatalog: new StaticReleaseCatalog("1.20.1"),
            curseForgeInstaller: installer,
            resolveCatalogJavaAsync: static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(@"C:\test-java\bin\java.exe");
            });
        await viewModel.InitializeForDiagnosticsAsync();
        viewModel.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
        await WaitUntilAsync(() => viewModel.SelectedCatalogVersion is not null);

        viewModel.InstallCatalogPackCommand.Execute(null);

        var expected = LocalizationService.Current.Get(
            "client.vm.catalog.curseForge.invalidCredential");
        await WaitUntilAsync(() =>
            installer.Invocations.Count == 1 &&
            !viewModel.HasCurseForgeCredential &&
            viewModel.CatalogStatusText == expected &&
            viewModel.CatalogInstallJobs.SingleOrDefault()?.IsTerminal == true);
        Assert.Equal(expected, viewModel.ErrorText);
        Assert.True(Assert.Single(viewModel.CatalogInstallJobs).IsFailed);
        Assert.False(viewModel.InstallCatalogPackCommand.CanExecute(null));
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
    public void CurseForgeCredentialUi_LivesInGeneralSettingsWithoutAPasswordInput()
    {
        var workspaceXaml = File.ReadAllText(
            TestRepositoryPaths.AppSource("Views", "ClientWorkspaceView.xaml"));
        var settingsXaml = File.ReadAllText(
            TestRepositoryPaths.AppSource("Dialogs", "GeneralSettingsDialog.xaml"));

        Assert.DoesNotContain("OpenCurseForgeCredentialFileCommand", workspaceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteCurseForgeCredentialCommand", workspaceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<PasswordBox", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CurseForgeApiKeyBox", settingsXaml, StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding CurseForgeCredentialSettings.OpenCredentialFileCommand}\"",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding CurseForgeCredentialSettings.DeleteCredentialCommand}\"",
            settingsXaml,
            StringComparison.Ordinal);
    }

    private static ClientWorkspaceViewModel CreateViewModel(
        string root,
        IOnlineModpackWorkflow workflow,
        ICurseForgeCredentialStore credentialStore,
        ICurseForgeCredentialFileImportService importer,
        IMinecraftReleaseCatalog? releaseCatalog = null,
        ICurseForgeMinecraftClientPackInstaller? curseForgeInstaller = null,
        Func<int, CancellationToken, Task<string>>? resolveCatalogJavaAsync = null) =>
        new(
            new ApplicationPaths(root),
            static () => new NewMinecraftClientDefaultsSettings(),
            releaseCatalog,
            loaderCatalogs: [],
            onlineModpackWorkflow: workflow,
            curseForgeCredentialStore: credentialStore,
            curseForgeCredentialFileImportService: importer,
            curseForgeInstaller: curseForgeInstaller,
            resolveCatalogJavaAsync: resolveCatalogJavaAsync);

    private static OnlineModpackSearchResult CreateProject(
        string projectId = "curseforge-project",
        bool? allowsThirdPartyDistribution = null) =>
        new(
            OnlineModpackProvider.CurseForge,
            projectId,
            "CurseForge test project",
            "A deterministic test result.",
            "Test author",
            new Uri("https://www.curseforge.com/minecraft/modpacks/test-project"),
            downloadCount: 42,
            updatedAtUtc: DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            allowsThirdPartyDistribution: allowsThirdPartyDistribution);

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

        public List<(OnlineModpackSearchResult Project, OnlineModpackVersion Version, CancellationToken Token)> MetadataRequests { get; } = [];

        public Func<OnlineModpackVersion, CancellationToken, Task<OnlineModpackVersion>>? MetadataHandler { get; init; }

        public Task<OnlineModpackVersion> ResolveVersionMetadataAsync(
            OnlineModpackSearchResult project,
            OnlineModpackVersion version,
            SecureString? curseForgeApiKey = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(curseForgeApiKey);
            Assert.True(curseForgeApiKey.IsReadOnly());
            MetadataRequests.Add((project, version, cancellationToken));
            return MetadataHandler?.Invoke(version, cancellationToken) ?? Task.FromResult(version);
        }

        public Exception? BrowseError { get; init; }

        public Func<
            OnlineModpackBrowseRequest,
            CancellationToken,
            Task<IReadOnlyList<OnlineModpackSearchResult>>>? BrowseHandler { get; init; }

        public Func<
            OnlineModpackBrowseRequest,
            CancellationToken,
            Task<OnlineModpackBrowsePage>>? BrowsePageHandler { get; init; }

        public int VersionRequestCount { get; private set; }

        public bool CredentialWasSupplied { get; private set; }

        public bool CredentialWasReadOnly { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseAsync(
            OnlineModpackBrowseRequest request,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordRequest(request, transientApiKey);
            if (BrowseHandler is not null)
            {
                return BrowseHandler(request, cancellationToken);
            }

            return BrowseError is null
                ? Task.FromResult(Results)
                : Task.FromException<IReadOnlyList<OnlineModpackSearchResult>>(BrowseError);
        }

        public async Task<OnlineModpackBrowsePage> BrowsePageAsync(
            OnlineModpackBrowseRequest request,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            if (BrowsePageHandler is null)
            {
                var projects = await BrowseAsync(request, transientApiKey, cancellationToken);
                return new OnlineModpackBrowsePage(
                    projects,
                    request.Offset,
                    request.Limit,
                    TotalHits: null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RecordRequest(request, transientApiKey);
            return await BrowsePageHandler(request, cancellationToken);
        }

        private void RecordRequest(
            OnlineModpackBrowseRequest request,
            SecureString? transientApiKey)
        {
            Requests.Add(request);
            CredentialWasSupplied = transientApiKey is not null;
            CredentialWasReadOnly = transientApiKey?.IsReadOnly() == true;
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

    private sealed class StaticReleaseCatalog(params string[] versions) : IMinecraftReleaseCatalog
    {
        private readonly MinecraftReleaseCatalogSnapshot _snapshot = new(
            versions[0],
            DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            versions.Select((version, index) => new MinecraftReleaseInfo(
                    version,
                    DateTimeOffset.Parse("2026-01-02T03:04:05Z").AddDays(-index),
                    new Uri($"https://piston-meta.mojang.com/v1/packages/{index}/{version}.json"),
                    new string((char)('a' + index), 40),
                    1))
                .ToArray());

        public Task<MinecraftReleaseCatalogSnapshot> GetStableReleasesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class RecordingCurseForgeInstaller(string root) :
        ICurseForgeMinecraftClientPackInstaller
    {
        public List<Invocation> Invocations { get; } = [];

        public Exception? Error { get; init; }

        public Task<CurseForgeClientPackInstallResult> InstallAsync(
            CurseForgeClientPackInstallRequest request,
            SecureString apiKey,
            string? javaExecutablePath,
            IProgress<CurseForgeClientPackInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(new Invocation(
                request,
                ReadSecureString(apiKey),
                apiKey.IsReadOnly(),
                javaExecutablePath));
            if (Error is not null)
            {
                return Task.FromException<CurseForgeClientPackInstallResult>(Error);
            }

            var instance = new MinecraftClientInstance
            {
                Id = request.InstanceId,
                Name = request.Name,
                DirectoryPath = Path.Combine(root, "installed", request.InstanceId.ToString("N")),
                GameVersion = request.ExpectedMinecraftVersion ?? string.Empty,
                InstalledVersionId = $"curseforge-{request.ModId}-{request.FileId}",
                Loader = MinecraftClientLoader.Forge,
                JavaMajorVersion = request.JavaMajorVersion,
                JavaExecutablePath = javaExecutablePath,
            };
            return Task.FromResult(new CurseForgeClientPackInstallResult(
                instance,
                request.ModId,
                request.FileId,
                request.Name,
                request.Name,
                request.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                InstalledContentFiles: 0,
                SkippedOptionalFiles: 0,
                InstalledPaths: []));
        }

        private static string ReadSecureString(SecureString value)
        {
            var pointer = Marshal.SecureStringToBSTR(value);
            try
            {
                return Marshal.PtrToStringBSTR(pointer);
            }
            finally
            {
                Marshal.ZeroFreeBSTR(pointer);
            }
        }

        public sealed record Invocation(
            CurseForgeClientPackInstallRequest Request,
            string Credential,
            bool CredentialWasReadOnly,
            string? JavaExecutablePath);
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
