using System.Security;
using System.Windows.Media;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackViewModelTests
{
    [Fact]
    public void Providers_ExposeAllBuiltInSourcesAndMarkCurseForgeAsRequiringTransientCredential()
    {
        var viewModel = new OnlineModpackViewModel(new FakeWorkflow());

        Assert.Equal(
            [OnlineModpackProvider.Ftb, OnlineModpackProvider.Modrinth, OnlineModpackProvider.CurseForge],
            viewModel.Providers.Select(item => item.Provider));
        Assert.False(Assert.Single(viewModel.Providers, item =>
            item.Provider == OnlineModpackProvider.Ftb).RequiresApiKey);
        Assert.False(Assert.Single(viewModel.Providers, item =>
            item.Provider == OnlineModpackProvider.Modrinth).RequiresApiKey);
        Assert.True(Assert.Single(viewModel.Providers, item =>
            item.Provider == OnlineModpackProvider.CurseForge).RequiresApiKey);
    }

    [Fact]
    public void SelectedProvider_AcceptsOnlyChoicesOwnedByThisViewModel()
    {
        var viewModel = new OnlineModpackViewModel(new FakeWorkflow());
        var modrinth = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);

        viewModel.SelectedProvider = modrinth;

        Assert.Same(modrinth, viewModel.SelectedProvider);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            viewModel.SelectedProvider = new(
                OnlineModpackProvider.Ftb,
                "FTB",
                RequiresApiKey: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            viewModel.SelectedProvider = new(
                OnlineModpackProvider.CurseForge,
                "CurseForge",
                RequiresApiKey: true));
        Assert.Same(modrinth, viewModel.SelectedProvider);
    }

    [Fact]
    public async Task SearchAsync_TransitionsThroughSearchingAndPublishesOnlyMatchingResults()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<OnlineModpackSearchResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeWorkflow { SearchTask = completion.Task };
        var viewModel = new OnlineModpackViewModel(workflow) { SearchQuery = " skies " };

        var operation = viewModel.SearchAsync(null);

        Assert.True(viewModel.IsBusy);
        Assert.Equal(OnlineModpackOperationState.Searching, viewModel.OperationState);
        Assert.True(viewModel.IsProgressIndeterminate);
        Assert.False(viewModel.CanSearch);
        Assert.Equal((OnlineModpackProvider.Ftb, "skies"), workflow.LastSearch);

        completion.SetResult(
        [
            Project(OnlineModpackProvider.Ftb, "ftb-result", "FTB Skies"),
            Project(OnlineModpackProvider.Modrinth, "wrong-provider", "Ignored")
        ]);
        await operation;

        var result = Assert.Single(viewModel.Results);
        Assert.Equal("FTB Skies", result.Name);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(OnlineModpackOperationState.Idle, viewModel.OperationState);
        Assert.Contains("1 個", viewModel.StageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_MapsEverySelectedFilterToOneBrowseRequest()
    {
        var workflow = new FakeWorkflow
        {
            SearchResults = [Project(OnlineModpackProvider.Modrinth, "filtered", "Filtered Pack")]
        };
        var viewModel = new OnlineModpackViewModel(workflow)
        {
            SearchQuery = "  performance  "
        };
        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);
        viewModel.SelectedSort = Assert.Single(
            viewModel.SortChoices,
            item => item.Sort == OnlineModpackSort.RecentlyUpdated);
        viewModel.SelectedGameVersion = Assert.Single(
            viewModel.GameVersionChoices,
            item => item.Key == "1.20.1");
        viewModel.SelectedLoader = Assert.Single(
            viewModel.LoaderChoices,
            item => item.Key == "neoforge");
        viewModel.SelectedCategory = Assert.Single(
            viewModel.CategoryChoices,
            item => item.Key == "magic");

        await viewModel.SearchAsync(null);

        var request = Assert.IsType<OnlineModpackBrowseRequest>(workflow.LastBrowseRequest);
        Assert.Equal(OnlineModpackProvider.Modrinth, request.Provider);
        Assert.Equal("performance", request.Query);
        Assert.Equal(OnlineModpackSort.RecentlyUpdated, request.Sort);
        Assert.Equal("1.20.1", request.GameVersion);
        Assert.Equal("neoforge", request.Loader);
        Assert.Equal("magic", request.SourceCategory);
        Assert.Equal(20, request.Limit);
        Assert.Single(viewModel.CatalogItems);
    }

    [Fact]
    public void ProviderFilters_ShowOnlyCapabilitiesThatHaveRealMappings()
    {
        var viewModel = new OnlineModpackViewModel(new FakeWorkflow());

        Assert.False(viewModel.HasCategories);
        Assert.DoesNotContain(viewModel.CategoryChoices, item => item.Key.Length > 0);

        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);
        Assert.True(viewModel.HasCategories);
        Assert.Contains(viewModel.CategoryChoices, item => item.Key == "optimization");
        Assert.Contains(viewModel.LoaderChoices, item => item.Key == "quilt");

        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.CurseForge);
        Assert.True(viewModel.IsSelectedProviderAvailable);
        Assert.True(viewModel.IsCurseForgeSelected);
        Assert.True(viewModel.CanLoadFeatured);
        Assert.Contains("官方 API Key", viewModel.ProviderAvailabilityText, StringComparison.Ordinal);
        Assert.Contains("不會儲存", viewModel.ProviderAvailabilityText, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseCriteriaChanged_RaisesOnceForEveryCatalogToggleAfterStateIsUpdated()
    {
        var viewModel = new OnlineModpackViewModel(new FakeWorkflow());
        var observedRequests = new List<OnlineModpackBrowseRequest>();
        viewModel.BrowseCriteriaChanged += (_, _) =>
            observedRequests.Add(viewModel.BuildCurrentBrowseRequest());

        var modrinth = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);
        viewModel.SelectedProvider = modrinth;
        viewModel.SelectedSort = Assert.Single(
            viewModel.SortChoices,
            item => item.Sort == OnlineModpackSort.RecentlyUpdated);
        viewModel.SelectedGameVersion = Assert.Single(
            viewModel.GameVersionChoices,
            item => item.Key == "1.20.1");
        viewModel.SelectedLoader = Assert.Single(
            viewModel.LoaderChoices,
            item => item.Key == "neoforge");
        viewModel.SelectedCategory = Assert.Single(
            viewModel.CategoryChoices,
            item => item.Key == "magic");

        Assert.Equal(5, observedRequests.Count);
        var final = observedRequests[^1];
        Assert.Equal(OnlineModpackProvider.Modrinth, final.Provider);
        Assert.Equal(OnlineModpackSort.RecentlyUpdated, final.Sort);
        Assert.Equal("1.20.1", final.GameVersion);
        Assert.Equal("neoforge", final.Loader);
        Assert.Equal("magic", final.SourceCategory);

        viewModel.SelectedProvider = modrinth;
        viewModel.SelectedSort = viewModel.SelectedSort;
        viewModel.SelectedGameVersion = viewModel.SelectedGameVersion;
        viewModel.SelectedLoader = viewModel.SelectedLoader;
        viewModel.SelectedCategory = viewModel.SelectedCategory;
        Assert.Equal(5, observedRequests.Count);
    }

    [Fact]
    public void ResultLimit_DefaultsToTwentyAndFlowsIntoEveryBrowseRequest()
    {
        var viewModel = new OnlineModpackViewModel(new FakeWorkflow());
        var observedRequests = new List<OnlineModpackBrowseRequest>();
        viewModel.BrowseCriteriaChanged += (_, _) =>
            observedRequests.Add(viewModel.BuildCurrentBrowseRequest());

        Assert.Equal([20, 40, 60, 100], viewModel.ResultLimitChoices);
        Assert.Equal(20, viewModel.SelectedResultLimit);
        Assert.Equal(20, viewModel.BuildCurrentBrowseRequest().Limit);

        viewModel.SelectedResultLimit = 100;

        var request = Assert.Single(observedRequests);
        Assert.Equal(100, request.Limit);
        Assert.Equal(100, viewModel.BuildCurrentBrowseRequest().Limit);
        Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.SelectedResultLimit = 30);
        Assert.Equal(100, viewModel.SelectedResultLimit);
    }

    [Fact]
    public async Task RefreshCurrentCatalogAsync_PreservesTheLastExplicitBrowseMode()
    {
        var workflow = new FakeWorkflow();
        var viewModel = new OnlineModpackViewModel(workflow)
        {
            SearchQuery = "  retained query  "
        };
        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);

        await viewModel.LoadFeaturedAsync(null);

        Assert.Equal(OnlineModpackBrowseMode.Featured, viewModel.BrowseMode);
        Assert.Equal(string.Empty, workflow.LastBrowseRequest?.Query);

        await viewModel.RefreshCurrentCatalogAsync(null);

        Assert.Equal(string.Empty, workflow.LastBrowseRequest?.Query);

        await viewModel.SearchAsync(null);

        Assert.Equal(OnlineModpackBrowseMode.Search, viewModel.BrowseMode);
        Assert.Equal("retained query", workflow.LastBrowseRequest?.Query);
        Assert.Equal((OnlineModpackProvider.Modrinth, "retained query"), workflow.LastSearch);

        await viewModel.RefreshCurrentCatalogAsync(null);

        Assert.Equal("retained query", workflow.LastBrowseRequest?.Query);

        await viewModel.LoadFeaturedAsync(null);
        await viewModel.RefreshCurrentCatalogAsync(null);

        Assert.Equal(OnlineModpackBrowseMode.Featured, viewModel.BrowseMode);
        Assert.Equal(string.Empty, workflow.LastBrowseRequest?.Query);
        Assert.Equal(OnlineModpackProvider.Modrinth, workflow.LastFeatured);
    }

    [Fact]
    public async Task SearchAsync_CardsBindOnlyTheLocalArtworkCacheResult()
    {
        const string localPath = "C:\\cache\\catalog\\pack.png";
        var project = new OnlineModpackSearchResult(
            OnlineModpackProvider.Modrinth,
            "safe-art",
            "Safe Art",
            "Summary",
            "Author",
            iconUri: new Uri("https://cdn.modrinth.com/data/safe/icon.png"));
        var workflow = new FakeWorkflow
        {
            SearchResults = [project],
            ArtworkCache = new StubArtworkCache(localPath)
        };
        var viewModel = new OnlineModpackViewModel(workflow, new StubArtworkDecoder())
        {
            SearchQuery = "safe"
        };
        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);

        await viewModel.SearchAsync(null);

        var card = Assert.Single(viewModel.CatalogItems);
        Assert.Same(project, card.Project);
        Assert.Equal(localPath, card.ArtworkPath);
        Assert.True(card.HasArtwork);
    }

    [Fact]
    public async Task SearchAsync_ArtworkUsesBoundedCandidatesAndFallsBackToUncroppedIcon()
    {
        var preview1 = new Uri("https://cdn.modrinth.com/data/safe/preview-1.webp");
        var preview2 = new Uri("https://cdn.modrinth.com/data/safe/preview-2.webp");
        var icon = new Uri("https://cdn.modrinth.com/data/safe/icon.webp");
        var project = new OnlineModpackSearchResult(
            OnlineModpackProvider.Modrinth,
            "candidate-art",
            "Candidate Art",
            "Summary",
            "Author",
            iconUri: icon,
            previewImageUri: preview1,
            previewImageUriCandidates: [preview2]);
        var cache = new SequencedArtworkCache(new Dictionary<string, string?>
        {
            [preview1.AbsoluteUri] = null,
            [preview2.AbsoluteUri] = "C:\\cache\\catalog\\invalid.webp",
            [icon.AbsoluteUri] = "C:\\cache\\catalog\\icon.webp"
        });
        var workflow = new FakeWorkflow
        {
            SearchResults = [project],
            ArtworkCache = cache
        };
        var viewModel = new OnlineModpackViewModel(workflow, new CandidateArtworkDecoder())
        {
            SearchQuery = "candidate"
        };
        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);

        await viewModel.SearchAsync(null);
        var card = Assert.Single(viewModel.CatalogItems);
        await WaitUntilAsync(() => card.ArtworkState != OnlineModpackArtworkState.Loading);

        Assert.Equal([preview1, preview2, icon], cache.RequestedUris);
        Assert.Equal(OnlineModpackArtworkState.Ready, card.ArtworkState);
        Assert.Equal(Stretch.Uniform, card.ArtworkStretch);
        Assert.Equal("C:\\cache\\catalog\\icon.webp", card.ArtworkPath);
    }

    [Fact]
    public async Task LoadFeaturedAsync_PublishesProviderRecommendationsAndUsesAccurateProgressState()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<OnlineModpackSearchResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeWorkflow { FeaturedTask = completion.Task };
        var viewModel = new OnlineModpackViewModel(workflow);

        var operation = viewModel.LoadFeaturedAsync(null);

        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.IsProgressIndeterminate);
        Assert.False(viewModel.ShowProgressPercentage);
        Assert.Equal(OnlineModpackProvider.Ftb, workflow.LastFeatured);

        completion.SetResult(
        [
            Project(OnlineModpackProvider.Ftb, "featured", "FTB Featured"),
            Project(OnlineModpackProvider.Modrinth, "wrong", "Ignored")
        ]);
        await operation;

        Assert.Equal("FTB Featured", Assert.Single(viewModel.Results).Name);
        Assert.Equal("FTB 熱門推薦", viewModel.ResultsHeading);
        Assert.Contains("熱門推薦", viewModel.StageText, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.ShowProgressPercentage);
    }

    [Fact]
    public async Task ProviderSwitch_LateFeaturedCannotOverwriteCurrentSearchResults()
    {
        var workflow = new GenerationRaceWorkflow();
        var viewModel = new OnlineModpackViewModel(workflow);

        var staleOperation = viewModel.LoadFeaturedAsync(null);
        Assert.True(viewModel.IsBusy);
        Assert.NotNull(workflow.PendingFeaturedCall);

        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);
        Assert.True(workflow.PendingFeaturedCancellation.IsCancellationRequested);

        viewModel.SearchQuery = "current search";
        await viewModel.SearchAsync(null);
        Assert.Equal("Current Modrinth Search", Assert.Single(viewModel.Results).Name);

        workflow.CompleteStaleFeatured(
            Project(OnlineModpackProvider.Ftb, "stale-featured", "Stale FTB Featured"));
        await staleOperation;

        var current = Assert.Single(viewModel.Results);
        Assert.Equal(OnlineModpackProvider.Modrinth, current.Provider);
        Assert.Equal("Current Modrinth Search", current.Name);
        Assert.Equal("Modrinth 搜尋結果", viewModel.ResultsHeading);
        Assert.Equal(OnlineModpackOperationState.Idle, viewModel.OperationState);
    }

    [Fact]
    public async Task ProviderSwitch_LateSearchCannotOverwriteCurrentFeaturedResults()
    {
        var workflow = new GenerationRaceWorkflow();
        var viewModel = new OnlineModpackViewModel(workflow) { SearchQuery = "stale search" };

        var staleOperation = viewModel.SearchAsync(null);
        Assert.True(viewModel.IsBusy);
        Assert.NotNull(workflow.PendingSearchCall);

        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);
        Assert.True(workflow.PendingSearchCancellation.IsCancellationRequested);

        await viewModel.LoadFeaturedAsync(null);
        Assert.Equal("Current Modrinth Featured", Assert.Single(viewModel.Results).Name);

        workflow.CompleteStaleSearch(
            Project(OnlineModpackProvider.Ftb, "stale-search", "Stale FTB Search"));
        await staleOperation;

        var current = Assert.Single(viewModel.Results);
        Assert.Equal(OnlineModpackProvider.Modrinth, current.Provider);
        Assert.Equal("Current Modrinth Featured", current.Name);
        Assert.Equal("Modrinth 熱門推薦", viewModel.ResultsHeading);
        Assert.Equal(OnlineModpackOperationState.Idle, viewModel.OperationState);
    }

    [Fact]
    public void ViewModel_ExposesDisabledCurseForgeChoiceButDoesNotRetainCredentialState()
    {
        var viewModel = new OnlineModpackViewModel(new FakeWorkflow());

        Assert.True(Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.CurseForge).RequiresApiKey);
        Assert.DoesNotContain(
            typeof(OnlineModpackViewModel).GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(SecureString)
                     || field.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                     || field.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectingProject_LoadsVersionsAndPrefersAnInstallableServerPack()
    {
        var result = Project(OnlineModpackProvider.Modrinth, "project", "Adrenaline");
        var unavailable = Version(result, "client", false);
        var installable = Version(result, "server", true);
        var workflow = new FakeWorkflow
        {
            SearchResults = [result],
            VersionResults = [unavailable, installable]
        };
        var viewModel = new OnlineModpackViewModel(workflow) { SearchQuery = "adrenaline" };
        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);
        await viewModel.SearchAsync(null);

        await viewModel.SelectResultAsync(Assert.Single(viewModel.Results), null);

        Assert.Equal(2, viewModel.Versions.Count);
        Assert.Same(installable, viewModel.SelectedVersion);
        Assert.Equal("Adrenaline", viewModel.ServerName);
        Assert.True(viewModel.CanInstall);

        viewModel.SelectedVersion = unavailable;
        Assert.False(viewModel.CanInstall);
        Assert.Contains("無法直接安裝", viewModel.SelectedVersionAvailability, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FtbInstall_RequiresFreshExplicitMinecraftEulaConsentAndCarriesItInRequest()
    {
        var result = Project(OnlineModpackProvider.Ftb, "ftb-project", "FTB Pack");
        var version = Version(result, "server", true);
        var workflow = new FakeWorkflow
        {
            SearchResults = [result],
            VersionResults = [version]
        };
        var viewModel = new OnlineModpackViewModel(workflow) { SearchQuery = "ftb" };

        await viewModel.SearchAsync(null);
        await viewModel.SelectResultAsync(Assert.Single(viewModel.Results), null);

        Assert.True(viewModel.RequiresMinecraftEulaAcceptance);
        Assert.False(viewModel.MinecraftEulaAccepted);
        Assert.False(viewModel.CanInstall);
        Assert.False(viewModel.TryBuildInstallRequest(out _));
        Assert.Contains("Minecraft EULA", viewModel.ErrorMessage, StringComparison.Ordinal);

        viewModel.MinecraftEulaAccepted = true;

        Assert.True(viewModel.CanInstall);
        Assert.True(viewModel.TryBuildInstallRequest(out var request));
        Assert.True(request.MinecraftEulaAccepted);

        await viewModel.SearchAsync(null);
        Assert.False(viewModel.MinecraftEulaAccepted);
        Assert.False(viewModel.CanInstall);
    }

    [Fact]
    public async Task SelectingFilteredProject_KeepsGameVersionAndLoaderAppliedToVersionChoices()
    {
        var result = Project(OnlineModpackProvider.Modrinth, "filtered-project", "Filtered Pack");
        var matching = new OnlineModpackVersion(
            result.Provider,
            result.ProjectId,
            "matching",
            "Matching",
            "1.20.1",
            "NeoForge 21.1",
            "release",
            DateTimeOffset.UtcNow,
            true);
        var wrongGame = matching with { VersionId = "wrong-game", MinecraftVersion = "1.21.1" };
        var wrongLoader = matching with { VersionId = "wrong-loader", Loader = "Fabric 0.16" };
        var workflow = new FakeWorkflow
        {
            SearchResults = [result],
            VersionResults = [wrongGame, wrongLoader, matching]
        };
        var viewModel = new OnlineModpackViewModel(workflow) { SearchQuery = "filtered" };
        viewModel.SelectedProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);
        viewModel.SelectedGameVersion = Assert.Single(
            viewModel.GameVersionChoices,
            item => item.Key == "1.20.1");
        viewModel.SelectedLoader = Assert.Single(
            viewModel.LoaderChoices,
            item => item.Key == "neoforge");

        await viewModel.SearchAsync(null);
        await viewModel.SelectResultAsync(Assert.Single(viewModel.Results), null);

        Assert.Same(matching, Assert.Single(viewModel.Versions));
        Assert.Same(matching, viewModel.SelectedVersion);
    }

    [Fact]
    public async Task InstallAsync_ReportsProgressAndReturnsInstalledServer()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            var result = Project(OnlineModpackProvider.Ftb, "project", "FTB Skies");
            var version = Version(result, "1.6.0", true);
            var installed = new ServerInstance
            {
                Name = "My FTB Server",
                DirectoryPath = "C:\\servers\\my-ftb",
                ServerJarPath = "server.jar"
            };
            var workflow = new FakeWorkflow
            {
                SearchResults = [result],
                VersionResults = [version],
                InstalledServer = installed,
                ProgressToReport = new(
                    OnlineModpackInstallStage.Verifying,
                    "正在驗證檔案…",
                    42,
                    "16 線平行下載｜預估剩餘 20 秒")
            };
            var viewModel = await ReadyForInstallAsync(workflow, "My FTB Server");
            var installedRaised = false;
            viewModel.Installed += (_, _) => installedRaised = true;

            await viewModel.InstallAsync(null);

            Assert.True(installedRaised);
            Assert.Same(installed, viewModel.InstalledServer);
            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.IsInstalling);
            Assert.Equal(100, viewModel.ProgressPercentage);
            Assert.False(viewModel.IsProgressIndeterminate);
            Assert.Contains("安裝完成", viewModel.StageText, StringComparison.Ordinal);
            Assert.Equal(string.Empty, viewModel.ProgressDetailText);
            Assert.Equal("My FTB Server", workflow.LastInstallRequest?.ServerName);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task CancellingInstall_TransitionsThroughCancellingAndDoesNotReturnAServer()
    {
        var workflow = new FakeWorkflow { BlockInstallUntilCancelled = true };
        var viewModel = await ReadyForInstallAsync(workflow, "Cancel Me");
        var observedStates = new List<OnlineModpackOperationState>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.OperationState))
            {
                observedStates.Add(viewModel.OperationState);
            }
        };

        var operation = viewModel.InstallAsync(null);
        await workflow.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsInstalling);
        Assert.Equal(OnlineModpackOperationState.Installing, viewModel.OperationState);

        viewModel.CancelCurrentOperation();

        await operation;

        Assert.Contains(OnlineModpackOperationState.Cancelling, observedStates);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsInstalling);
        Assert.Null(viewModel.InstalledServer);
        Assert.Contains("安裝已取消", viewModel.StageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderChange_IsRejectedUntilCancelledInstallRollbackCompletes()
    {
        var workflow = new FakeWorkflow
        {
            BlockInstallUntilCancelled = true,
            BlockInstallRollbackUntilReleased = true
        };
        var viewModel = await ReadyForInstallAsync(workflow, "Protected Install");
        var originalProvider = viewModel.SelectedProvider;
        var otherProvider = Assert.Single(
            viewModel.Providers,
            item => item.Provider == OnlineModpackProvider.Modrinth);

        var operation = viewModel.InstallAsync(null);
        try
        {
            await workflow.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(viewModel.IsInstalling);
            Assert.False(viewModel.CanChangeProvider);
            viewModel.SelectedProvider = otherProvider;
            Assert.Same(originalProvider, viewModel.SelectedProvider);
            Assert.True(viewModel.IsInstalling);

            viewModel.CancelCurrentOperation();
            await workflow.InstallRollbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(OnlineModpackOperationState.Cancelling, viewModel.OperationState);
            Assert.True(viewModel.IsInstalling);
            Assert.False(viewModel.CanChangeProvider);
            viewModel.SelectedProvider = otherProvider;
            Assert.Same(originalProvider, viewModel.SelectedProvider);
            Assert.True(viewModel.IsInstalling);
        }
        finally
        {
            workflow.ReleaseInstallRollback.TrySetResult(true);
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.False(viewModel.IsInstalling);
        Assert.True(viewModel.CanChangeProvider);
        viewModel.SelectedProvider = otherProvider;
        Assert.Same(otherProvider, viewModel.SelectedProvider);
    }

    [Fact]
    public async Task InstallProgress_ExposesDetailOnSeparatePropertyAndClearsItAfterCancellation()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            var workflow = new FakeWorkflow
            {
                BlockInstallUntilCancelled = true,
                ProgressToReport = new OnlineModpackInstallProgress(
                    OnlineModpackInstallStage.Downloading,
                    "FTB 正在下載 Server Pack 檔案：100 / 1,000（10.0%）",
                    38,
                    "16 線平行下載｜預估剩餘 20 秒")
            };
            var viewModel = await ReadyForInstallAsync(workflow, "Progress Detail");

            var operation = viewModel.InstallAsync(null);
            await workflow.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains("100 / 1,000", viewModel.StageText, StringComparison.Ordinal);
            Assert.Equal("16 線平行下載｜預估剩餘 20 秒", viewModel.ProgressDetailText);
            Assert.Equal(38, viewModel.ProgressPercentage);

            viewModel.CancelCurrentOperation();
            await operation;

            Assert.Equal(string.Empty, viewModel.ProgressDetailText);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static async Task<OnlineModpackViewModel> ReadyForInstallAsync(
        FakeWorkflow workflow,
        string serverName)
    {
        var result = workflow.SearchResults.FirstOrDefault()
            ?? Project(OnlineModpackProvider.Ftb, "project", "Pack");
        var version = workflow.VersionResults.FirstOrDefault()
            ?? Version(result, "version", true);
        workflow.SearchResults = [result];
        workflow.VersionResults = [version];
        var viewModel = new OnlineModpackViewModel(workflow) { SearchQuery = "pack" };
        await viewModel.SearchAsync(null);
        await viewModel.SelectResultAsync(Assert.Single(viewModel.Results), null);
        viewModel.ServerName = serverName;
        if (result.Provider == OnlineModpackProvider.Ftb)
        {
            viewModel.MinecraftEulaAccepted = true;
        }
        Assert.True(viewModel.CanInstall);
        return viewModel;
    }

    private static OnlineModpackSearchResult Project(
        OnlineModpackProvider provider,
        string id,
        string name)
        => new(provider, id, name, "Performance modpack", "Example Author");

    private static OnlineModpackVersion Version(
        OnlineModpackSearchResult project,
        string id,
        bool hasServerPack)
        => new(
            project.Provider,
            project.ProjectId,
            id,
            id,
            "1.21.1",
            "NeoForge",
            "Release",
            DateTimeOffset.UtcNow,
            hasServerPack);

    private static SecureString CreateCredential()
    {
        var credential = new SecureString();
        foreach (var character in new[] { 't', 'e', 's', 't' })
        {
            credential.AppendChar(character);
        }

        credential.MakeReadOnly();
        return credential;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate());
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class FakeWorkflow : IOnlineModpackWorkflow
    {
        public IOnlineModpackArtworkCache? ArtworkCache { get; set; }
        public IReadOnlyList<OnlineModpackSearchResult> SearchResults { get; set; } = [];
        public IReadOnlyList<OnlineModpackSearchResult> FeaturedResults { get; set; } = [];
        public IReadOnlyList<OnlineModpackVersion> VersionResults { get; set; } = [];
        public Task<IReadOnlyList<OnlineModpackSearchResult>>? SearchTask { get; init; }
        public Task<IReadOnlyList<OnlineModpackSearchResult>>? FeaturedTask { get; init; }
        public ServerInstance InstalledServer { get; set; } = new();
        public OnlineModpackInstallProgress? ProgressToReport { get; init; }
        public bool BlockInstallUntilCancelled { get; init; }
        public bool BlockInstallRollbackUntilReleased { get; init; }
        public TaskCompletionSource<bool> InstallStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> InstallRollbackStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseInstallRollback { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public (OnlineModpackProvider Provider, string Query)? LastSearch { get; private set; }
        public OnlineModpackProvider? LastFeatured { get; private set; }
        public int? LastCredentialLength { get; private set; }
        public OnlineModpackInstallRequest? LastInstallRequest { get; private set; }
        public OnlineModpackBrowseRequest? LastBrowseRequest { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> BrowseAsync(
            OnlineModpackBrowseRequest request,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            LastBrowseRequest = request;
            LastCredentialLength = transientApiKey?.Length;
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                LastFeatured = request.Provider;
                return FeaturedTask ?? Task.FromResult(FeaturedResults);
            }

            LastSearch = (request.Provider, request.Query);
            return SearchTask ?? Task.FromResult(SearchResults);
        }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            LastSearch = (provider, query);
            LastCredentialLength = transientApiKey?.Length;
            return SearchTask ?? Task.FromResult(SearchResults);
        }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
            OnlineModpackProvider provider,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            LastFeatured = provider;
            LastCredentialLength = transientApiKey?.Length;
            return FeaturedTask ?? Task.FromResult(FeaturedResults);
        }

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            LastCredentialLength = transientApiKey?.Length;
            return Task.FromResult(VersionResults);
        }

        public async Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            LastInstallRequest = request;
            LastCredentialLength = transientApiKey?.Length;
            InstallStarted.TrySetResult(true);
            if (ProgressToReport is not null)
            {
                progress.Report(ProgressToReport);
            }

            if (BlockInstallUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (
                    BlockInstallRollbackUntilReleased
                    && cancellationToken.IsCancellationRequested)
                {
                    InstallRollbackStarted.TrySetResult(true);
                    await ReleaseInstallRollback.Task;
                    throw;
                }
            }

            return InstalledServer;
        }
    }

    private sealed class StubArtworkCache(string localPath) : IOnlineModpackArtworkCache
    {
        public Task<string?> GetOrCacheAsync(
            OnlineModpackProvider provider,
            Uri? remoteUri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(OnlineModpackProvider.Modrinth, provider);
            Assert.Equal("cdn.modrinth.com", remoteUri?.Host);
            return Task.FromResult<string?>(localPath);
        }
    }

    private sealed class StubArtworkDecoder : IOnlineModpackArtworkDecoder
    {
        public Task<ImageSource?> DecodePreviewAsync(
            string? localPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("C:\\cache\\catalog\\pack.png", localPath);
            var image = new DrawingImage();
            image.Freeze();
            return Task.FromResult<ImageSource?>(image);
        }
    }

    private sealed class SequencedArtworkCache(IReadOnlyDictionary<string, string?> results)
        : IOnlineModpackArtworkCache
    {
        public List<Uri> RequestedUris { get; } = [];

        public Task<string?> GetOrCacheAsync(
            OnlineModpackProvider provider,
            Uri? remoteUri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(OnlineModpackProvider.Modrinth, provider);
            var requested = Assert.IsType<Uri>(remoteUri);
            RequestedUris.Add(requested);
            return Task.FromResult(results[requested.AbsoluteUri]);
        }
    }

    private sealed class CandidateArtworkDecoder : IOnlineModpackArtworkDecoder
    {
        public Task<ImageSource?> DecodePreviewAsync(
            string? localPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(localPath, "C:\\cache\\catalog\\icon.webp", StringComparison.Ordinal))
            {
                return Task.FromResult<ImageSource?>(null);
            }

            var image = new DrawingImage();
            image.Freeze();
            return Task.FromResult<ImageSource?>(image);
        }
    }

    private sealed class GenerationRaceWorkflow : IOnlineModpackWorkflow
    {
        private readonly TaskCompletionSource<IReadOnlyList<OnlineModpackSearchResult>> _featured = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<OnlineModpackSearchResult>> _search = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<OnlineModpackSearchResult>>? PendingFeaturedCall { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>>? PendingSearchCall { get; private set; }

        public CancellationToken PendingFeaturedCancellation { get; private set; }

        public CancellationToken PendingSearchCancellation { get; private set; }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> GetFeaturedAsync(
            OnlineModpackProvider provider,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            if (provider == OnlineModpackProvider.Ftb)
            {
                PendingFeaturedCancellation = cancellationToken;
                return PendingFeaturedCall = _featured.Task;
            }

            return Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>(
            [
                Project(OnlineModpackProvider.Modrinth, "current-featured", "Current Modrinth Featured")
            ]);
        }

        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
        {
            if (provider == OnlineModpackProvider.Ftb)
            {
                PendingSearchCancellation = cancellationToken;
                return PendingSearchCall = _search.Task;
            }

            return Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>(
            [
                Project(OnlineModpackProvider.Modrinth, "current-search", "Current Modrinth Search")
            ]);
        }

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

        public void CompleteStaleFeatured(OnlineModpackSearchResult result)
            => _featured.SetResult([result]);

        public void CompleteStaleSearch(OnlineModpackSearchResult result)
            => _search.SetResult([result]);
    }
}
