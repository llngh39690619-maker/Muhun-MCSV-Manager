using System.Reflection;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientCatalogPaginationTests
{
    [Fact]
    public void PaginationItems_UseCompactStartMiddleAndEndWindows()
    {
        AssertPagination(
            totalHits: 2_500,
            resultLimit: 20,
            currentPage: 1,
            "1*", "2", "3", "4", "5", "…", "125");
        AssertPagination(
            totalHits: 2_500,
            resultLimit: 20,
            currentPage: 63,
            "1", "…", "62", "63*", "64", "…", "125");
        AssertPagination(
            totalHits: 2_500,
            resultLimit: 20,
            currentPage: 125,
            "1", "…", "121", "122", "123", "124", "125*");
        Assert.Empty(ClientWorkspaceViewModel.CreateCatalogPaginationItems(0, 20, 1));
    }

    [Fact]
    public async Task PageCommands_RequestRealOffsetsAndReplaceVisibleResults()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var catalog = new RecordingModrinthCatalog(totalHits: 2_500);
        await using var viewModel = CreateViewModel(directory.Path, catalog);

        viewModel.OpenCatalogCommand.Execute(null);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 20);

        Assert.Equal(0, catalog.Requests[0].Offset);
        Assert.Equal(1, viewModel.CatalogCurrentPage);
        Assert.Same(viewModel.CatalogProjects[0], viewModel.SelectedCatalogProject);
        Assert.True(viewModel.IsCatalogDetailOpen);
        Assert.True(viewModel.ShowsCatalogPagination);
        Assert.Equal(
            new[] { "1", "2", "3", "4", "5", "…", "125" },
            viewModel.CatalogPaginationItems.Select(static item => item.DisplayText).ToArray());

        viewModel.GoToCatalogPageCommand.Execute(4);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 2 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogCurrentPage == 4);

        Assert.Equal(60, catalog.Requests[1].Offset);
        Assert.Equal(20, viewModel.CatalogProjects.Count);
        Assert.Equal("project-60", viewModel.CatalogProjects[0].ProjectId);
        Assert.DoesNotContain(
            viewModel.CatalogProjects,
            static project => project.ProjectId == "project-0");

        viewModel.PreviousCatalogPageCommand.Execute(null);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 3 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogCurrentPage == 3);
        Assert.Equal(40, catalog.Requests[2].Offset);

        viewModel.NextCatalogPageCommand.Execute(null);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 4 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogCurrentPage == 4);
        Assert.Equal(60, catalog.Requests[3].Offset);
    }

    [Fact]
    public async Task QueryAndResultLimitChanges_ResetToTheFirstRealPage()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var catalog = new RecordingModrinthCatalog(totalHits: 2_500);
        await using var viewModel = CreateViewModel(directory.Path, catalog);

        viewModel.OpenCatalogCommand.Execute(null);
        await WaitUntilAsync(() => catalog.Requests.Count == 1 && !viewModel.IsCatalogBusy);
        viewModel.GoToCatalogPageCommand.Execute(5);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 2 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogCurrentPage == 5);

        viewModel.CatalogSearchText = "skyblock";

        Assert.Equal(1, viewModel.CatalogCurrentPage);
        await WaitUntilAsync(() => catalog.Requests.Count == 3 && !viewModel.IsCatalogBusy);
        Assert.Equal("skyblock", catalog.Requests[2].Query);
        Assert.Equal(0, catalog.Requests[2].Offset);
        Assert.Equal("project-0", viewModel.CatalogProjects[0].ProjectId);

        viewModel.GoToCatalogPageCommand.Execute(4);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 4 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogCurrentPage == 4);

        viewModel.CatalogResultLimit = 40;

        Assert.Equal(1, viewModel.CatalogCurrentPage);
        await WaitUntilAsync(() => catalog.Requests.Count == 5 && !viewModel.IsCatalogBusy);
        Assert.Equal(0, catalog.Requests[4].Offset);
        Assert.Equal(40, catalog.Requests[4].Limit);
        Assert.Equal(40, viewModel.CatalogProjects.Count);
    }

    [Fact]
    public async Task BrowseAll_ClearsSearchAndFiltersBeforeReloadingTheFirstPage()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var catalog = new RecordingModrinthCatalog(totalHits: 2_500);
        await using var viewModel = CreateViewModel(directory.Path, catalog);

        Assert.Equal(new[] { 20, 40 }, viewModel.CatalogResultLimits);
        viewModel.OpenCatalogCommand.Execute(null);
        await WaitUntilAsync(() => catalog.Requests.Count == 1 && !viewModel.IsCatalogBusy);

        viewModel.CatalogSearchText = "skyblock";
        await WaitUntilAsync(() => catalog.Requests.Count == 2 && !viewModel.IsCatalogBusy);
        Assert.Equal("skyblock", catalog.Requests[1].Query);

        viewModel.BrowseAllCatalogCommand.Execute(null);
        await WaitUntilAsync(() => catalog.Requests.Count == 3 && !viewModel.IsCatalogBusy);

        Assert.Equal(string.Empty, viewModel.CatalogSearchText);
        Assert.Null(viewModel.SelectedCatalogGameVersion?.Version);
        Assert.Null(viewModel.SelectedCatalogLoader?.Loader);
        Assert.Null(viewModel.SelectedCatalogCategory?.Category);
        Assert.Equal(string.Empty, catalog.Requests[2].Query);
        Assert.Equal(0, catalog.Requests[2].Offset);
        Assert.Equal(1, viewModel.CatalogCurrentPage);
    }

    [Fact]
    public async Task LoadMore_RemainsCompatibleAndAdvancesFromTheCurrentOffset()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var catalog = new RecordingModrinthCatalog(totalHits: 80);
        await using var viewModel = CreateViewModel(directory.Path, catalog);

        viewModel.OpenCatalogCommand.Execute(null);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 1 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 20);

        viewModel.LoadMoreCatalogCommand.Execute(null);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 2 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogProjects.Count == 40);

        Assert.Equal(20, catalog.Requests[1].Offset);
        Assert.Equal(2, viewModel.CatalogCurrentPage);
        Assert.Equal("project-0", viewModel.CatalogProjects[0].ProjectId);
        Assert.Equal("project-20", viewModel.CatalogProjects[20].ProjectId);
    }

    [Fact]
    public async Task SourceSwitch_ResetsPaginationAndModrinthRestartsAtOffsetZero()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var catalog = new RecordingModrinthCatalog(totalHits: 100);
        await using var viewModel = CreateViewModel(directory.Path, catalog);

        viewModel.OpenCatalogCommand.Execute(null);
        await WaitUntilAsync(() => catalog.Requests.Count == 1 && !viewModel.IsCatalogBusy);
        viewModel.GoToCatalogPageCommand.Execute(3);
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 2 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogCurrentPage == 3);

        viewModel.SelectCatalogSourceCommand.Execute("curseforge");
        await WaitUntilAsync(() =>
            viewModel.CatalogSourceId == "curseforge" &&
            viewModel.SelectCatalogSourceCommand.CanExecute(null));

        Assert.Equal(1, viewModel.CatalogCurrentPage);
        Assert.Equal(0, viewModel.CatalogTotalHits);
        Assert.Empty(viewModel.CatalogPaginationItems);

        viewModel.SelectCatalogSourceCommand.Execute("modrinth");
        await WaitUntilAsync(() =>
            catalog.Requests.Count == 3 &&
            !viewModel.IsCatalogBusy &&
            viewModel.CatalogSourceId == "modrinth");

        Assert.Equal(0, catalog.Requests[2].Offset);
        Assert.Equal(1, viewModel.CatalogCurrentPage);
        Assert.Equal("project-0", viewModel.CatalogProjects[0].ProjectId);
    }

    [Fact]
    public async Task FtbSource_HidesAndDisablesPaginationBecauseItsApiHasNoOffset()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var catalog = new RecordingModrinthCatalog(totalHits: 100);
        await using var viewModel = CreateViewModel(directory.Path, catalog);
        SetPrivateField(viewModel, "_catalogSourceId", "ftb");
        SetPrivateField(viewModel, "_catalogTotalHits", 100);
        SetPrivateField(viewModel, "_catalogCurrentPage", 3);

        Assert.False(viewModel.SupportsCatalogPagination);
        Assert.False(viewModel.ShowsCatalogPagination);
        Assert.Empty(viewModel.CatalogPaginationItems);
        Assert.False(viewModel.GoToCatalogPageCommand.CanExecute(2));
        Assert.False(viewModel.PreviousCatalogPageCommand.CanExecute(null));
        Assert.False(viewModel.NextCatalogPageCommand.CanExecute(null));
    }

    private static ClientWorkspaceViewModel CreateViewModel(
        string root,
        IModrinthClientModpackCatalog catalog) =>
        new(
            new ApplicationPaths(root),
            static () => new NewMinecraftClientDefaultsSettings(),
            releaseCatalog: null,
            loaderCatalogs: [],
            modrinthCatalog: catalog);

    private static void AssertPagination(
        int totalHits,
        int resultLimit,
        int currentPage,
        params string[] expected)
    {
        var items = ClientWorkspaceViewModel.CreateCatalogPaginationItems(
            totalHits,
            resultLimit,
            currentPage);
        var actual = items
            .Select(static item => item.DisplayText + (item.IsCurrent ? "*" : string.Empty))
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.All(
            items.Where(static item => item.PageNumber is null),
            static item => Assert.False(item.CanNavigate));
        Assert.All(
            items.Where(static item => item.IsCurrent),
            static item => Assert.False(item.CanNavigate));
    }

    private static void SetPrivateField<T>(
        ClientWorkspaceViewModel viewModel,
        string fieldName,
        T value)
    {
        var field = typeof(ClientWorkspaceViewModel).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(viewModel, value);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate());
    }

    private sealed class RecordingModrinthCatalog(int totalHits) : IModrinthClientModpackCatalog
    {
        public List<ModrinthClientModpackSearchRequest> Requests { get; } = [];

        public Task<ModrinthClientModpackSearchPage> SearchAsync(
            ModrinthClientModpackSearchRequest request,
            CancellationToken cancellationToken = default) =>
            BrowseAsync(request, cancellationToken);

        public Task<ModrinthClientModpackSearchPage> GetPopularAsync(
            ModrinthClientModpackSearchRequest request,
            CancellationToken cancellationToken = default) =>
            BrowseAsync(request, cancellationToken);

        public Task<ModrinthClientModpackProject> GetProjectAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ModrinthClientModpackVersion>> GetStableVersionsAsync(
            string projectId,
            string? gameVersion = null,
            MinecraftClientLoader? loader = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModrinthClientModpackVersion> GetStableVersionAsync(
            string versionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private Task<ModrinthClientModpackSearchPage> BrowseAsync(
            ModrinthClientModpackSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var count = Math.Min(request.Limit, Math.Max(0, totalHits - request.Offset));
            var projects = Enumerable.Range(request.Offset, count)
                .Select(CreateProject)
                .ToArray();
            return Task.FromResult(new ModrinthClientModpackSearchPage(
                projects,
                request.Offset,
                request.Limit,
                totalHits));
        }

        private static ModrinthClientModpackProject CreateProject(int index) =>
            new(
                $"project-{index}",
                $"project-{index}",
                $"Project {index}",
                "Pagination test project.",
                "X MCSV",
                IconUri: null,
                FeaturedImageUri: null,
                GalleryImageUris: [],
                GameVersions: ["1.21.1"],
                Categories: ["technology"],
                Environments: ["client"],
                Downloads: index,
                Followers: index,
                DateModified: DateTimeOffset.UnixEpoch);
    }
}
