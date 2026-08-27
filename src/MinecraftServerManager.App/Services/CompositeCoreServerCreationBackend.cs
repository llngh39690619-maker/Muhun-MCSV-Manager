namespace MinecraftServerManager.App.Services;

internal sealed class CompositeCoreServerCreationBackend
    : ICoreServerCreationBackend
{
    private readonly IReadOnlyDictionary<string, ICoreServerCreationBackend> _sources;

    public CompositeCoreServerCreationBackend(
        IEnumerable<KeyValuePair<string, ICoreServerCreationBackend>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var materialized = sources.ToArray();
        if (materialized.Length == 0
            || materialized.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null))
        {
            throw new ArgumentException("至少需要一個具名核心來源。", nameof(sources));
        }

        _sources = materialized.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<CoreServerBackendProduct>> GetProductsAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<CoreServerBackendProduct>();
        foreach (var source in _sources.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var products = await source.Value.GetProductsAsync(cancellationToken).ConfigureAwait(false);
            if (products.Any(product => !product.SourceId.Equals(source.Key, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"核心來源 {source.Key} 回傳了錯誤 SourceId。");
            }

            result.AddRange(products);
        }

        return result;
    }

    public Task<IReadOnlyList<CoreServerBackendVersion>> GetVersionsAsync(
        CoreServerBackendProduct product,
        CancellationToken cancellationToken)
        => Resolve(product.SourceId).GetVersionsAsync(product, cancellationToken);

    public Task<CoreServerInstallPlan> ResolveExactAsync(
        CoreServerBackendProduct product,
        CoreServerBackendVersion version,
        CancellationToken cancellationToken)
        => Resolve(product.SourceId).ResolveExactAsync(product, version, cancellationToken);

    public Task<CoreServerBackendInstallResult> InstallAsync(
        CoreServerInstallPlan plan,
        string stagingDirectory,
        string javaExecutablePath,
        IProgress<CoreServerCreationProgress> progress,
        CancellationToken cancellationToken)
        => Resolve(plan.Product.SourceId).InstallAsync(
            plan,
            stagingDirectory,
            javaExecutablePath,
            progress,
            cancellationToken);

    private ICoreServerCreationBackend Resolve(string sourceId)
        => _sources.TryGetValue(sourceId, out var source)
            ? source
            : throw new InvalidDataException($"找不到核心來源：{sourceId}");
}
