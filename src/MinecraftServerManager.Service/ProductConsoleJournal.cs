using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Service;

/// <summary>Bounded cursor journal used by REST and IPC cursor clients.</summary>
public sealed class ProductConsoleJournal
{
    // Fifty capped lines remain below the IPC protocol's 64 KiB response frame even at the
    // maximum retained text length and with full diagnostic metadata.
    public const int MaximumPageSize = ProductConsoleContract.MaximumPageSize;
    public const int MaximumTextCharacters = ProductConsoleContract.MaximumTextCharacters;
    private readonly object _sync = new();
    private readonly Queue<ProductConsoleEntry> _entries;
    private readonly int _capacity;
    private TaskCompletionSource? _changed;
    private long _nextCursor;

    public ProductConsoleJournal(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _entries = new Queue<ProductConsoleEntry>(capacity);
    }

    public void Add(Guid sessionId, ConsoleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Text ?? string.Empty;
        var truncated = text.Length > MaximumTextCharacters;
        if (truncated)
        {
            text = text[..MaximumTextCharacters];
            if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
            {
                text = text[..^1];
            }
        }

        TaskCompletionSource? changed;
        lock (_sync)
        {
            var entry = new ProductConsoleEntry(
                ++_nextCursor,
                sessionId,
                line.Timestamp,
                text,
                (ProductConsoleStream)line.Stream,
                (ProductConsoleSeverity)line.Severity,
                line.DiagnosticId,
                line.IsDiagnosticContinuation,
                truncated);
            if (_entries.Count == _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
            changed = _changed;
            _changed = null;
        }

        changed?.TrySetResult();
    }

    public ProductConsolePage Read(Guid serverId, long afterCursor, int limit)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(afterCursor);
        if (limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (_sync)
        {
            return ReadUnsafe(serverId, afterCursor, limit);
        }
    }

    public async Task<ProductConsolePage> WaitForChangeAsync(
        Guid serverId,
        long afterCursor,
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateReadArguments(serverId, afterCursor, limit);
        if (timeout < TimeSpan.FromMilliseconds(ProductConsoleContract.MinimumWaitTimeoutMilliseconds) ||
            timeout > TimeSpan.FromMilliseconds(ProductConsoleContract.MaximumWaitTimeoutMilliseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task changed;
        lock (_sync)
        {
            var page = ReadUnsafe(serverId, afterCursor, limit);
            if (page.HistoryGap || page.Entries.Count > 0)
            {
                return page;
            }

            // Capturing the generation while holding the journal lock closes the otherwise
            // unavoidable gap between checking the cursor and subscribing for its next advance.
            _changed ??= CreateChangeSignal();
            changed = _changed.Task;
        }

        try
        {
            await changed.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A timeout is a successful empty wait response, not an IPC error. Read again so a
            // line racing with the timer is still delivered whenever it reached the journal.
        }

        return Read(serverId, afterCursor, limit);
    }

    private ProductConsolePage ReadUnsafe(Guid serverId, long afterCursor, int limit)
    {
        var oldest = _entries.TryPeek(out var first) ? first.Cursor : _nextCursor + 1;
        var gap = afterCursor > _nextCursor ||
                  (_entries.Count > 0 && afterCursor < oldest - 1);
        var effectiveCursor = gap ? oldest - 1 : afterCursor;
        var entries = _entries
            .Where(entry => entry.Cursor > effectiveCursor)
            .Take(limit)
            .ToArray();
        var next = entries.Length > 0 ? entries[^1].Cursor : _nextCursor;
        return new ProductConsolePage(
            serverId,
            afterCursor,
            oldest,
            next,
            gap,
            entries);
    }

    private static void ValidateReadArguments(Guid serverId, long afterCursor, int limit)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(afterCursor);
        if (limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }

    private static TaskCompletionSource CreateChangeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
