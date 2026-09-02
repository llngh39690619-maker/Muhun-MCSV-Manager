using System.Buffers;
using System.Text.Json;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

public sealed record ProductKnownPlayerRecord(
    string Name,
    Guid? Uuid,
    bool Online,
    bool Operator,
    bool Whitelisted,
    bool Banned,
    DateTimeOffset? LastSeenUtc);

/// <summary>
/// Reads the four fixed Minecraft player registries below a Service-owned server directory. The
/// caller supplies only a registry identity, never a path; every file is bounded and opened
/// without following reparse points or hard links.
/// </summary>
public sealed class ProductKnownPlayerRegistryReader(
    ProductDataLayout layout,
    ProductServerRegistry registry)
{
    internal const int MaximumKnownPlayers = 4_096;
    internal const long MaximumRegistryFileBytes = 16L * 1024 * 1024;
    private const int MaximumInspectedEntriesPerFile = 65_536;

    public async Task<IReadOnlyList<ProductKnownPlayerRecord>> ReadAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (serverId == Guid.Empty || !registry.TryGet(serverId, out var registration))
        {
            return [];
        }

        try
        {
            var serverRoot = ProductServerRegistrationValidator.ResolveOwnedPath(
                layout.Servers,
                registration.ServerDirectory,
                allowRoot: false);
            if (!Directory.Exists(serverRoot))
            {
                return [];
            }

            serverRoot = SafePath.EnsureNoReparsePointsUnderRoot(layout.Servers, serverRoot);
            using var ownershipLease = SafePath.AcquireNoReparseDirectoryChainLease(
                layout.Servers,
                serverRoot);
            var players = new Dictionary<string, PlayerAccumulator>(StringComparer.OrdinalIgnoreCase);
            foreach (var registryFile in RegistryFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await MergeRegistryAsync(
                        serverRoot,
                        registryFile,
                        players,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return players.Values
                .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static player => player.ToRecord())
                .ToArray();
        }
        catch (Exception error) when (IsExpectedReadFailure(error, cancellationToken))
        {
            // Fail closed. A missing, concurrently replaced, malformed, or redirecting managed
            // tree must never cause a fallback to a desktop-visible path.
            return [];
        }
    }

    private static async Task MergeRegistryAsync(
        string serverRoot,
        RegistryFile registryFile,
        IDictionary<string, PlayerAccumulator> players,
        CancellationToken cancellationToken)
    {
        var path = SafePath.EnsureWithinRoot(
            serverRoot,
            registryFile.FileName,
            allowRoot: false);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var lease = ProductNoFollowFileReader.OpenManagedMutable(serverRoot, path);
            if (lease.Stream.Length > MaximumRegistryFileBytes)
            {
                return;
            }

            using var snapshot = await ProductBoundedReadSnapshot.CaptureAsync(
                    lease.Stream,
                    MaximumRegistryFileBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Length < 2)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(
                snapshot.Bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32,
                });
            cancellationToken.ThrowIfCancellationRequested();
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var inspected = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (inspected++ >= MaximumInspectedEntriesPerFile)
                {
                    break;
                }
                if ((inspected & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var nameProperty) ||
                    nameProperty.ValueKind != JsonValueKind.String ||
                    !TryNormalizePlayerName(nameProperty.GetString(), out var name))
                {
                    continue;
                }

                if (!players.TryGetValue(name, out var player))
                {
                    if (players.Count >= MaximumKnownPlayers)
                    {
                        continue;
                    }

                    player = new PlayerAccumulator(name);
                    players.Add(name, player);
                }

                if (item.TryGetProperty("uuid", out var uuidProperty) &&
                    uuidProperty.ValueKind == JsonValueKind.String &&
                    TryParseMinecraftUuid(uuidProperty.GetString(), out var uuid))
                {
                    player.Uuid = uuid;
                }

                switch (registryFile.Kind)
                {
                    case RegistryKind.Operator:
                        player.Operator = true;
                        break;
                    case RegistryKind.Whitelisted:
                        player.Whitelisted = true;
                        break;
                    case RegistryKind.Banned:
                        player.Banned = true;
                        break;
                }
            }
        }
        catch (Exception error) when (IsExpectedFileReadFailure(error, cancellationToken))
        {
            // Each registry is independent. A malformed or concurrently replaced optional file
            // cannot erase safely captured records from the other fixed registries.
        }
    }

    private static bool TryNormalizePlayerName(string? value, out string name)
    {
        name = value?.Trim() ?? string.Empty;
        return name.Length is > 0 and <= 16 &&
               name.All(static character => character is >= 'a' and <= 'z'
                   or >= 'A' and <= 'Z'
                   or >= '0' and <= '9'
                   or '_');
    }

    private static bool TryParseMinecraftUuid(string? value, out Guid uuid)
        => Guid.TryParse(value, out uuid) || Guid.TryParseExact(value, "N", out uuid);

    private static bool IsExpectedReadFailure(Exception error, CancellationToken cancellationToken)
        => error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException ||
           error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static bool IsExpectedFileReadFailure(Exception error, CancellationToken cancellationToken)
        => IsExpectedReadFailure(error, cancellationToken) ||
           error is FileNotFoundException or DirectoryNotFoundException;

    private static readonly RegistryFile[] RegistryFiles =
    [
        new("usercache.json", RegistryKind.Known),
        new("ops.json", RegistryKind.Operator),
        new("whitelist.json", RegistryKind.Whitelisted),
        new("banned-players.json", RegistryKind.Banned),
    ];

    private enum RegistryKind
    {
        Known,
        Operator,
        Whitelisted,
        Banned,
    }

    private sealed record RegistryFile(string FileName, RegistryKind Kind);

    private sealed class PlayerAccumulator(string name)
    {
        public string Name { get; } = name;
        public Guid? Uuid { get; set; }
        public bool Operator { get; set; }
        public bool Whitelisted { get; set; }
        public bool Banned { get; set; }

        public ProductKnownPlayerRecord ToRecord()
            => new(Name, Uuid, false, Operator, Whitelisted, Banned, LastSeenUtc: null);
    }
}

/// <summary>
/// Immutable segmented copy of a mutable input stream. It reads at most maximumBytes + 1, using
/// the final byte only to detect growth beyond the hard limit before any parser sees the data.
/// </summary>
internal sealed class ProductBoundedReadSnapshot : IDisposable
{
    private const int SegmentSize = 64 * 1024;
    private List<BufferLease>? _buffers;

    private ProductBoundedReadSnapshot(List<BufferLease> buffers, long length)
    {
        _buffers = buffers;
        Length = length;
        Bytes = CreateSequence(buffers);
    }

    public long Length { get; }

    public ReadOnlySequence<byte> Bytes { get; }

    public static async Task<ProductBoundedReadSnapshot> CaptureAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Snapshot source must be readable.", nameof(source));
        }
        if (maximumBytes < 1 || maximumBytes >= int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var buffers = new List<BufferLease>();
        long total = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingThroughProbe = checked(maximumBytes + 1 - total);
                var requested = checked((int)Math.Min(SegmentSize, remainingThroughProbe));
                var buffer = ArrayPool<byte>.Shared.Rent(requested);
                var used = 0;
                var reachedEnd = false;
                try
                {
                    while (used < requested)
                    {
                        var read = await source.ReadAsync(
                                buffer.AsMemory(used, requested - used),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            reachedEnd = true;
                            break;
                        }

                        used += read;
                        total += read;
                        if (total > maximumBytes)
                        {
                            throw new InvalidDataException(
                                "Managed player registry exceeded its bounded snapshot limit.");
                        }
                    }

                    if (used > 0)
                    {
                        buffers.Add(new BufferLease(buffer, used));
                        buffer = null!;
                    }
                }
                finally
                {
                    if (buffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    }
                }

                if (reachedEnd)
                {
                    return new ProductBoundedReadSnapshot(buffers, total);
                }
            }
        }
        catch
        {
            ReturnBuffers(buffers);
            throw;
        }
    }

    public void Dispose()
    {
        var buffers = Interlocked.Exchange(ref _buffers, null);
        if (buffers is not null)
        {
            ReturnBuffers(buffers);
        }
    }

    private static ReadOnlySequence<byte> CreateSequence(IReadOnlyList<BufferLease> buffers)
    {
        if (buffers.Count == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        var first = new SequenceSegment(buffers[0].Buffer.AsMemory(0, buffers[0].Length));
        var last = first;
        for (var index = 1; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            last = last.Append(buffer.Buffer.AsMemory(0, buffer.Length));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static void ReturnBuffers(IEnumerable<BufferLease> buffers)
    {
        foreach (var buffer in buffers)
        {
            ArrayPool<byte>.Shared.Return(buffer.Buffer, clearArray: true);
        }
    }

    private sealed record BufferLease(byte[] Buffer, int Length);

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SequenceSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = next;
            return next;
        }
    }
}
