using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MinecraftServerManager.Contracts.Notifications;

namespace MinecraftServerManager.Data;

public enum NotificationDispatchState
{
    Pending = 0,
    Delivered = 1,
    TerminalFailure = 2,
}

public enum NotificationRetryOutcome
{
    Scheduled,
    TerminalFailure,
    LeaseLost,
}

public sealed record LeasedNotification(
    Guid DispatchId,
    string ProviderId,
    int AttemptCount,
    ProductEventEnvelope Event);

public sealed record NotificationDeliveryRecord(
    Guid DispatchId,
    Guid EventId,
    string ProviderId,
    NotificationDispatchState State,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastFailureCode,
    DateTimeOffset? DeliveredAtUtc);

public sealed record NotificationPruneResult(int DispatchesDeleted, int EventsDeleted);

public sealed partial class NotificationOutboxStore
{
    private const int MaximumProviderCount = 16;
    private const int MaximumLeaseBatchSize = 100;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ProductDatabase _database;

    public NotificationOutboxStore(ProductDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<Guid>> EnqueueAsync(
        ProductEventEnvelope envelope,
        IReadOnlyCollection<string> providerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(providerIds);

        var validation = ProductEventEnvelopeValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(" ", validation.Errors), nameof(envelope));
        }

        var normalizedProviderIds = providerIds
            .Select(ValidateProviderId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedProviderIds.Length is < 1 or > MaximumProviderCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerIds),
                $"Provider count must be between 1 and {MaximumProviderCount}.");
        }

        var payloadJson = JsonSerializer.Serialize(envelope, SerializerOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var now = DateTimeOffset.UtcNow;
        var dispatchIds = normalizedProviderIds
            .Select(providerId => (ProviderId: providerId, DispatchId: Guid.NewGuid()))
            .ToArray();

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        try
        {
            await using (var eventCommand = connection.CreateCommand())
            {
                eventCommand.Transaction = transaction;
                eventCommand.CommandText =
                    """
                    INSERT INTO notification_events(
                        event_id, sequence, occurred_at_utc, event_type, severity, server_id,
                        payload_json, payload_sha256, created_at_utc)
                    VALUES (
                        $event_id, $sequence, $occurred_at_utc, $event_type, $severity, $server_id,
                        $payload_json, $payload_sha256, $created_at_utc)
                    ON CONFLICT(event_id) DO NOTHING;
                    """;
                eventCommand.Parameters.AddWithValue("$event_id", envelope.EventId.ToString("D"));
                eventCommand.Parameters.AddWithValue("$sequence", envelope.Sequence);
                eventCommand.Parameters.AddWithValue("$occurred_at_utc", FormatUtc(envelope.OccurredAtUtc));
                eventCommand.Parameters.AddWithValue("$event_type", envelope.Type);
                eventCommand.Parameters.AddWithValue("$severity", (int)envelope.Severity);
                eventCommand.Parameters.AddWithValue(
                    "$server_id",
                    envelope.ServerId is { } serverId ? serverId.ToString("D") : DBNull.Value);
                eventCommand.Parameters.AddWithValue("$payload_json", payloadJson);
                eventCommand.Parameters.AddWithValue("$payload_sha256", payloadHash);
                eventCommand.Parameters.AddWithValue("$created_at_utc", FormatUtc(now));
                await eventCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureEventIdentityMatchesAsync(
                connection,
                transaction,
                envelope.EventId,
                payloadHash,
                cancellationToken).ConfigureAwait(false);

            var created = new List<Guid>(dispatchIds.Length);
            foreach (var item in dispatchIds)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO notification_outbox(
                        dispatch_id, event_id, provider_id, next_attempt_at_utc, created_at_utc)
                    VALUES ($dispatch_id, $event_id, $provider_id, $next_attempt_at_utc, $created_at_utc)
                    ON CONFLICT(event_id, provider_id) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$dispatch_id", item.DispatchId.ToString("D"));
                command.Parameters.AddWithValue("$event_id", envelope.EventId.ToString("D"));
                command.Parameters.AddWithValue("$provider_id", item.ProviderId);
                command.Parameters.AddWithValue("$next_attempt_at_utc", FormatUtc(now));
                command.Parameters.AddWithValue("$created_at_utc", FormatUtc(now));
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    created.Add(item.DispatchId);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return created.AsReadOnly();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<LeasedNotification>> LeaseDueAsync(
        DateTimeOffset nowUtc,
        int maximumCount,
        TimeSpan leaseDuration,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (maximumCount is < 1 or > MaximumLeaseBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        if (leaseDuration < TimeSpan.FromSeconds(5) || leaseDuration > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        workerId = ValidateWorkerId(workerId);
        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        var leased = new List<LeasedNotification>(maximumCount);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notification_outbox
            SET lease_owner = $lease_owner,
                lease_expires_at_utc = $lease_expires_at_utc
            WHERE dispatch_id IN (
                SELECT dispatch_id
                FROM notification_outbox
                WHERE state = 0
                  AND next_attempt_at_utc <= $now_utc
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= $now_utc)
                ORDER BY next_attempt_at_utc, created_at_utc, dispatch_id
                LIMIT $maximum_count
            )
            RETURNING dispatch_id,
                      provider_id,
                      attempt_count,
                      (SELECT payload_json
                       FROM notification_events
                       WHERE notification_events.event_id = notification_outbox.event_id);
            """;
        command.Parameters.AddWithValue("$lease_owner", workerId);
        command.Parameters.AddWithValue("$lease_expires_at_utc", FormatUtc(leaseExpiresAtUtc));
        command.Parameters.AddWithValue("$now_utc", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$maximum_count", maximumCount);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var envelope = JsonSerializer.Deserialize<ProductEventEnvelope>(reader.GetString(3), SerializerOptions)
                ?? throw new InvalidDataException("Stored notification payload is invalid.");
            var validation = ProductEventEnvelopeValidator.Validate(envelope);
            if (!validation.IsValid)
            {
                throw new InvalidDataException("Stored notification payload failed contract validation.");
            }

            leased.Add(new LeasedNotification(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                envelope));
        }

        return leased.AsReadOnly();
    }

    public Task<bool> MarkDeliveredAsync(
        Guid dispatchId,
        string workerId,
        DateTimeOffset deliveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(dispatchId, nameof(dispatchId));
        EnsureUtc(deliveredAtUtc, nameof(deliveredAtUtc));
        return ExecuteLeaseCompletionAsync(
            dispatchId,
            ValidateWorkerId(workerId),
            NotificationDispatchState.Delivered,
            deliveredAtUtc,
            failureCode: null,
            cancellationToken);
    }

    public async Task<NotificationRetryOutcome> ScheduleRetryAsync(
        Guid dispatchId,
        string workerId,
        DateTimeOffset nextAttemptAtUtc,
        string failureCode,
        int maximumAttempts,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(dispatchId, nameof(dispatchId));
        EnsureUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        workerId = ValidateWorkerId(workerId);
        failureCode = ValidateFailureCode(failureCode);
        if (maximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notification_outbox
            SET attempt_count = attempt_count + 1,
                state = CASE WHEN attempt_count + 1 >= $maximum_attempts THEN 2 ELSE 0 END,
                next_attempt_at_utc = $next_attempt_at_utc,
                lease_owner = NULL,
                lease_expires_at_utc = NULL,
                last_failure_code = $failure_code
            WHERE dispatch_id = $dispatch_id
              AND state = 0
              AND lease_owner = $lease_owner
            RETURNING state;
            """;
        command.Parameters.AddWithValue("$maximum_attempts", maximumAttempts);
        command.Parameters.AddWithValue("$next_attempt_at_utc", FormatUtc(nextAttemptAtUtc));
        command.Parameters.AddWithValue("$failure_code", failureCode);
        command.Parameters.AddWithValue("$dispatch_id", dispatchId.ToString("D"));
        command.Parameters.AddWithValue("$lease_owner", workerId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            long state when state == (long)NotificationDispatchState.Pending => NotificationRetryOutcome.Scheduled,
            long state when state == (long)NotificationDispatchState.TerminalFailure => NotificationRetryOutcome.TerminalFailure,
            _ => NotificationRetryOutcome.LeaseLost,
        };
    }

    public async Task<IReadOnlyList<NotificationDeliveryRecord>> ReadRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var records = new List<NotificationDeliveryRecord>(maximumCount);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT dispatch_id, event_id, provider_id, state, attempt_count,
                   next_attempt_at_utc, last_failure_code, delivered_at_utc
            FROM notification_outbox
            ORDER BY created_at_utc DESC, dispatch_id DESC
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$maximum_count", maximumCount);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new NotificationDeliveryRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                (NotificationDispatchState)reader.GetInt32(3),
                reader.GetInt32(4),
                ParseUtc(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7))));
        }

        return records.AsReadOnly();
    }

    /// <summary>
    /// Bounds completed notification history without ever deleting pending work. Age and count
    /// are both enforced, then immutable event payloads are removed only when no dispatch still
    /// references them.
    /// </summary>
    public async Task<NotificationPruneResult> PruneCompletedAsync(
        DateTimeOffset completedBeforeUtc,
        int maximumCompletedRecords,
        CancellationToken cancellationToken = default)
    {
        EnsureUtc(completedBeforeUtc, nameof(completedBeforeUtc));
        if (maximumCompletedRecords is < 100 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCompletedRecords));
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        try
        {
            int deletedDispatches;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    DELETE FROM notification_outbox
                    WHERE state <> 0
                      AND (
                          created_at_utc < $completed_before_utc
                          OR dispatch_id IN (
                              SELECT dispatch_id
                              FROM notification_outbox
                              WHERE state <> 0
                              ORDER BY created_at_utc DESC, dispatch_id DESC
                              LIMIT -1 OFFSET $maximum_completed_records
                          )
                      );
                    """;
                command.Parameters.AddWithValue(
                    "$completed_before_utc",
                    FormatUtc(completedBeforeUtc));
                command.Parameters.AddWithValue(
                    "$maximum_completed_records",
                    maximumCompletedRecords);
                deletedDispatches = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            int deletedEvents;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    DELETE FROM notification_events
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM notification_outbox
                        WHERE notification_outbox.event_id = notification_events.event_id
                    );
                    """;
                deletedEvents = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new NotificationPruneResult(deletedDispatches, deletedEvents);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> ExecuteLeaseCompletionAsync(
        Guid dispatchId,
        string workerId,
        NotificationDispatchState state,
        DateTimeOffset deliveredAtUtc,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notification_outbox
            SET state = $state,
                delivered_at_utc = $delivered_at_utc,
                lease_owner = NULL,
                lease_expires_at_utc = NULL,
                last_failure_code = $failure_code
            WHERE dispatch_id = $dispatch_id
              AND state = 0
              AND lease_owner = $lease_owner;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$delivered_at_utc", FormatUtc(deliveredAtUtc));
        command.Parameters.AddWithValue("$failure_code", failureCode is null ? DBNull.Value : failureCode);
        command.Parameters.AddWithValue("$dispatch_id", dispatchId.ToString("D"));
        command.Parameters.AddWithValue("$lease_owner", workerId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task EnsureEventIdentityMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload_sha256 FROM notification_events WHERE event_id = $event_id;";
        command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
        var actualHash = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Event id is already bound to different immutable content.");
        }
    }

    private static string ValidateProviderId(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (!IdentifierPattern().IsMatch(providerId))
        {
            throw new ArgumentException("Provider id is invalid.", nameof(providerId));
        }

        return providerId;
    }

    private static string ValidateWorkerId(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 96 || !WorkerPattern().IsMatch(workerId))
        {
            throw new ArgumentException("Worker id is invalid.", nameof(workerId));
        }

        return workerId;
    }

    private static string ValidateFailureCode(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (!IdentifierPattern().IsMatch(failureCode))
        {
            throw new ArgumentException("Failure code is invalid.", nameof(failureCode));
        }

        return failureCode;
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC.", parameterName);
        }
    }

    private static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
        => DateTimeOffset.Parse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal);

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+){0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkerPattern();
}
