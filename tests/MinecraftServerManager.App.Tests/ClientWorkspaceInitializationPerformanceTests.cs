using System.Collections.Specialized;
using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientWorkspaceInitializationPerformanceTests
{
    [Fact]
    public async Task Initialize_PublishesLocalInstancesInOneBatchBeforeRemoteCatalogCompletes()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var expectedInstances = Enumerable.Range(0, 64)
            .Select(index => CreateInstance(paths, index))
            .ToArray();
        using (var registry = new MinecraftClientRegistry(paths.ClientRegistryFile))
        {
            await registry.SaveAsync(new MinecraftClientRegistryDocument
            {
                Instances = [.. expectedInstances],
            });
        }

        var catalog = new BlockingReleaseCatalog();
        await using var viewModel = new ClientWorkspaceViewModel(
            paths,
            static () => new NewMinecraftClientDefaultsSettings(),
            catalog,
            Array.Empty<IMinecraftLoaderCatalogProvider>());
        var instanceNotifications = new List<NotifyCollectionChangedAction>();
        var releaseNotifications = new List<NotifyCollectionChangedAction>();
        var gameVersionNotifications = new List<NotifyCollectionChangedAction>();
        viewModel.Instances.CollectionChanged += (_, args) =>
            instanceNotifications.Add(args.Action);
        viewModel.Releases.CollectionChanged += (_, args) =>
            releaseNotifications.Add(args.Action);
        viewModel.CatalogGameVersions.CollectionChanged += (_, args) =>
            gameVersionNotifications.Add(args.Action);

        var initialization = viewModel.InitializeForDiagnosticsAsync();
        await catalog.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(initialization.IsCompleted);
        Assert.True(viewModel.IsInitialized);
        Assert.Equal(expectedInstances.Length, viewModel.Instances.Count);
        Assert.Equal([NotifyCollectionChangedAction.Reset], instanceNotifications);
        Assert.Empty(viewModel.Releases);
        Assert.Empty(releaseNotifications);
        Assert.Empty(gameVersionNotifications);

        catalog.Complete(CreateCatalogSnapshot());
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, viewModel.Releases.Count);
        Assert.Equal(3, viewModel.CatalogGameVersions.Count);
        Assert.Equal([NotifyCollectionChangedAction.Reset], releaseNotifications);
        Assert.Equal([NotifyCollectionChangedAction.Reset], gameVersionNotifications);
    }

    private static MinecraftClientInstance CreateInstance(ApplicationPaths paths, int index)
    {
        var instanceDirectory = Path.Combine(paths.Clients, $"instance-{index:D3}");
        Directory.CreateDirectory(instanceDirectory);
        return new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = $"Instance {index:D3}",
            DirectoryPath = instanceDirectory,
            GameVersion = "1.21.1",
            InstalledVersionId = "1.21.1",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-index),
        };
    }

    private static MinecraftReleaseCatalogSnapshot CreateCatalogSnapshot()
    {
        var loadedAtUtc = DateTimeOffset.UtcNow;
        return new MinecraftReleaseCatalogSnapshot(
            "1.21.1",
            loadedAtUtc,
            [
                new MinecraftReleaseInfo(
                    "1.21.1",
                    loadedAtUtc,
                    new Uri("https://piston-meta.mojang.com/v1/packages/a/1.21.1.json"),
                    new string('a', 40),
                    1),
                new MinecraftReleaseInfo(
                    "1.20.1",
                    loadedAtUtc.AddYears(-1),
                    new Uri("https://piston-meta.mojang.com/v1/packages/b/1.20.1.json"),
                    new string('b', 40),
                    1),
            ]);
    }

    private sealed class BlockingReleaseCatalog : IMinecraftReleaseCatalog
    {
        private readonly TaskCompletionSource<MinecraftReleaseCatalogSnapshot> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MinecraftReleaseCatalogSnapshot> GetStableReleasesAsync(
            CancellationToken cancellationToken = default)
        {
            Requested.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(MinecraftReleaseCatalogSnapshot snapshot) =>
            _completion.TrySetResult(snapshot);
    }
}
