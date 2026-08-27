using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace MinecraftServerManager.App.Services;

public sealed partial class CoreServerCreationWorkflow
{
    private const int MaximumCatalogProducts = 64;
    private const int MaximumConcurrentCatalogSources = 3;
    private static readonly TimeSpan CatalogRefreshCooldown = TimeSpan.FromMinutes(2);

    public async ValueTask<CoreServerCatalogBootstrap> GetCatalogBootstrapAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _catalogLifetimeCancellation.Token);
        await EnsureCatalogBootstrapStateAsync(linked.Token).ConfigureAwait(false);

        IReadOnlyList<CoreServerBackendProduct> products;
        Dictionary<string, CoreServerCatalogCacheEntry> entries;
        string? warning;
        lock (_catalogStateSync)
        {
            products = _catalogProducts ?? [];
            entries = _catalogEntries is null
                ? new Dictionary<string, CoreServerCatalogCacheEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CoreServerCatalogCacheEntry>(
                    _catalogEntries,
                    StringComparer.OrdinalIgnoreCase);
            warning = _catalogCacheLoadWarning;
        }

        var now = _catalogTimeProvider.GetUtcNow();
        var kind = entries.Count == 0
            ? CoreServerCatalogBootstrapKind.BuiltInBaseline
            : entries.Values.Any(entry => now - entry.RefreshedAtUtc > CoreServerCatalogCache.FreshnessTtl)
                ? CoreServerCatalogBootstrapKind.StaleCache
                : CoreServerCatalogBootstrapKind.FreshCache;
        DateTimeOffset? cachedAt = entries.Count == 0
            ? null
            : entries.Values.Max(static entry => entry.RefreshedAtUtc);
        var status = kind switch
        {
            CoreServerCatalogBootstrapKind.FreshCache =>
                L("core.catalog.bootstrap.fresh"),
            CoreServerCatalogBootstrapKind.StaleCache =>
                L("core.catalog.bootstrap.stale"),
            _ => L("core.catalog.bootstrap.baseline")
        };
        if (!string.IsNullOrWhiteSpace(warning))
        {
            status += L("core.catalog.cacheRejectedSuffix", warning);
        }

        return new CoreServerCatalogBootstrap(
            products.Select(static item => item.Product).ToArray(),
            entries.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Versions,
                StringComparer.OrdinalIgnoreCase),
            kind,
            cachedAt,
            status);
    }

    public async IAsyncEnumerable<CoreServerCatalogUpdate> RefreshCatalogAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _catalogLifetimeCancellation.Token);
        await _catalogRefreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        CancellationTokenSource? workersCancellation = null;
        Task? completion = null;
        DateTimeOffset? recordedAttempt = null;
        var refreshReachedTerminalState = false;
        try
        {
            await EnsureCatalogBootstrapStateAsync(linked.Token).ConfigureAwait(false);
            IReadOnlyList<CoreServerBackendProduct> products;
            DateTimeOffset? lastAttempt;
            bool lastRefreshHadFailures;
            lock (_catalogStateSync)
            {
                products = _catalogProducts ?? [];
                lastAttempt = _lastCatalogRefreshAttemptUtc;
                lastRefreshHadFailures = _lastCatalogRefreshHadFailures;
            }

            var now = _catalogTimeProvider.GetUtcNow();
            if (lastAttempt is { } previous
                && now >= previous
                && now - previous < CatalogRefreshCooldown)
            {
                yield return new CoreServerCatalogUpdate(
                    Core: null,
                    Versions: [],
                    SourceId: string.Empty,
                    CompletedCores: products.Count,
                    TotalCores: products.Count,
                    Succeeded: !lastRefreshHadFailures,
                    IsFinal: true,
                    StatusText: lastRefreshHadFailures
                        ? L("core.catalog.cooldown.failed")
                        : L("core.catalog.cooldown.succeeded")
                );
                yield break;
            }

            lock (_catalogStateSync)
            {
                _lastCatalogRefreshAttemptUtc = now;
            }
            recordedAttempt = now;

            if (products.Count == 0)
            {
                lock (_catalogStateSync)
                {
                    _lastCatalogRefreshAttemptUtc = _catalogTimeProvider.GetUtcNow();
                    _lastCatalogRefreshHadFailures = false;
                }

                refreshReachedTerminalState = true;
                yield return new CoreServerCatalogUpdate(
                    Core: null,
                    Versions: [],
                    SourceId: string.Empty,
                    CompletedCores: 0,
                    TotalCores: 0,
                    Succeeded: true,
                    IsFinal: true,
                    StatusText: L("core.catalog.noSources")
                );
                yield break;
            }

            var channel = Channel.CreateBounded<CatalogRefreshResult>(
                new BoundedChannelOptions(Math.Min(products.Count, 8))
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });
            workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            var sourceConcurrency = new SemaphoreSlim(MaximumConcurrentCatalogSources);
            var groups = products
                .GroupBy(static product => product.SourceId, StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => group.ToArray())
                .ToArray();
            var workers = groups
                .Select(group => RefreshSourceGroupAsync(
                    group,
                    channel.Writer,
                    sourceConcurrency,
                    workersCancellation.Token))
                .ToArray();
            completion = CompleteCatalogChannelAsync(workers, channel.Writer, sourceConcurrency);

            var completed = 0;
            var failures = 0;
            await foreach (var result in channel.Reader.ReadAllAsync(workersCancellation.Token))
            {
                completed++;
                var statusSuffix = string.Empty;
                IReadOnlyList<CoreServerVersion> visibleVersions;
                if (result.Error is null)
                {
                    var entry = new CoreServerCatalogCacheEntry(
                        result.Product.Product.CoreId,
                        _catalogTimeProvider.GetUtcNow(),
                        result.Versions.Select(static item => item.Version).ToArray());
                    Dictionary<string, CoreServerCatalogCacheEntry> snapshot;
                    lock (_catalogStateSync)
                    {
                        _catalogEntries ??= new Dictionary<string, CoreServerCatalogCacheEntry>(
                            StringComparer.OrdinalIgnoreCase);
                        _catalogEntries[entry.CoreId] = entry;
                        snapshot = new Dictionary<string, CoreServerCatalogCacheEntry>(
                            _catalogEntries,
                            StringComparer.OrdinalIgnoreCase);
                    }

                    try
                    {
                        await _catalogCache.SaveAsync(snapshot, workersCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (workersCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsRecoverableCacheError(exception))
                    {
                        statusSuffix = L("core.catalog.cacheWriteFailedSuffix");
                    }

                    visibleVersions = entry.Versions;
                }
                else
                {
                    failures++;
                    lock (_catalogStateSync)
                    {
                        visibleVersions = _catalogEntries is not null
                                          && _catalogEntries.TryGetValue(
                                              result.Product.Product.CoreId,
                                              out var cached)
                            ? cached.Versions
                            : [];
                    }
                }

                var succeeded = result.Error is null;
                var message = succeeded
                    ? L(
                        "core.catalog.updated",
                        result.Product.Product.DisplayName,
                        visibleVersions.Count,
                        statusSuffix)
                    : visibleVersions.Count > 0
                        ? L(
                            "core.catalog.failedWithCache",
                            result.Product.Product.DisplayName,
                            GetSafeCatalogError(result.Error!))
                        : L(
                            "core.catalog.failed",
                            result.Product.Product.DisplayName,
                            GetSafeCatalogError(result.Error!));
                yield return new CoreServerCatalogUpdate(
                    result.Product.Product,
                    visibleVersions,
                    result.Product.SourceId,
                    completed,
                    products.Count,
                    succeeded,
                    IsFinal: false,
                    L("core.catalog.progress", message, completed, products.Count));
            }

            await completion.ConfigureAwait(false);
            completion = null;
            lock (_catalogStateSync)
            {
                _lastCatalogRefreshAttemptUtc = _catalogTimeProvider.GetUtcNow();
                _lastCatalogRefreshHadFailures = failures > 0;
            }

            refreshReachedTerminalState = true;
            yield return new CoreServerCatalogUpdate(
                Core: null,
                Versions: [],
                SourceId: string.Empty,
                CompletedCores: products.Count,
                TotalCores: products.Count,
                Succeeded: failures == 0,
                IsFinal: true,
                StatusText: failures == 0
                    ? L("core.catalog.completed", products.Count)
                    : L("core.catalog.completedWithFailures", failures)
            );
        }
        finally
        {
            if (workersCancellation is not null)
            {
                workersCancellation.Cancel();
                if (completion is not null)
                {
                    try
                    {
                        await completion.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                workersCancellation.Dispose();
            }

            if (recordedAttempt is { } attempt && !refreshReachedTerminalState)
            {
                lock (_catalogStateSync)
                {
                    if (_lastCatalogRefreshAttemptUtc == attempt)
                    {
                        _lastCatalogRefreshAttemptUtc = null;
                    }
                }
            }

            _catalogRefreshGate.Release();
        }
    }

    private async Task EnsureCatalogBootstrapStateAsync(CancellationToken cancellationToken)
    {
        lock (_catalogStateSync)
        {
            if (_catalogProducts is not null && _catalogEntries is not null)
            {
                return;
            }
        }

        await _catalogBootstrapGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_catalogStateSync)
            {
                if (_catalogProducts is not null && _catalogEntries is not null)
                {
                    return;
                }
            }

            var products = await GetValidatedProductsAsync(cancellationToken).ConfigureAwait(false);
            if (products.Count > MaximumCatalogProducts)
            {
                throw new InvalidDataException(L("core.catalog.error.productLimit"));
            }

            CoreServerCatalogCacheSnapshot cache;
            string? warning = null;
            try
            {
                cache = await _catalogCache.LoadAsync(
                        products.Select(static item => item.Product).ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableCacheError(exception))
            {
                cache = new CoreServerCatalogCacheSnapshot(
                    new Dictionary<string, CoreServerCatalogCacheEntry>(
                        StringComparer.OrdinalIgnoreCase),
                    CoreServerCatalogBootstrapKind.BuiltInBaseline,
                    CachedAtUtc: null);
                warning = GetSafeCatalogError(exception);
            }

            lock (_catalogStateSync)
            {
                _catalogProducts = products;
                _catalogEntries = new Dictionary<string, CoreServerCatalogCacheEntry>(
                    cache.Entries,
                    StringComparer.OrdinalIgnoreCase);
                _catalogCacheLoadWarning = warning;
            }
        }
        finally
        {
            _catalogBootstrapGate.Release();
        }
    }

    private async Task RefreshSourceGroupAsync(
        IReadOnlyList<CoreServerBackendProduct> products,
        ChannelWriter<CatalogRefreshResult> writer,
        SemaphoreSlim sourceConcurrency,
        CancellationToken cancellationToken)
    {
        await sourceConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var product in products)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var versions = await GetValidatedVersionsAsync(product, cancellationToken)
                        .ConfigureAwait(false);
                    await writer.WriteAsync(
                            new CatalogRefreshResult(product, versions, Error: null),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await writer.WriteAsync(
                            new CatalogRefreshResult(product, [], exception),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            sourceConcurrency.Release();
        }
    }

    private static async Task CompleteCatalogChannelAsync(
        IReadOnlyList<Task> workers,
        ChannelWriter<CatalogRefreshResult> writer,
        SemaphoreSlim sourceConcurrency)
    {
        Exception? error = null;
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            writer.TryComplete(error);
            sourceConcurrency.Dispose();
        }
    }

    private static bool IsRecoverableCacheError(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException;

    private static string GetSafeCatalogError(Exception exception)
    {
        var message = exception.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return L("common.unexpectedError");
        }

        var sanitized = new string(message
            .Where(static character => !char.IsControl(character))
            .Take(300)
            .ToArray());
        return sanitized.Length == 0 ? L("common.unexpectedError") : sanitized;
    }

    private static string L(string key, params object?[] arguments) =>
        LocalizationService.Current.Get(key, arguments);

    private sealed record CatalogRefreshResult(
        CoreServerBackendProduct Product,
        IReadOnlyList<CoreServerBackendVersion> Versions,
        Exception? Error);
}
