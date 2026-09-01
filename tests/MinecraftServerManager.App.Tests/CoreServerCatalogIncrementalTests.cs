using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class CoreServerCatalogIncrementalTests
{
    [Fact]
    public async Task DiskCache_UsesVersionMarkerAndTransitionsFromFreshToStaleThenExpires()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var cacheRoot = Path.Combine(directory.Path, "cache");
        Directory.CreateDirectory(cacheRoot);
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero));
        var cache = new CoreServerCatalogCache(cacheRoot, clock);
        var product = Product("official:paper", CoreServerSoftware.Paper, "Paper");
        var version = Version(product, "1.21.11");
        var entries = new Dictionary<string, CoreServerCatalogCacheEntry>(
            StringComparer.OrdinalIgnoreCase)
        {
            [product.CoreId] = new(product.CoreId, clock.GetUtcNow(), [version])
        };

        await cache.SaveAsync(entries, CancellationToken.None);
        var fresh = await cache.LoadAsync([product], CancellationToken.None);

        Assert.Equal(CoreServerCatalogBootstrapKind.FreshCache, fresh.Kind);
        Assert.Equal(version, Assert.Single(fresh.Entries[product.CoreId].Versions));

        clock.Advance(CoreServerCatalogCache.FreshnessTtl + TimeSpan.FromMinutes(1));
        var stale = await cache.LoadAsync([product], CancellationToken.None);
        Assert.Equal(CoreServerCatalogBootstrapKind.StaleCache, stale.Kind);
        Assert.Single(stale.Entries);

        clock.Advance(
            CoreServerCatalogCache.MaximumRetention
            - CoreServerCatalogCache.FreshnessTtl
            + TimeSpan.FromMinutes(1));
        var expired = await cache.LoadAsync([product], CancellationToken.None);
        Assert.Equal(CoreServerCatalogBootstrapKind.BuiltInBaseline, expired.Kind);
        Assert.Empty(expired.Entries);
    }

    [Fact]
    public async Task DiskCache_DifferentCatalogVersionIsIgnoredInsteadOfInventingRows()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var cacheRoot = Path.Combine(directory.Path, "cache");
        Directory.CreateDirectory(cacheRoot);
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero));
        var cache = new CoreServerCatalogCache(cacheRoot, clock);
        var product = Product("official:paper", CoreServerSoftware.Paper, "Paper");
        await cache.SaveAsync(
            new Dictionary<string, CoreServerCatalogCacheEntry>
            {
                [product.CoreId] = new(product.CoreId, clock.GetUtcNow(), [Version(product, "1.21.11")])
            },
            CancellationToken.None);
        var cacheFile = Assert.Single(Directory.GetFiles(cacheRoot, "*.json"));
        var json = await File.ReadAllTextAsync(cacheFile);
        await File.WriteAllTextAsync(
            cacheFile,
            json.Replace(
                CoreServerCatalogCache.CatalogVersion,
                "future-incompatible-catalog",
                StringComparison.Ordinal));

        var snapshot = await cache.LoadAsync([product], CancellationToken.None);

        Assert.Equal(CoreServerCatalogBootstrapKind.BuiltInBaseline, snapshot.Kind);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public async Task ViewModel_FirstPaintUsesCachedVersionsWithoutBusyAndDisposeCancelsRefresh()
    {
        var product = Product("official:paper", CoreServerSoftware.Paper, "Paper");
        var version = Version(product, "1.21.11");
        var workflow = IncrementalWorkflow.Blocking(product, version);
        var viewModel = new CoreServerCreationViewModel(workflow);

        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await workflow.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(product, Assert.Single(viewModel.Cores));
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsInputEnabled);
        Assert.True(viewModel.IsCatalogRefreshing);
        await viewModel.SelectCoreAsync(product);
        Assert.Equal(version, Assert.Single(viewModel.Versions));
        Assert.True(viewModel.RequiresMinecraftEula);
        viewModel.MinecraftEulaAccepted = true;
        Assert.True(viewModel.CanCreate);
        Assert.Contains("可信快取", viewModel.VersionStateText, StringComparison.Ordinal);

        viewModel.Dispose();
        await workflow.RefreshCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await viewModel.CatalogRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(viewModel.IsCatalogRefreshing);
    }

    [Fact]
    public async Task ViewModel_RefreshFailureRetainsTrustedCacheAndNeverAddsFallbackVersion()
    {
        var product = Product("hybrid:arclight", CoreServerSoftware.Arclight, "Arclight");
        var cached = Version(product, "1.20.1-forge");
        var workflow = IncrementalWorkflow.Failing(product, cached);
        var viewModel = new CoreServerCreationViewModel(workflow);

        await viewModel.InitializeAsync();
        await viewModel.SelectCoreAsync(product);
        await viewModel.CatalogRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(cached, Assert.Single(viewModel.Versions));
        Assert.DoesNotContain(viewModel.Versions, item => item.VersionId == "fallback");
        Assert.Contains("保留上次可信快取", viewModel.VersionStateText, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
        viewModel.Dispose();
    }

    [Fact]
    public async Task Workflow_RefreshesProviderGroupsConcurrentlyButNeverExceedsBound()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var tracker = new CatalogConcurrencyTracker(expectedInitialConcurrency: 3);
        var sources = Enumerable.Range(0, 5)
            .Select(index =>
            {
                var sourceId = $"source-{index}";
                ICoreServerCreationBackend backend = new GatedCatalogBackend(sourceId, index, tracker);
                return new KeyValuePair<string, ICoreServerCreationBackend>(sourceId, backend);
            })
            .ToArray();
        using var workflow = new CoreServerCreationWorkflow(
            paths,
            new CompositeCoreServerCreationBackend(sources),
            new UnexpectedJavaRuntimeResolver());
        _ = await workflow.GetCatalogBootstrapAsync(CancellationToken.None);
        await using var enumerator = workflow.RefreshCatalogAsync(CancellationToken.None)
            .GetAsyncEnumerator();
        var firstMove = enumerator.MoveNextAsync().AsTask();

        await tracker.InitialConcurrentRequests.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, tracker.ActiveRequests);
        Assert.Equal(3, tracker.MaximumConcurrentRequests);
        tracker.ReleaseAll.TrySetResult(true);

        Assert.True(await firstMove.WaitAsync(TimeSpan.FromSeconds(2)));
        var updates = new List<CoreServerCatalogUpdate> { enumerator.Current };
        while (await enumerator.MoveNextAsync())
        {
            updates.Add(enumerator.Current);
        }

        Assert.False(updates[0].IsFinal);
        Assert.Equal(5, updates.Count(update => !update.IsFinal));
        Assert.True(Assert.Single(updates, update => update.IsFinal).Succeeded);
        Assert.Equal(5, tracker.TotalRequests);
        Assert.InRange(tracker.MaximumConcurrentRequests, 2, 3);
    }

    [Fact]
    public async Task Workflow_CancellationStopsBlockedProviderRefreshWithoutFinalBatch()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var tracker = new CatalogConcurrencyTracker(expectedInitialConcurrency: 1);
        const string sourceId = "blocked-source";
        using var workflow = new CoreServerCreationWorkflow(
            paths,
            new CompositeCoreServerCreationBackend(
            [
                new KeyValuePair<string, ICoreServerCreationBackend>(
                    sourceId,
                    new GatedCatalogBackend(sourceId, 0, tracker))
            ]),
            new UnexpectedJavaRuntimeResolver());
        _ = await workflow.GetCatalogBootstrapAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = workflow.RefreshCatalogAsync(cancellation.Token)
            .GetAsyncEnumerator();
        var firstMove = enumerator.MoveNextAsync().AsTask();
        await tracker.InitialConcurrentRequests.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            firstMove.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, tracker.ActiveRequests);
        Assert.Equal(1, tracker.TotalRequests);
    }

    [Fact]
    public async Task StaDialog_ClosesImmediatelyAndCancelsBackgroundCatalogRefresh()
    {
        var product = Product("official:vanilla", CoreServerSoftware.Vanilla, "Minecraft 原版");
        var workflow = IncrementalWorkflow.Blocking(product, Version(product, "1.21.11"));

        WpfStaTestHost.Run(() =>
        {
            var viewModel = new CoreServerCreationViewModel(workflow);
            var dialog = new CoreServerCreationDialog(viewModel);
            var timedOut = false;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                timedOut = true;
                dialog.Close();
            };
            dialog.ContentRendered += (_, _) =>
            {
                Assert.False(viewModel.IsBusy);
                dialog.Close();
            };

            timeout.Start();
            var result = dialog.ShowDialog();
            timeout.Stop();
            Assert.False(timedOut);
            Assert.False(result);
        });

        await workflow.RefreshCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static CoreServerProduct Product(
        string coreId,
        CoreServerSoftware software,
        string displayName)
        => new(software, coreId, displayName, $"{displayName} description");

    private static CoreServerVersion Version(CoreServerProduct product, string version)
        => new(
            product.CoreId,
            $"{product.CoreId}:{version}",
            version,
            version.Split('-', 2)[0],
            "verified upstream build",
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
            IsRecommended: true);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class IncrementalWorkflow :
        ICoreServerCreationWorkflow,
        IIncrementalCoreServerCatalogWorkflow
    {
        private readonly CoreServerProduct _product;
        private readonly CoreServerVersion _cached;
        private readonly bool _fail;

        private IncrementalWorkflow(
            CoreServerProduct product,
            CoreServerVersion cached,
            bool fail)
        {
            _product = product;
            _cached = cached;
            _fail = fail;
        }

        public TaskCompletionSource<bool> RefreshStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> RefreshCancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public static IncrementalWorkflow Blocking(
            CoreServerProduct product,
            CoreServerVersion cached)
            => new(product, cached, fail: false);

        public static IncrementalWorkflow Failing(
            CoreServerProduct product,
            CoreServerVersion cached)
            => new(product, cached, fail: true);

        public ValueTask<CoreServerCatalogBootstrap> GetCatalogBootstrapAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CoreServerCatalogBootstrap(
                [_product],
                new Dictionary<string, IReadOnlyList<CoreServerVersion>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [_product.CoreId] = [_cached]
                },
                CoreServerCatalogBootstrapKind.FreshCache,
                DateTimeOffset.UtcNow,
                "已載入可信快取；背景更新中。"));
        }

        public async IAsyncEnumerable<CoreServerCatalogUpdate> RefreshCatalogAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RefreshStarted.TrySetResult(true);
            if (_fail)
            {
                yield return new CoreServerCatalogUpdate(
                    _product,
                    [_cached],
                    "test",
                    1,
                    1,
                    Succeeded: false,
                    IsFinal: false,
                    "更新失敗，保留快取。");
                yield return new CoreServerCatalogUpdate(
                    null,
                    [],
                    string.Empty,
                    1,
                    1,
                    Succeeded: false,
                    IsFinal: true,
                    "背景更新完成；保留快取。");
                yield break;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    RefreshCancellationObserved.TrySetResult(true);
                }
            }
        }

        public Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerProduct>>([_product]);

        public Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
            CoreServerProduct core,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreServerVersion>>([_cached]);

        public Task<ServerInstance> CreateAsync(
            CoreServerCreationRequest request,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class CatalogConcurrencyTracker(int expectedInitialConcurrency)
    {
        private int _active;
        private int _maximum;
        private int _total;

        public int ActiveRequests => Volatile.Read(ref _active);

        public int MaximumConcurrentRequests => Volatile.Read(ref _maximum);

        public int TotalRequests => Volatile.Read(ref _total);

        public TaskCompletionSource<bool> InitialConcurrentRequests { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseAll { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _total);
            var active = Interlocked.Increment(ref _active);
            int observed;
            while (active > (observed = Volatile.Read(ref _maximum)))
            {
                if (Interlocked.CompareExchange(ref _maximum, active, observed) == observed)
                {
                    break;
                }
            }

            if (active == expectedInitialConcurrency)
            {
                InitialConcurrentRequests.TrySetResult(true);
            }

            try
            {
                await ReleaseAll.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class GatedCatalogBackend(
        string sourceId,
        int index,
        CatalogConcurrencyTracker tracker) : ICoreServerCreationBackend
    {
        private readonly CoreServerBackendProduct _product = new(
            Product($"{sourceId}:core", CoreServerSoftware.Paper, $"Core {index}"),
            CoreType.Paper,
            sourceId);

        public Task<IReadOnlyList<CoreServerBackendProduct>> GetProductsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CoreServerBackendProduct>>([_product]);
        }

        public async Task<IReadOnlyList<CoreServerBackendVersion>> GetVersionsAsync(
            CoreServerBackendProduct product,
            CancellationToken cancellationToken)
        {
            await tracker.WaitAsync(cancellationToken);
            return
            [
                new CoreServerBackendVersion(
                    Version(_product.Product, "1.21.11"),
                    JavaMajorVersion: 21)
            ];
        }

        public Task<CoreServerInstallPlan> ResolveExactAsync(
            CoreServerBackendProduct product,
            CoreServerBackendVersion version,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CoreServerBackendInstallResult> InstallAsync(
            CoreServerInstallPlan plan,
            string stagingDirectory,
            string javaExecutablePath,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class UnexpectedJavaRuntimeResolver : IModrinthJavaRuntimeResolver
    {
        public Task<string> ResolveAsync(
            int majorVersion,
            IProgress<double>? downloadProgress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
