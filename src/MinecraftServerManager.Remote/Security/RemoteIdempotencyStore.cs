using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Remote;

public enum RemoteIdempotencyOutcome
{
    Completed,
    Conflict,
    CapacityExceeded
}

public sealed record RemoteIdempotencyExecution(
    RemoteIdempotencyOutcome Outcome,
    RemoteOperationResultDto? Result);

/// <summary>
/// A bounded, in-memory, per-session idempotency ledger. It retains only hashes
/// of the session/key pair and canonical request, never the header key or body.
/// </summary>
public sealed class RemoteIdempotencyStore
{
    private const int SignatureBytes = 32;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<Task<RemoteOperationResultDto>> _inFlightCompletions = [];
    private readonly TimeSpan _lifetime;
    private readonly int _maximumEntries;
    private readonly TimeProvider _timeProvider;

    public RemoteIdempotencyStore(RemoteControlOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        RemoteControlOptionsValidator.ValidateAndThrow(options);
        _lifetime = options.IdempotencyLifetime;
        _maximumEntries = options.MaximumIdempotencyEntries;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RemoteIdempotencyExecution> ExecuteAsync(
        Guid sessionId,
        Guid idempotencyKey,
        ReadOnlyMemory<byte> requestSignature,
        Func<CancellationToken, ValueTask<RemoteOperationResultDto>> operation,
        CancellationToken operationCancellationToken,
        CancellationToken waitCancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty session identifier is required.", nameof(sessionId));
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("A non-empty idempotency key is required.", nameof(idempotencyKey));
        }

        if (requestSignature.Length != SignatureBytes)
        {
            throw new ArgumentException("Request signature must be a SHA-256 digest.", nameof(requestSignature));
        }

        ArgumentNullException.ThrowIfNull(operation);

        var lookupKey = CreateLookupKey(sessionId, idempotencyKey);
        Entry? entry;
        lock (_gate)
        {
            RemoveExpiredCompletedEntries(_timeProvider.GetUtcNow());
            if (_entries.TryGetValue(lookupKey, out entry))
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        entry.RequestSignature,
                        requestSignature.Span))
                {
                    return Task.FromResult(new RemoteIdempotencyExecution(
                        RemoteIdempotencyOutcome.Conflict,
                        null));
                }

                return WaitForCompletionAsync(entry.Completion.Task, waitCancellationToken);
            }

            if (_entries.Count >= _maximumEntries
                || _inFlightCompletions.Count >= _maximumEntries)
            {
                return Task.FromResult(new RemoteIdempotencyExecution(
                    RemoteIdempotencyOutcome.CapacityExceeded,
                    null));
            }

            entry = new Entry(requestSignature.ToArray());
            _entries.Add(lookupKey, entry);
            _inFlightCompletions.Add(entry.Completion.Task);
        }

        // Observe faults even if the originating HTTP waiter disconnects and no
        // later replay arrives. Nothing is logged here.
        _ = entry.Completion.Task.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        _ = CompleteOperationAsync(entry, operation, operationCancellationToken);
        return WaitForCompletionAsync(entry.Completion.Task, waitCancellationToken);
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                CryptographicOperations.ZeroMemory(entry.RequestSignature);
            }

            _entries.Clear();
        }
    }

    /// <summary>
    /// Waits for operations that were detached from their original HTTP request. Clearing the
    /// replay ledger never removes this independent in-flight tracking, so host shutdown can take
    /// a stable snapshot after Kestrel has stopped accepting new requests.
    /// </summary>
    public async Task<bool> DrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task all;
        lock (_gate)
        {
            if (_inFlightCompletions.Count == 0)
            {
                return true;
            }

            all = Task.WhenAll(_inFlightCompletions);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await all.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (all.IsCompleted)
        {
            _ = all.Exception;
            return true;
        }
        catch (Exception) when (all.IsCompleted)
        {
            _ = all.Exception;
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The continuations installed by ExecuteAsync still observe later faults. Shutdown
            // remains bounded even if a backend fails to honor ApplicationStopping promptly.
            return false;
        }
    }

    private async Task CompleteOperationAsync(
        Entry entry,
        Func<CancellationToken, ValueTask<RemoteOperationResultDto>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            MarkCompleted(entry);
            entry.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkCompleted(entry);
            entry.Completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            MarkCompleted(entry);
            entry.Completion.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
            {
                _inFlightCompletions.Remove(entry.Completion.Task);
            }
        }
    }

    private void MarkCompleted(Entry entry)
    {
        lock (_gate)
        {
            entry.ExpiresAtUtc = _timeProvider.GetUtcNow().Add(_lifetime);
        }
    }

    private static async Task<RemoteIdempotencyExecution> WaitForCompletionAsync(
        Task<RemoteOperationResultDto> completion,
        CancellationToken cancellationToken)
    {
        var result = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteIdempotencyExecution(RemoteIdempotencyOutcome.Completed, result);
    }

    private void RemoveExpiredCompletedEntries(DateTimeOffset now)
    {
        foreach (var pair in _entries
                     .Where(pair => pair.Value.ExpiresAtUtc is { } expiresAt && expiresAt <= now)
                     .ToArray())
        {
            if (_entries.Remove(pair.Key, out var removed))
            {
                CryptographicOperations.ZeroMemory(removed.RequestSignature);
            }
        }
    }

    private static string CreateLookupKey(Guid sessionId, Guid idempotencyKey)
    {
        Span<byte> input = stackalloc byte[32];
        sessionId.TryWriteBytes(input[..16]);
        idempotencyKey.TryWriteBytes(input[16..]);
        Span<byte> hash = stackalloc byte[SignatureBytes];
        SHA256.HashData(input, hash);
        CryptographicOperations.ZeroMemory(input);
        var lookupKey = Convert.ToHexString(hash);
        CryptographicOperations.ZeroMemory(hash);
        return lookupKey;
    }

    private sealed class Entry(byte[] requestSignature)
    {
        public byte[] RequestSignature { get; } = requestSignature;

        public TaskCompletionSource<RemoteOperationResultDto> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset? ExpiresAtUtc { get; set; }
    }
}

/// <summary>
/// Produces canonical SHA-256 request signatures without retaining raw commands,
/// reasons, routes, or other request payload after hashing.
/// </summary>
public static class RemoteMutationSignature
{
    public static byte[] CreateLifecycle(string serverId, string action)
        => Create(
            "POST",
            "/api/v1/servers/{serverId}/actions/{action}",
            serverId,
            action);

    public static byte[] CreateCommand(string serverId, string command)
        => Create(
            "POST",
            "/api/v1/servers/{serverId}/console/commands",
            serverId,
            command);

    public static byte[] CreatePlayerAction(string serverId, RemotePlayerActionRequestDto request)
        => Create(
            "POST",
            "/api/v1/servers/{serverId}/player-actions",
            serverId,
            ((int)request.Action).ToString(CultureInfo.InvariantCulture),
            request.PlayerName,
            request.Reason);

    public static byte[] CreateBackup(string serverId)
        => Create(
            "POST",
            "/api/v1/servers/{serverId}/backups",
            serverId);

    public static byte[] CreateBackupRestore(
        string serverId,
        string backupId,
        string confirmation)
        => Create(
            "POST",
            "/api/v1/servers/{serverId}/backups/{backupId}/restore",
            serverId,
            backupId.ToLowerInvariant(),
            confirmation);

    public static byte[] CreateProductUpdate(
        string action,
        string channel,
        DateTimeOffset? notBeforeUtc = null)
        => Create(
            "POST",
            "/api/v1/updates/{channel}/{action}",
            channel,
            action,
            notBeforeUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static byte[] Create(params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        foreach (var field in fields)
        {
            if (field is null)
            {
                BinaryPrimitives.WriteInt32BigEndian(lengthBytes, -1);
                hash.AppendData(lengthBytes);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(field);
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(lengthBytes, bytes.Length);
                hash.AppendData(lengthBytes);
                hash.AppendData(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return hash.GetHashAndReset();
    }
}
