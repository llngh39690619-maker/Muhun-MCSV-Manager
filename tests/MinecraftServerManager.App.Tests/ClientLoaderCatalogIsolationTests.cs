using System.IO;
using System.Net.Http;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientLoaderCatalogIsolationTests
{
    [Fact]
    public void ClientWorkspace_BindsStableLoaderCardsAndDisablesUnavailableEntries()
    {
        var xaml = File.ReadAllText(
            TestRepositoryPaths.AppSource("Views", "ClientWorkspaceView.xaml"));

        Assert.Contains("ItemsSource=\"{Binding LoaderChoices}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedLoader}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsEnabled\" Value=\"{Binding IsAvailable}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AvailabilityText}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFixedLoaderChoices_KeepsEveryProductLoaderInStableOrder()
    {
        LoaderCatalogQueryResult[] results =
        [
            Result(MinecraftClientLoader.Forge, ManagedEntry(MinecraftClientLoader.Forge)),
            Result(MinecraftClientLoader.Fabric),
            Result(MinecraftClientLoader.Quilt),
            Result(MinecraftClientLoader.NeoForge),
            Result(MinecraftClientLoader.OptiFine, ExternalEntry(MinecraftClientLoader.OptiFine)),
            Result(MinecraftClientLoader.LabyMod, ExternalEntry(MinecraftClientLoader.LabyMod)),
        ];

        var choices = ClientWorkspaceViewModel.CreateFixedLoaderChoices(results, isChecking: false);

        Assert.Equal(
            [
                MinecraftClientLoader.Vanilla,
                MinecraftClientLoader.Forge,
                MinecraftClientLoader.Fabric,
                MinecraftClientLoader.Quilt,
                MinecraftClientLoader.NeoForge,
                MinecraftClientLoader.OptiFine,
                MinecraftClientLoader.LabyMod,
            ],
            choices.Select(static choice => choice.Loader));
        Assert.True(choices.Single(choice => choice.Loader == MinecraftClientLoader.Vanilla).IsAvailable);
        Assert.True(choices.Single(choice => choice.Loader == MinecraftClientLoader.Forge).IsAvailable);
        Assert.False(choices.Single(choice => choice.Loader == MinecraftClientLoader.Fabric).IsAvailable);
        Assert.False(choices.Single(choice => choice.Loader == MinecraftClientLoader.Quilt).IsAvailable);
        Assert.False(choices.Single(choice => choice.Loader == MinecraftClientLoader.NeoForge).IsAvailable);
        Assert.True(choices.Single(choice => choice.Loader == MinecraftClientLoader.OptiFine).IsAvailable);
        Assert.True(choices.Single(choice => choice.Loader == MinecraftClientLoader.LabyMod).IsAvailable);
    }

    [Fact]
    public void CreateFixedLoaderChoices_ExposesCheckingAndQueryFailureInsteadOfRemovingCards()
    {
        var checking = ClientWorkspaceViewModel.CreateFixedLoaderChoices(
            results: null,
            isChecking: true);
        Assert.Equal(7, checking.Count);
        Assert.All(
            checking.Where(choice => choice.Loader != MinecraftClientLoader.Vanilla),
            choice =>
            {
                Assert.True(choice.IsChecking);
                Assert.False(choice.IsAvailable);
            });

        LoaderCatalogQueryResult[] failedResults =
        [
            new(MinecraftClientLoader.NeoForge, [], new HttpRequestException("fixture unavailable")),
        ];
        var failed = ClientWorkspaceViewModel.CreateFixedLoaderChoices(
            failedResults,
            isChecking: false);

        var neoForge = Assert.Single(
            failed,
            choice => choice.Loader == MinecraftClientLoader.NeoForge);
        Assert.True(neoForge.CatalogQueryFailed);
        Assert.False(neoForge.IsAvailable);
    }

    [Fact]
    public void LoaderRefresh_PreservesPreferredLoaderWhileCheckingAndFallsBackOnlyAfterFinalResults()
    {
        var checking = ClientWorkspaceViewModel.CreateFixedLoaderChoices(
            results: null,
            isChecking: true);

        var checkingSelection = ClientWorkspaceViewModel.SelectLoaderChoiceForRefresh(
            checking,
            MinecraftClientLoader.Fabric,
            requireAvailablePreferred: false);
        var unavailableFinalSelection = ClientWorkspaceViewModel.SelectLoaderChoiceForRefresh(
            checking,
            MinecraftClientLoader.Fabric,
            requireAvailablePreferred: true);

        Assert.Equal(MinecraftClientLoader.Fabric, checkingSelection?.Loader);
        Assert.Equal(MinecraftClientLoader.Vanilla, unavailableFinalSelection?.Loader);

        LoaderCatalogQueryResult[] supportedResults =
        [
            Result(MinecraftClientLoader.Fabric, ManagedEntry(MinecraftClientLoader.Fabric)),
        ];
        var supported = ClientWorkspaceViewModel.CreateFixedLoaderChoices(
            supportedResults,
            isChecking: false);
        var supportedFinalSelection = ClientWorkspaceViewModel.SelectLoaderChoiceForRefresh(
            supported,
            MinecraftClientLoader.Fabric,
            requireAvailablePreferred: true);

        Assert.Equal(MinecraftClientLoader.Fabric, supportedFinalSelection?.Loader);
    }

    [Fact]
    public async Task QueryLoaderCatalogsAsync_IsolatesOneFailureAndKeepsManagedAndExternalChoices()
    {
        var snapshot = StableSnapshot("1.21.1");
        IMinecraftLoaderCatalogProvider[] providers =
        [
            new ThrowingProvider(MinecraftClientLoader.Fabric),
            new FixedProvider(MinecraftClientLoader.Forge),
            new OptiFineExternalInstallerCatalogProvider(),
            new LabyModExternalInstallerCatalogProvider(),
        ];

        var results = await ClientWorkspaceViewModel.QueryLoaderCatalogsAsync(
            providers,
            snapshot,
            "1.21.1");

        var failed = Assert.Single(results, result => result.Error is not null);
        Assert.Equal(MinecraftClientLoader.Fabric, failed.Loader);
        Assert.Empty(failed.Versions);
        Assert.Contains(results, result => result.Loader == MinecraftClientLoader.Forge
                                           && result.Error is null
                                           && result.Versions.Count == 1);
        Assert.Contains(results, result => result.Loader == MinecraftClientLoader.OptiFine
                                           && result.Error is null
                                           && result.Versions.Count == 1);
        Assert.Contains(results, result => result.Loader == MinecraftClientLoader.LabyMod
                                           && result.Error is null
                                           && result.Versions.Count == 1);
    }

    [Fact]
    public async Task QueryLoaderCatalogsAsync_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ClientWorkspaceViewModel.QueryLoaderCatalogsAsync(
                [new ThrowingProvider(MinecraftClientLoader.Fabric)],
                StableSnapshot("1.21.1"),
                "1.21.1",
                cancellation.Token));
    }

    private static MinecraftReleaseCatalogSnapshot StableSnapshot(string version)
    {
        var release = new MinecraftReleaseInfo(
            version,
            DateTimeOffset.UtcNow,
            new Uri($"https://piston-meta.mojang.com/v1/packages/{new string('a', 40)}/{version}.json"),
            new string('a', 40),
            1);
        return new MinecraftReleaseCatalogSnapshot(version, DateTimeOffset.UtcNow, [release]);
    }

    private static LoaderCatalogQueryResult Result(
        MinecraftClientLoader loader,
        params MinecraftLoaderCatalogEntry[] entries) =>
        new(loader, entries, null);

    private static MinecraftLoaderCatalogEntry ManagedEntry(MinecraftClientLoader loader) =>
        new(
            loader,
            "1.21.1",
            "1.0.0",
            MinecraftLoaderReleaseChannel.Stable,
            MinecraftClientLoaderInstallKind.Managed,
            new Uri("https://example.com/official/"),
            new Uri("https://example.com/official/loader.jar"),
            "fixture");

    private static MinecraftLoaderCatalogEntry ExternalEntry(MinecraftClientLoader loader) =>
        ManagedEntry(loader) with
        {
            InstallKind = MinecraftClientLoaderInstallKind.ExternalInstallerRequired,
            InstallProfileOrArtifactUri = null,
        };

    private sealed class ThrowingProvider(MinecraftClientLoader loader) : IMinecraftLoaderCatalogProvider
    {
        public MinecraftClientLoader Loader => loader;

        public Task<IReadOnlyList<MinecraftLoaderCatalogEntry>> GetVersionsAsync(
            MinecraftReleaseCatalogSnapshot stableMinecraftReleases,
            string gameVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("fixture provider unavailable");
        }
    }

    private sealed class FixedProvider(MinecraftClientLoader loader) : IMinecraftLoaderCatalogProvider
    {
        public MinecraftClientLoader Loader => loader;

        public Task<IReadOnlyList<MinecraftLoaderCatalogEntry>> GetVersionsAsync(
            MinecraftReleaseCatalogSnapshot stableMinecraftReleases,
            string gameVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MinecraftLoaderCatalogEntry> result =
            [
                new(
                    loader,
                    gameVersion,
                    "1.0.0",
                    MinecraftLoaderReleaseChannel.Stable,
                    MinecraftClientLoaderInstallKind.Managed,
                    new Uri("https://files.minecraftforge.net/"),
                    new Uri("https://maven.minecraftforge.net/example.jar"),
                    "fixture"),
            ];
            return Task.FromResult(result);
        }
    }
}
