using System.Text.Json;

namespace MinecraftServerManager.App.Services;

internal sealed record CoreServerCatalogCacheEntry(
    string CoreId,
    DateTimeOffset RefreshedAtUtc,
    IReadOnlyList<CoreServerVersion> Versions);

internal sealed record CoreServerCatalogCacheSnapshot(
    IReadOnlyDictionary<string, CoreServerCatalogCacheEntry> Entries,
    CoreServerCatalogBootstrapKind Kind,
    DateTimeOffset? CachedAtUtc);

/// <summary>
/// A bounded, versioned discovery cache. It never contains an install plan or artifact trust
/// decision; creation always re-resolves the selected public row through the live backend.
/// </summary>
internal sealed class CoreServerCatalogCache
{
    internal const int SchemaVersion = 1;
    internal const string CatalogVersion = "core-server-catalog-2026-08-v1";
    internal static readonly TimeSpan FreshnessTtl = TimeSpan.FromHours(6);
    internal static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(30);

    private const long MaximumCacheBytes = 8L * 1024 * 1024;
    private const int MaximumCoreEntries = 64;
    private const int MaximumVersionsPerCore = 2_048;
    private const string CacheFileName = "core-server-catalog-v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 24
    };

    private readonly string _cacheRoot;
    private readonly string _cachePath;
    private readonly TimeProvider _timeProvider;

    public CoreServerCatalogCache(string cacheDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        _cachePath = Path.Combine(_cacheRoot, CacheFileName);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CoreServerCatalogCacheSnapshot> LoadAsync(
        IReadOnlyList<CoreServerProduct> baselineProducts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baselineProducts);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_cachePath))
        {
            return EmptySnapshot();
        }

        EnsureCachePathIsSafe(requireDirectory: true);
        var info = new FileInfo(_cachePath);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || info.Length is < 2 or > MaximumCacheBytes)
        {
            throw new InvalidDataException("核心 catalog 快取的類型或大小無效。");
        }

        CacheEnvelope envelope;
        await using (var stream = new FileStream(
                         _cachePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         32 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            try
            {
                envelope = await JsonSerializer.DeserializeAsync<CacheEnvelope>(
                        stream,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException("核心 catalog 快取是空的。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("核心 catalog 快取 JSON 無效。", exception);
            }
        }

        if (envelope.SchemaVersion != SchemaVersion
            || !string.Equals(envelope.CatalogVersion, CatalogVersion, StringComparison.Ordinal)
            || envelope.Entries is null
            || envelope.Entries.Count > MaximumCoreEntries)
        {
            return EmptySnapshot();
        }

        var now = _timeProvider.GetUtcNow();
        if (!IsPlausibleCacheTime(envelope.WrittenAtUtc, now))
        {
            return EmptySnapshot();
        }

        var baselineIds = baselineProducts
            .Where(static product => !string.IsNullOrWhiteSpace(product.CoreId))
            .Select(static product => product.CoreId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, CoreServerCatalogCacheEntry>(
            StringComparer.OrdinalIgnoreCase);
        var anyStale = false;
        DateTimeOffset? newest = null;
        foreach (var entry in envelope.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEntry(entry, now);
            if (!baselineIds.Contains(entry.CoreId))
            {
                continue;
            }

            var age = now - entry.RefreshedAtUtc;
            if (age > MaximumRetention)
            {
                continue;
            }

            if (!entries.TryAdd(entry.CoreId, entry))
            {
                throw new InvalidDataException("核心 catalog 快取含有重複 CoreId。");
            }

            anyStale |= age > FreshnessTtl;
            newest = newest is null || entry.RefreshedAtUtc > newest
                ? entry.RefreshedAtUtc
                : newest;
        }

        if (entries.Count == 0)
        {
            return EmptySnapshot();
        }

        return new CoreServerCatalogCacheSnapshot(
            entries,
            anyStale
                ? CoreServerCatalogBootstrapKind.StaleCache
                : CoreServerCatalogBootstrapKind.FreshCache,
            newest);
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, CoreServerCatalogCacheEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaximumCoreEntries)
        {
            throw new InvalidDataException("核心 catalog 快取項目超過安全上限。");
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var entry in entries.Values)
        {
            ValidateEntry(entry, now);
        }

        Directory.CreateDirectory(_cacheRoot);
        EnsureCachePathIsSafe(requireDirectory: true);
        var envelope = new CacheEnvelope(
            SchemaVersion,
            CatalogVersion,
            now,
            entries.Values
                .OrderBy(static entry => entry.CoreId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        var temporary = Path.Combine(
            _cacheRoot,
            $".{CacheFileName}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        envelope,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumCacheBytes)
                {
                    throw new InvalidDataException("核心 catalog 快取超過安全大小上限。");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_cachePath)
                && File.GetAttributes(_cachePath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("核心 catalog 快取不得是 reparse point。");
            }

            File.Move(temporary, _cachePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void EnsureCachePathIsSafe(bool requireDirectory)
    {
        if (requireDirectory && !Directory.Exists(_cacheRoot))
        {
            throw new DirectoryNotFoundException($"找不到核心 catalog 快取資料夾：{_cacheRoot}");
        }

        if (Directory.Exists(_cacheRoot))
        {
            if (File.GetAttributes(_cacheRoot).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("核心 catalog 快取資料夾不得是 reparse point。");
            }

            _ = MinecraftServerManager.Core.Services.SafePath.EnsureWithinRoot(
                _cacheRoot,
                CacheFileName,
                allowRoot: false);
        }
    }

    private static void ValidateEntry(CoreServerCatalogCacheEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsSafeRequiredText(entry.CoreId)
            || !IsPlausibleCacheTime(entry.RefreshedAtUtc, now)
            || entry.Versions is null
            || entry.Versions.Count > MaximumVersionsPerCore)
        {
            throw new InvalidDataException("核心 catalog 快取 entry 無效。");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in entry.Versions)
        {
            ArgumentNullException.ThrowIfNull(version);
            if (!version.CoreId.Equals(entry.CoreId, StringComparison.OrdinalIgnoreCase)
                || !IsSafeRequiredText(version.CoreId)
                || !IsSafeRequiredText(version.VersionId)
                || !IsSafeRequiredText(version.DisplayName)
                || !IsSafeRequiredText(version.MinecraftVersion)
                || !IsSafeOptionalText(version.Build)
                || version.ReleasedAtUtc is { } released
                    && (released < new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        || released > now + TimeSpan.FromDays(2))
                || !ids.Add(version.VersionId))
            {
                throw new InvalidDataException("核心 catalog 快取 version 無效或重複。");
            }
        }
    }

    private static bool IsPlausibleCacheTime(DateTimeOffset value, DateTimeOffset now)
        => value > DateTimeOffset.UnixEpoch && value <= now + TimeSpan.FromMinutes(5);

    private static bool IsSafeRequiredText(string? value)
        => !string.IsNullOrWhiteSpace(value) && IsSafeOptionalText(value);

    private static bool IsSafeOptionalText(string? value)
        => value is not null
           && value.Length <= 2_048
           && !value.Any(static character => char.IsControl(character));

    private static CoreServerCatalogCacheSnapshot EmptySnapshot()
        => new(
            new Dictionary<string, CoreServerCatalogCacheEntry>(StringComparer.OrdinalIgnoreCase),
            CoreServerCatalogBootstrapKind.BuiltInBaseline,
            CachedAtUtc: null);

    private sealed record CacheEnvelope(
        int SchemaVersion,
        string CatalogVersion,
        DateTimeOffset WrittenAtUtc,
        IReadOnlyList<CoreServerCatalogCacheEntry> Entries);
}
