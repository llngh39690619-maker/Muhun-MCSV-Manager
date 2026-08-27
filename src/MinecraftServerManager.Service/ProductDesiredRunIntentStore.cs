using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftServerManager.Service;

/// <summary>
/// Atomic, bounded desired-run set. It records operator intent only; process state and launch
/// diagnostics never enter this file. A malformed file is never replaced or interpreted as an
/// empty set by a read operation.
/// </summary>
public sealed class ProductDesiredRunIntentStore(ProductDataLayout layout)
{
    internal const string FileName = "desired-run.v1.json";
    internal const int MaximumEntries = ProductServerRegistry.MaximumServers;
    internal const int MaximumFileBytes = 16 * 1024;
    private const int SchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly string _path = Path.Combine(layout.Operations, FileName);
    private HashSet<Guid> _desired = [];
    private bool _loaded;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<Guid> GetDesiredServerIds()
    {
        lock (_stateGate)
        {
            EnsureLoaded();
            return _desired.Order().ToArray();
        }
    }

    public bool IsDesired(Guid serverId)
    {
        if (serverId == Guid.Empty)
        {
            return false;
        }

        lock (_stateGate)
        {
            EnsureLoaded();
            return _desired.Contains(serverId);
        }
    }

    public async Task SetDesiredAsync(
        Guid serverId,
        bool desired,
        CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            HashSet<Guid> snapshot;
            lock (_stateGate)
            {
                snapshot = [.. _desired];
            }

            var changed = desired ? snapshot.Add(serverId) : snapshot.Remove(serverId);
            if (!changed)
            {
                return;
            }

            if (snapshot.Count > MaximumEntries)
            {
                throw new InvalidOperationException("Desired-run intent has reached its server limit.");
            }

            await SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                _desired = snapshot;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        layout.EnsureCreated();
        EnsureStoragePathIsNotReparsePoint();
        HashSet<Guid> loaded = [];
        if (File.Exists(_path))
        {
            if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Desired-run intent file cannot be a reparse point.");
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4_096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is < 2 or > MaximumFileBytes)
            {
                throw new InvalidDataException("Desired-run intent file size is invalid.");
            }

            IntentDocument document;
            try
            {
                document = await JsonSerializer.DeserializeAsync(
                        stream,
                        ProductDesiredRunIntentJsonContext.Default.IntentDocument,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException("Desired-run intent document is empty.");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Desired-run intent JSON is invalid.", error);
            }

            if (document.SchemaVersion != SchemaVersion ||
                document.ServerIds is null ||
                document.ServerIds.Count > MaximumEntries)
            {
                throw new InvalidDataException("Desired-run intent document is unsupported or unbounded.");
            }

            foreach (var id in document.ServerIds)
            {
                if (id == Guid.Empty || !loaded.Add(id))
                {
                    throw new InvalidDataException("Desired-run intent contains an invalid or duplicate server id.");
                }
            }
        }

        lock (_stateGate)
        {
            _desired = loaded;
            _loaded = true;
        }
    }

    private async Task SaveAsync(HashSet<Guid> desired, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(layout.Operations);
        EnsureStoragePathIsNotReparsePoint();
        if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Desired-run intent file cannot be a reparse point.");
        }

        var document = new IntentDocument(SchemaVersion, desired.Order().ToArray());
        var temporaryPath = Path.Combine(
            layout.Operations,
            $".{FileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4_096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        ProductDesiredRunIntentJsonContext.Default.IntentDocument,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > MaximumFileBytes)
            {
                throw new InvalidDataException("Desired-run intent file exceeds its size limit.");
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void EnsureStoragePathIsNotReparsePoint()
    {
        var root = Path.GetFullPath(layout.Root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var current = new DirectoryInfo(layout.Operations); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Desired-run intent path cannot traverse a reparse point.");
            }

            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidDataException("Desired-run intent path is outside the product data root.");
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("Desired-run intent has not been loaded.");
        }
    }

    internal sealed record IntentDocument(int SchemaVersion, IReadOnlyList<Guid> ServerIds);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(ProductDesiredRunIntentStore.IntentDocument))]
internal sealed partial class ProductDesiredRunIntentJsonContext : JsonSerializerContext;
