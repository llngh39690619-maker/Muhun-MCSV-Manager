using System.Security;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackBrowseContractTests
{
    [Theory]
    [InlineData(OnlineModpackSort.Relevance, "relevance", CurseForgeModpackSortField.Popularity)]
    [InlineData(OnlineModpackSort.Downloads, "downloads", CurseForgeModpackSortField.TotalDownloads)]
    [InlineData(OnlineModpackSort.RecentlyUpdated, "updated", CurseForgeModpackSortField.LastUpdated)]
    [InlineData(OnlineModpackSort.Newest, "newest", CurseForgeModpackSortField.ReleasedDate)]
    public void Sorts_MapToEachProvidersOfficialIdentifier(
        OnlineModpackSort sort,
        string expectedModrinthIndex,
        CurseForgeModpackSortField expectedCurseForgeField)
    {
        Assert.Equal(expectedModrinthIndex, OnlineModpackWorkflow.MapModrinthIndex(sort));
        Assert.Equal(expectedCurseForgeField, OnlineModpackWorkflow.MapCurseForgeSort(sort));
    }

    [Theory]
    [InlineData(null, CurseForgeModLoaderType.Any)]
    [InlineData("Forge", CurseForgeModLoaderType.Forge)]
    [InlineData("Fabric", CurseForgeModLoaderType.Fabric)]
    [InlineData("Neo-Forge", CurseForgeModLoaderType.NeoForge)]
    [InlineData("Quilt", CurseForgeModLoaderType.Quilt)]
    public void LoaderAndCategory_MapToCurseForgeIdentifiers(
        string? loader,
        CurseForgeModLoaderType expected)
    {
        Assert.Equal(expected, OnlineModpackWorkflow.MapCurseForgeLoader(loader));
        Assert.Equal(4484, OnlineModpackWorkflow.ParseCurseForgeCategory("4484"));
        Assert.Null(OnlineModpackWorkflow.ParseCurseForgeCategory(null));
    }

    [Fact]
    public void FtbFiltersReturnedVersionMetadataAndSortsByOfficialInstallCount()
    {
        var olderMatch = Pack(
            1,
            "Older",
            new FtbPackVersion(10, "1.0", "release", 1_000, Targets("1.21.1", "neoforge")));
        var irrelevant = Pack(
            2,
            "Fabric",
            new FtbPackVersion(20, "2.0", "release", 9_000, Targets("1.20.1", "fabric")));
        var newestMatch = Pack(
            3,
            "Newest",
            new FtbPackVersion(30, "3.0", "release", 3_000, Targets("1.21.1", "NeoForge")));

        var request = new OnlineModpackBrowseRequest(
            OnlineModpackProvider.Ftb,
            Sort: OnlineModpackSort.RecentlyUpdated,
            GameVersion: "1.21.1",
            Loader: "neo-forge");
        var filtered = OnlineModpackWorkflow.FilterAndSortFtbPacks(
            [olderMatch, irrelevant, newestMatch],
            request);

        Assert.Equal([3, 1], filtered.Select(static pack => pack.Id));

        olderMatch = olderMatch with { InstallCount = 100 };
        newestMatch = newestMatch with { InstallCount = 500 };
        var downloadOrder = OnlineModpackWorkflow.FilterAndSortFtbPacks(
            [olderMatch, newestMatch],
            request with
            {
                Sort = OnlineModpackSort.Downloads,
                GameVersion = null,
                Loader = null
            });
        Assert.Equal([3, 1], downloadOrder.Select(static pack => pack.Id));
        Assert.Empty(OnlineModpackWorkflow.FilterAndSortFtbPacks(
            [olderMatch],
            request with { SourceCategory = "adventure" }));
    }

    [Fact]
    public void FtbBrowseSort_NormalizesMixedTimestampUnitsAndIgnoresInvalidEpochs()
    {
        var older = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var newer = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var request = new OnlineModpackBrowseRequest(
            OnlineModpackProvider.Ftb,
            Sort: OnlineModpackSort.RecentlyUpdated);
        var sorted = OnlineModpackWorkflow.FilterAndSortFtbPacks(
            [
                Pack(1, "Older", new FtbPackVersion(
                    1, "1", "release", older.ToUnixTimeMilliseconds(), [])),
                Pack(2, "Newer", new FtbPackVersion(
                    2, "2", "release", newer.ToUnixTimeSeconds(), [])),
                Pack(3, "Invalid", new FtbPackVersion(
                    3, "3", "release", long.MaxValue, []))
            ],
            request);

        Assert.Equal([2, 1, 3], sorted.Select(static pack => pack.Id));
    }

    [Fact]
    public void FtbProjectMapping_NormalizesSecondsAndMilliseconds_AndHidesInvalidEpochs()
    {
        var updated = DateTimeOffset.Parse("2026-08-20T03:04:05Z");

        Assert.Equal(
            updated,
            OnlineModpackWorkflow.MapFtbProject(Pack(
                1,
                "Seconds",
                new FtbPackVersion(1, "1", "release", updated.ToUnixTimeSeconds(), [])))
                .UpdatedAtUtc);
        Assert.Equal(
            updated,
            OnlineModpackWorkflow.MapFtbProject(Pack(
                2,
                "Milliseconds",
                new FtbPackVersion(2, "2", "release", updated.ToUnixTimeMilliseconds(), [])))
                .UpdatedAtUtc);
        Assert.Null(OnlineModpackWorkflow.MapFtbProject(Pack(
            3,
            "Zero",
            new FtbPackVersion(3, "3", "release", 0, []))).UpdatedAtUtc);
        Assert.Null(OnlineModpackWorkflow.MapFtbProject(Pack(
            4,
            "Out of range",
            new FtbPackVersion(4, "4", "release", long.MaxValue, []))).UpdatedAtUtc);
    }

    [Fact]
    public void ProviderMappingsCarrySafePreviewMetadata()
    {
        var updated = DateTimeOffset.Parse("2026-08-20T03:04:05Z");
        var ftb = new FtbPack(
            134,
            "FTB Pack",
            "ftb-pack",
            false,
            [new FtbPackVersion(1, "1.0", "release", updated.ToUnixTimeMilliseconds(), [])],
            "FTB summary",
            44_000,
            [
                new FtbArtwork(
                    new Uri("https://cdn.feed-the-beast.com/icon.webp"),
                    "square",
                    512,
                    512,
                    [new Uri("https://cdn.feed-the-beast.com/icon-mirror.webp")]),
                new FtbArtwork(
                    new Uri("https://cdn.feed-the-beast.com/splash.webp"),
                    "splash",
                    1920,
                    1080,
                    [new Uri("https://cdn.feed-the-beast.com/splash-mirror.webp")]),
                new FtbArtwork(
                    new Uri("https://cdn.feed-the-beast.com/screenshot.webp"),
                    "screenshot",
                    1920,
                    1080)
            ]);
        var mappedFtb = OnlineModpackWorkflow.MapFtbProject(ftb);

        Assert.Equal(ftb.IconUri, mappedFtb.IconUri);
        Assert.Equal(ftb.PreviewImageUri, mappedFtb.PreviewImageUri);
        Assert.Equal(
            [
                "https://cdn.feed-the-beast.com/icon.webp",
                "https://cdn.feed-the-beast.com/icon-mirror.webp",
                "https://cdn.feed-the-beast.com/splash.webp",
                "https://cdn.feed-the-beast.com/splash-mirror.webp",
                "https://cdn.feed-the-beast.com/screenshot.webp"
            ],
            mappedFtb.IconUriCandidates.Select(static uri => uri.AbsoluteUri));
        Assert.Equal(
            [
                "https://cdn.feed-the-beast.com/splash.webp",
                "https://cdn.feed-the-beast.com/splash-mirror.webp",
                "https://cdn.feed-the-beast.com/screenshot.webp",
                "https://cdn.feed-the-beast.com/icon.webp",
                "https://cdn.feed-the-beast.com/icon-mirror.webp"
            ],
            mappedFtb.PreviewImageUriCandidates.Select(static uri => uri.AbsoluteUri));
        Assert.Equal(44_000, mappedFtb.DownloadCount);
        Assert.Equal(updated, mappedFtb.UpdatedAtUtc);

        var modrinth = new ModrinthModpackProject(
            "project",
            "slug",
            "Pack",
            "Summary",
            "Author",
            new Uri("https://cdn.modrinth.com/data/project/icon.png"),
            "MIT",
            ["1.21.1"],
            ["neoforge", "adventure"],
            ["server_only"],
            12_345,
            updated,
            [
                new Uri("https://cdn.modrinth.com/data/project/images/preview.png"),
                new Uri("https://cdn.modrinth.com/data/project/images/preview.png"),
                new Uri("http://cdn.modrinth.com/data/project/images/unsafe.png"),
                new Uri("https://cdn.modrinth.com/data/project/images/second.png")
            ]);
        var mappedModrinth = OnlineModpackWorkflow.MapModrinthProject(modrinth);

        Assert.Equal(modrinth.IconUri, mappedModrinth.IconUri);
        Assert.Equal(modrinth.GalleryImageUris[0], mappedModrinth.PreviewImageUri);
        Assert.Equal(
            [
                "https://cdn.modrinth.com/data/project/icon.png",
                "https://cdn.modrinth.com/data/project/images/preview.png",
                "https://cdn.modrinth.com/data/project/images/second.png"
            ],
            mappedModrinth.IconUriCandidates.Select(static uri => uri.AbsoluteUri));
        Assert.Equal(
            [
                "https://cdn.modrinth.com/data/project/images/preview.png",
                "https://cdn.modrinth.com/data/project/images/second.png",
                "https://cdn.modrinth.com/data/project/icon.png"
            ],
            mappedModrinth.PreviewImageUriCandidates.Select(static uri => uri.AbsoluteUri));
        Assert.Equal(12_345, mappedModrinth.DownloadCount);
        Assert.Equal(updated, mappedModrinth.UpdatedAtUtc);

        var galleryOnly = OnlineModpackWorkflow.MapModrinthProject(modrinth with { IconUri = null });
        Assert.Equal(modrinth.GalleryImageUris[0], galleryOnly.IconUri);
        var iconOnly = OnlineModpackWorkflow.MapModrinthProject(modrinth with { GalleryImageUris = [] });
        Assert.Equal(modrinth.IconUri, iconOnly.PreviewImageUri);

        var curseForge = new CurseForgeModpackProject(
            100,
            432,
            4471,
            "pack",
            "Curse Pack",
            "Summary",
            "Author",
            new Uri("https://www.curseforge.com/minecraft/modpacks/pack"),
            new Uri("https://media.forgecdn.net/icon.png"),
            true,
            true,
            98_765,
            updated,
            new Uri("https://media.forgecdn.net/preview.jpg"));
        var mappedCurseForge = OnlineModpackWorkflow.MapCurseForgeProject(curseForge);

        Assert.Equal(curseForge.IconUri, mappedCurseForge.IconUri);
        Assert.Equal(curseForge.PreviewImageUri, mappedCurseForge.PreviewImageUri);
        Assert.Equal(98_765, mappedCurseForge.DownloadCount);
        Assert.Equal(updated, mappedCurseForge.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("http://cdn.example.test/icon.png")]
    [InlineData("https://localhost/icon.png")]
    [InlineData("https://127.0.0.1/icon.png")]
    [InlineData("https://user:secret@cdn.example.test/icon.png")]
    [InlineData("https://cdn.example.test:8443/icon.png")]
    public void SearchResult_DropsUnsafeMediaAddresses(string value)
    {
        var result = new OnlineModpackSearchResult(
            OnlineModpackProvider.Modrinth,
            "project",
            "Pack",
            "Summary",
            "Author",
            iconUri: new Uri(value));

        Assert.Null(result.IconUri);
    }

    [Fact]
    public void SearchResult_CandidateListsAreSafeDeduplicatedAndBounded()
    {
        var candidates = Enumerable.Range(0, 20)
            .Select(index => new Uri($"https://cdn.example.test/images/{index}.png"))
            .Prepend(new Uri("https://cdn.example.test/images/0.png"))
            .Prepend(new Uri("http://cdn.example.test/images/unsafe.png"));

        var result = new OnlineModpackSearchResult(
            OnlineModpackProvider.Modrinth,
            "project",
            "Pack",
            "Summary",
            "Author",
            iconUri: new Uri("https://cdn.example.test/images/0.png"),
            previewImageUri: new Uri("https://localhost/unsafe.png"),
            iconUriCandidates: candidates,
            previewImageUriCandidates: candidates);

        Assert.Equal(OnlineModpackSearchResult.MaximumIconUriCandidates, result.IconUriCandidates.Count);
        Assert.Equal(
            OnlineModpackSearchResult.MaximumPreviewImageUriCandidates,
            result.PreviewImageUriCandidates.Count);
        Assert.Equal("https://cdn.example.test/images/0.png", result.IconUri?.AbsoluteUri);
        Assert.Equal("https://cdn.example.test/images/0.png", result.PreviewImageUri?.AbsoluteUri);
        Assert.Equal(
            result.IconUriCandidates.Count,
            result.IconUriCandidates.Select(static uri => uri.AbsoluteUri).Distinct().Count());
        Assert.All(result.IconUriCandidates, static uri => Assert.Equal(Uri.UriSchemeHttps, uri.Scheme));
        Assert.DoesNotContain(result.PreviewImageUriCandidates, static uri => uri.IsLoopback);
    }

    [Fact]
    public async Task BrowseDefaultMethodKeepsExistingFakesSourceCompatible()
    {
        IOnlineModpackWorkflow workflow = new LegacyWorkflow();

        var result = await workflow.BrowseAsync(
            new OnlineModpackBrowseRequest(
                OnlineModpackProvider.Modrinth,
                Query: "pack",
                Offset: 1,
                Limit: 1),
            null,
            CancellationToken.None);

        Assert.Equal("second", Assert.Single(result).ProjectId);
    }

    private static FtbPack Pack(int id, string name, params FtbPackVersion[] versions)
        => new(id, name, name.ToLowerInvariant(), false, versions);

    private static IReadOnlyList<FtbTarget> Targets(string gameVersion, string loader)
        =>
        [
            new FtbTarget("game", "minecraft", gameVersion),
            new FtbTarget("modloader", loader, "1.0")
        ];

    private sealed class LegacyWorkflow : IOnlineModpackWorkflow
    {
        public Task<IReadOnlyList<OnlineModpackSearchResult>> SearchAsync(
            OnlineModpackProvider provider,
            string query,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackSearchResult>>(
            [
                new(provider, "first", "First", "", ""),
                new(provider, "second", "Second", "", "")
            ]);

        public Task<IReadOnlyList<OnlineModpackVersion>> GetVersionsAsync(
            OnlineModpackSearchResult project,
            SecureString? transientApiKey,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OnlineModpackVersion>>([]);

        public Task<ServerInstance> InstallAsync(
            OnlineModpackInstallRequest request,
            SecureString? transientApiKey,
            IProgress<OnlineModpackInstallProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
