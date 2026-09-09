using System.IO;
using System.Net;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed partial class ClientCurseForgeCatalogTests
{
    [Fact]
    public async Task MetadataEnrichment_ShowsListImmediatelyUpdatesExactItemsAndSkipsKnownLoader()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var project = CreateProject("285109", allowsThirdPartyDistribution: true);
        var versions = new[]
        {
            CreateVersion(project.ProjectId, "4612979", "release", "1.12.2", ""),
            CreateVersion(project.ProjectId, "second", "release", "1.16.5", ""),
            CreateVersion(project.ProjectId, "third", "release", "1.20.1", ""),
            CreateVersion(project.ProjectId, "known", "release", "1.12.2", "Forge"),
        };
        var completions = versions.ToDictionary(v => v.VersionId, _ => NewMetadataCompletion());
        var workflow = new RecordingWorkflow
        {
            Versions = versions,
            MetadataHandler = (version, token) => completions[version.VersionId].Task.WaitAsync(token),
        };
        var store = new FakeCredentialStore(true);
        await using var vm = CreateViewModel(directory.Path, workflow, store,
            new FakeCredentialImporter(store, CurseForgeCredentialImportResult.Missing));

        vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 2);
        Assert.Equal(4, vm.CatalogVersions.Count);
        var selected = vm.SelectedCatalogVersion!;
        Assert.True(selected.IsResolvingLoaderMetadata);
        Assert.Equal(LocalizationService.Current.Get("client.vm.loader.readingMetadata"), selected.LoaderDisplay);
        var changes = new List<string?>();
        selected.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        completions[versions[0].VersionId].SetResult(versions[0] with { Loader = "Forge", MinecraftVersion = "1.12.2" });
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 3 && selected.LoaderDisplay == "Forge");
        Assert.Same(selected, vm.SelectedCatalogVersion);
        Assert.Equal("4612979", selected.CurseForgeVersion!.VersionId);
        Assert.Equal("Forge", selected.CurseForgeVersion.Loader);
        Assert.Equal("MC 1.12.2", selected.GameVersionDisplay);
        Assert.Contains(nameof(ClientCatalogVersionItemViewModel.Name), changes);
        Assert.Contains(nameof(ClientCatalogVersionItemViewModel.GameVersionDisplay), changes);
        Assert.Contains(nameof(ClientCatalogVersionItemViewModel.PackVersionDisplay), changes);
        Assert.All(workflow.MetadataRequests, request => Assert.Equal(project.ProjectId, request.Project.ProjectId));
        Assert.DoesNotContain(workflow.MetadataRequests, request => request.Version.VersionId == "known");

        completions["second"].SetResult(versions[1] with { Loader = "Fabric" });
        completions["third"].SetResult(versions[2] with { Loader = "NeoForge" });
        await WaitUntilAsync(() => vm.CatalogVersions.All(v => !v.IsResolvingLoaderMetadata));
        Assert.Equal(new[] { "Forge", "Fabric", "NeoForge", "Forge" }, vm.CatalogVersions.Select(v => v.LoaderDisplay));
    }

    [Fact]
    public async Task MetadataEnrichment_SelectionPrioritizesPendingItemWithAtMostTwoRequests()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var project = CreateProject();
        var versions = Enumerable.Range(1, 4).Select(i => CreateVersion(project.ProjectId, i.ToString(), "release", loader: "")).ToArray();
        var completions = versions.ToDictionary(v => v.VersionId, _ => NewMetadataCompletion());
        var workflow = new RecordingWorkflow
        {
            Versions = versions,
            MetadataHandler = (version, token) => completions[version.VersionId].Task.WaitAsync(token),
        };
        var store = new FakeCredentialStore(true);
        await using var vm = CreateViewModel(directory.Path, workflow, store,
            new FakeCredentialImporter(store, CurseForgeCredentialImportResult.Missing));

        vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 2);
        vm.SelectedCatalogVersion = vm.CatalogVersions[3];
        Assert.Equal(2, workflow.MetadataRequests.Count);
        completions["1"].SetResult(versions[0] with { Loader = "Forge" });
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 3);
        Assert.Equal("4", workflow.MetadataRequests[2].Version.VersionId);
        completions["2"].SetResult(versions[1] with { Loader = "Fabric" });
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 4);
        Assert.Equal("3", workflow.MetadataRequests[3].Version.VersionId);
        completions["4"].SetResult(versions[3] with { Loader = "Quilt" });
        completions["3"].SetResult(versions[2] with { Loader = "NeoForge" });
        await WaitUntilAsync(() => vm.CatalogVersions.All(v => !v.IsResolvingLoaderMetadata));
        Assert.Equal("4", vm.SelectedCatalogVersion!.CurseForgeVersion!.VersionId);
    }

    [Fact]
    public async Task MetadataEnrichment_LoaderFilterKeepsPendingThenRemovesOnlyResolvedMismatch()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var project = CreateProject();
        var versions = new[]
        {
            CreateVersion(project.ProjectId, "101", "release", loader: "") with { VersionName = "Example v1.0" },
            CreateVersion(project.ProjectId, "102", "release", loader: "") with { VersionName = "Example v2.0" },
        };
        var first = NewMetadataCompletion();
        var second = NewMetadataCompletion();
        var workflow = new RecordingWorkflow
        {
            Versions = versions,
            MetadataHandler = (v, token) => (v.VersionId == "101" ? first : second).Task.WaitAsync(token),
        };
        var store = new FakeCredentialStore(true);
        await using var vm = CreateViewModel(directory.Path, workflow, store,
            new FakeCredentialImporter(store, CurseForgeCredentialImportResult.Missing));
        vm.SelectedCatalogLoader = Assert.Single(vm.CatalogLoaders, choice => choice.Loader == MinecraftClientLoader.Forge);

        vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 2);
        Assert.Equal(2, vm.CatalogVersions.Count);
        second.SetResult(versions[1] with { Loader = "Forge" });
        await WaitUntilAsync(() => vm.CatalogVersions[1].LoaderDisplay == "Forge");
        first.SetResult(versions[0] with { Loader = "Fabric" });
        await WaitUntilAsync(() => vm.CatalogVersions.Count == 1);
        Assert.Equal("102", Assert.Single(vm.CatalogVersions).CurseForgeVersion!.VersionId);
        Assert.Same(vm.CatalogVersions[0], vm.SelectedCatalogVersion);
        Assert.Equal("Forge", vm.SelectedCatalogVersion!.LoaderDisplay);
    }

    [Fact]
    public async Task MetadataEnrichment_ProjectChangeCancelsAndIgnoresLateTransportCompletion()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var oldProject = CreateProject("old");
        var currentProject = CreateProject("current");
        var oldVersion = CreateVersion("old", "same-file-label", "release", loader: "");
        var completion = NewMetadataCompletion();
        var workflow = new RecordingWorkflow
        {
            Versions = [oldVersion, CreateVersion("current", "same-file-label", "release", loader: "Fabric")],
            // Deliberately ignore cancellation to exercise stale-result checks after completion.
            MetadataHandler = (_, _) => completion.Task,
        };
        var store = new FakeCredentialStore(true);
        await using var vm = CreateViewModel(directory.Path, workflow, store,
            new FakeCredentialImporter(store, CurseForgeCredentialImportResult.Missing));

        vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(oldProject);
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 1);
        var oldItem = vm.SelectedCatalogVersion!;
        vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(currentProject);
        await WaitUntilAsync(() => vm.SelectedCatalogVersion?.CurseForgeVersion?.ProjectId == "current");
        Assert.True(workflow.MetadataRequests[0].Token.IsCancellationRequested);
        completion.SetResult(oldVersion with { Loader = "Forge" });
        await Task.Yield();
        Assert.Equal("Fabric", vm.SelectedCatalogVersion!.LoaderDisplay);
        Assert.Equal("current", Assert.Single(vm.CatalogVersions).CurseForgeVersion!.ProjectId);
        Assert.Equal(string.Empty, oldItem.CurseForgeVersion!.Loader);
    }

    [Fact]
    public async Task MetadataEnrichment_FailureDoesNotInventLoaderAndReselectRetriesExactFile()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var project = CreateProject();
        var failedVersion = CreateVersion(project.ProjectId, "unknown-file", "release", loader: "");
        var attempt = 0;
        var workflow = new RecordingWorkflow
        {
            Versions = [failedVersion, CreateVersion(project.ProjectId, "known", "release")],
            MetadataHandler = (v, _) => ++attempt == 1
                ? Task.FromException<OnlineModpackVersion>(new InvalidDataException("Missing primary loader"))
                : Task.FromResult(v with { Loader = "Forge" }),
        };
        var store = new FakeCredentialStore(true);
        await using var vm = CreateViewModel(directory.Path, workflow, store,
            new FakeCredentialImporter(store, CurseForgeCredentialImportResult.Missing));

        vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 1 && !vm.CatalogVersions[0].IsResolvingLoaderMetadata);
        var item = vm.CatalogVersions[0];
        Assert.Equal(LocalizationService.Current.Get("client.vm.loader.metadataUnavailable"), item.LoaderDisplay);
        Assert.Equal(string.Empty, item.CurseForgeVersion!.Loader);
        vm.SelectedCatalogVersion = vm.CatalogVersions[1];
        vm.SelectedCatalogVersion = item;
        await WaitUntilAsync(() => item.LoaderDisplay == "Forge");
        Assert.Equal(2, workflow.MetadataRequests.Count);
        Assert.All(workflow.MetadataRequests, request => Assert.Equal("unknown-file", request.Version.VersionId));
        Assert.Equal(string.Empty, vm.ErrorText);
    }

    [Theory]
    [InlineData(CurseForgeApiErrorCode.RateLimited, HttpStatusCode.TooManyRequests, "client.vm.catalog.curseForge.rateLimited", true)]
    [InlineData(CurseForgeApiErrorCode.InvalidApiKey, HttpStatusCode.Unauthorized, "client.vm.catalog.curseForge.invalidCredential", false)]
    public async Task MetadataEnrichment_AccountWideApiFailureStopsWholeBatchAndResetsPendingItems(
        CurseForgeApiErrorCode errorCode,
        HttpStatusCode statusCode,
        string localizationKey,
        bool expectedCredentialState)
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var project = CreateProject(allowsThirdPartyDistribution: true);
        var rejected = NewMetadataCompletion();
        var inFlight = NewMetadataCompletion();
        var workflow = new RecordingWorkflow
        {
            Versions = Enumerable.Range(1, 50)
                .Select(i => CreateVersion(project.ProjectId, i.ToString(), "release", loader: ""))
                .ToArray(),
            MetadataHandler = (version, token) =>
                (version.VersionId == "1" ? rejected : inFlight).Task.WaitAsync(token),
        };
        var store = new FakeCredentialStore(true);
        await using var vm = CreateViewModel(directory.Path, workflow, store,
            new FakeCredentialImporter(store, CurseForgeCredentialImportResult.Missing));

        vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 2);
        Assert.Equal(50, vm.CatalogVersions.Count);
        rejected.SetException(new CurseForgeApiException(errorCode, statusCode));
        var expectedMessage = LocalizationService.Current.Get(localizationKey);
        await WaitUntilAsync(() => vm.CatalogStatusText == expectedMessage &&
            vm.CatalogVersions.All(item => !item.IsResolvingLoaderMetadata));

        Assert.Equal(expectedMessage, vm.ErrorText);
        Assert.Equal(expectedCredentialState, vm.HasCurseForgeCredential);
        Assert.Equal(2, workflow.MetadataRequests.Count);
        Assert.All(workflow.MetadataRequests, request => Assert.True(request.Token.IsCancellationRequested));
        Assert.All(vm.CatalogVersions, item => Assert.Equal(
            LocalizationService.Current.Get("client.vm.loader.metadataUnavailable"), item.LoaderDisplay));
        vm.SelectedCatalogVersion = vm.CatalogVersions[2];
        Assert.Equal(2, workflow.MetadataRequests.Count);
        if (!expectedCredentialState)
        {
            Assert.False(vm.InstallCatalogPackCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task MetadataEnrichment_ManifestWithoutModLoadersDisplaysVanillaAndDoesNotRetry()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var project = CreateProject();
        var workflow = new RecordingWorkflow
        {
            Versions =
            [
                CreateVersion(project.ProjectId, "101", "release", loader: ""),
                CreateVersion(project.ProjectId, "102", "release", loader: "Forge"),
            ],
            // The exact-file resolver maps an explicitly empty manifest modLoaders list to Vanilla.
            MetadataHandler = (version, _) => Task.FromResult(version with { Loader = "Vanilla" }),
        };
        var store = new FakeCredentialStore(true);
        await using var vm = CreateViewModel(directory.Path, workflow, store,
            new FakeCredentialImporter(store, CurseForgeCredentialImportResult.Missing));

        vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
        await WaitUntilAsync(() => workflow.MetadataRequests.Count == 1 &&
            !vm.CatalogVersions[0].IsResolvingLoaderMetadata);
        var item = vm.CatalogVersions[0];
        Assert.False(item.NeedsLoaderMetadata);
        Assert.Equal("Vanilla", item.CurseForgeVersion!.Loader);
        Assert.Equal(LocalizationService.Current.Get("client.vm.loader.vanilla"), item.LoaderDisplay);
        Assert.EndsWith($" · {item.LoaderDisplay}", item.Name, StringComparison.Ordinal);

        vm.SelectedCatalogVersion = vm.CatalogVersions[1];
        vm.SelectedCatalogVersion = item;

        Assert.Single(workflow.MetadataRequests);
        Assert.False(item.IsResolvingLoaderMetadata);
        Assert.False(item.NeedsLoaderMetadata);
    }

    [Fact]
    public async Task MetadataEnrichment_DisposeCancelsBothActiveRequestsWithoutStartingQueuedItems()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var project = CreateProject();
        var completions = NewMetadataCompletion();
        var workflow = new RecordingWorkflow
        {
            Versions = Enumerable.Range(1, 5)
                .Select(i => CreateVersion(project.ProjectId, i.ToString(), "release", loader: ""))
                .ToArray(),
            MetadataHandler = (_, token) => completions.Task.WaitAsync(token),
        };
        var store = new FakeCredentialStore(true);
        var vm = CreateViewModel(directory.Path, workflow, store,
            new FakeCredentialImporter(store, CurseForgeCredentialImportResult.Missing));
        try
        {
            vm.SelectedCatalogProject = new ClientModpackProjectItemViewModel(project);
            await WaitUntilAsync(() => workflow.MetadataRequests.Count == 2);
        }
        finally
        {
            await vm.DisposeAsync();
        }

        Assert.Equal(2, workflow.MetadataRequests.Count);
        Assert.All(workflow.MetadataRequests, request => Assert.True(request.Token.IsCancellationRequested));
    }

    [Theory]
    [InlineData("other-project", "file")]
    [InlineData("project", "other-file")]
    public void MetadataEnrichment_ItemRejectsMismatchedProjectOrFile(string projectId, string fileId)
    {
        var original = CreateVersion("project", "file", "release", loader: "");
        var item = new ClientCatalogVersionItemViewModel(original, "Example");
        item.BeginLoaderMetadataResolution();

        item.ApplyResolvedMetadata(original with
        {
            ProjectId = projectId,
            VersionId = fileId,
            Loader = "Forge",
        });

        Assert.Same(original, item.CurseForgeVersion);
        Assert.Equal(LocalizationService.Current.Get("client.vm.loader.metadataUnavailable"), item.LoaderDisplay);
    }

    private static TaskCompletionSource<OnlineModpackVersion> NewMetadataCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
