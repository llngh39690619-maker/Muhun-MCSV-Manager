using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MinecraftServerManager.Data;

public sealed record ProductSecurityAuditEntry(
    Guid AuditId,
    DateTimeOffset OccurredAtUtc,
    string ActionCode,
    string OutcomeCode,
    string? Username,
    string? PermissionCode,
    Guid? ServerId,
    string ReasonCode,
    Guid? CorrelationId = null);

public sealed partial class ProductSecurityAuditStore
{
    private readonly ProductDatabase _database;

    public ProductSecurityAuditStore(ProductDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Writes a bounded security decision before a mutation is accepted. False means the
    /// caller must fail closed; raw request bodies and secrets have no field in this model.
    /// </summary>
    public bool TryAppend(ProductSecurityAuditEntry entry)
    {
        if (!IsValid(entry))
        {
            return false;
        }

        try
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO security_audit(
                    audit_id, occurred_at_utc, action_code, outcome_code, username,
                    permission_code, server_id, reason_code, correlation_id)
                VALUES (
                    $audit_id, $occurred_at_utc, $action_code, $outcome_code, $username,
                    $permission_code, $server_id, $reason_code, $correlation_id);
                """;
            command.Parameters.AddWithValue("$audit_id", entry.AuditId.ToString("D"));
            command.Parameters.AddWithValue("$occurred_at_utc", FormatUtc(entry.OccurredAtUtc));
            command.Parameters.AddWithValue("$action_code", entry.ActionCode);
            command.Parameters.AddWithValue("$outcome_code", entry.OutcomeCode);
            command.Parameters.AddWithValue("$username", (object?)entry.Username ?? DBNull.Value);
            command.Parameters.AddWithValue("$permission_code", (object?)entry.PermissionCode ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$server_id",
                entry.ServerId is { } serverId ? serverId.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$reason_code", entry.ReasonCode);
            command.Parameters.AddWithValue(
                "$correlation_id",
                entry.CorrelationId is { } correlationId ? correlationId.ToString("D") : DBNull.Value);
            return command.ExecuteNonQuery() == 1;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ProductSecurityAuditEntry>> ReadRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var entries = new List<ProductSecurityAuditEntry>(maximumCount);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT audit_id, occurred_at_utc, action_code, outcome_code, username,
                   permission_code, server_id, reason_code, correlation_id
            FROM security_audit
            ORDER BY occurred_at_utc DESC, audit_id DESC
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$maximum_count", maximumCount);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ProductSecurityAuditEntry(
                Guid.Parse(reader.GetString(0)),
                ParseUtc(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8))));
        }

        return entries.AsReadOnly();
    }

    /// <summary>Applies a durable age/count ceiling while retaining the newest decisions.</summary>
    public async Task<int> PruneAsync(
        DateTimeOffset occurredBeforeUtc,
        int maximumRecords,
        CancellationToken cancellationToken = default)
    {
        if (occurredBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC.", nameof(occurredBeforeUtc));
        }

        if (maximumRecords is < 1_000 or > 5_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM security_audit
            WHERE occurred_at_utc < $occurred_before_utc
               OR audit_id IN (
                   SELECT audit_id
                   FROM security_audit
                   ORDER BY occurred_at_utc DESC, audit_id DESC
                   LIMIT -1 OFFSET $maximum_records
               );
            """;
        command.Parameters.AddWithValue("$occurred_before_utc", FormatUtc(occurredBeforeUtc));
        command.Parameters.AddWithValue("$maximum_records", maximumRecords);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool IsValid(ProductSecurityAuditEntry? entry)
        => entry is not null &&
           entry.AuditId != Guid.Empty &&
           entry.OccurredAtUtc.Offset == TimeSpan.Zero &&
           IsCode(entry.ActionCode) &&
           IsCode(entry.OutcomeCode) &&
           IsOptionalIdentifier(entry.Username, 64) &&
           IsOptionalCode(entry.PermissionCode) &&
           entry.ServerId != Guid.Empty &&
           IsCode(entry.ReasonCode) &&
           entry.CorrelationId != Guid.Empty;

    private static bool IsCode(string? value)
        => value is not null && value.Length <= 64 && CodePattern().IsMatch(value);

    private static bool IsOptionalCode(string? value)
        => value is null || IsCode(value);

    private static bool IsOptionalIdentifier(string? value, int maximumLength)
        => value is null ||
           (value.Length is > 0 &&
            value.Length <= maximumLength &&
            value == value.Trim() &&
            !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)));

    private static string FormatUtc(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+){0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
