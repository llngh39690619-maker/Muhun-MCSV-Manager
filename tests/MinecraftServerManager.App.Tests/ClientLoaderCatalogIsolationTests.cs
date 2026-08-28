using System.Net.Http;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientLoaderCatalogIsolationTests
{
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
