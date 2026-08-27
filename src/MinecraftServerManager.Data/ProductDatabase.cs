using Microsoft.Data.Sqlite;

namespace MinecraftServerManager.Data;

public sealed class ProductDatabase
{
    private const int BusyTimeoutMilliseconds = 5_000;
    private const int CurrentProductDataSchemaVersion = 4;
    private readonly string _connectionString;

    public ProductDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Database path must include a directory.", nameof(databasePath));

        DatabasePath = fullPath;
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS schema_versions (
                component TEXT PRIMARY KEY NOT NULL,
                version INTEGER NOT NULL,
                applied_at_utc TEXT NOT NULL
            ) STRICT;
            """,
            cancellationToken).ConfigureAwait(false);

        var existingVersion = await ReadSchemaVersionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (existingVersion > CurrentProductDataSchemaVersion)
        {
            throw new InvalidDataException(
                $"Product database schema {existingVersion} is newer than this Service supports.");
        }

        if (existingVersion is > 0 and < CurrentProductDataSchemaVersion)
        {
            await EnsureMigrationBackupAsync(connection, existingVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                CREATE TABLE IF NOT EXISTS notification_events (
                    event_id TEXT PRIMARY KEY NOT NULL,
                    sequence INTEGER NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    severity INTEGER NOT NULL,
                    server_id TEXT NULL,
                    payload_json TEXT NOT NULL,
                    payload_sha256 TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                ) STRICT;

                CREATE INDEX IF NOT EXISTS ix_notification_events_sequence
                    ON notification_events(sequence DESC);

                CREATE TABLE IF NOT EXISTS notification_outbox (
                    dispatch_id TEXT PRIMARY KEY NOT NULL,
                    event_id TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    state INTEGER NOT NULL DEFAULT 0 CHECK(state BETWEEN 0 AND 2),
                    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                    next_attempt_at_utc TEXT NOT NULL,
                    lease_owner TEXT NULL,
                    lease_expires_at_utc TEXT NULL,
                    last_failure_code TEXT NULL,
                    delivered_at_utc TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    FOREIGN KEY(event_id) REFERENCES notification_events(event_id) ON DELETE CASCADE,
                    UNIQUE(event_id, provider_id)
                ) STRICT;

                CREATE INDEX IF NOT EXISTS ix_notification_outbox_due
                    ON notification_outbox(state, next_attempt_at_utc, lease_expires_at_utc);

                CREATE TABLE IF NOT EXISTS security_audit (
                    audit_id TEXT PRIMARY KEY NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    action_code TEXT NOT NULL,
                    outcome_code TEXT NOT NULL,
                    username TEXT NULL,
                    permission_code TEXT NULL,
                    server_id TEXT NULL,
                    reason_code TEXT NOT NULL,
                    correlation_id TEXT NULL
                ) STRICT;

                CREATE INDEX IF NOT EXISTS ix_security_audit_occurred
                    ON security_audit(occurred_at_utc DESC, audit_id DESC);

                CREATE INDEX IF NOT EXISTS ix_security_audit_username
                    ON security_audit(username, occurred_at_utc DESC);

                CREATE TABLE IF NOT EXISTS remote_accounts (
                    username TEXT PRIMARY KEY NOT NULL,
                    credential_subject TEXT NOT NULL,
                    email TEXT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0, 1)),
                    role INTEGER NOT NULL DEFAULT 4 CHECK(role BETWEEN 1 AND 4),
                    pin_salt BLOB NOT NULL CHECK(length(pin_salt) = 32),
                    pin_verifier BLOB NOT NULL CHECK(length(pin_verifier) = 32),
                    pin_secret_reference TEXT NOT NULL UNIQUE,
                    security_stamp TEXT NOT NULL,
                    failed_attempts INTEGER NOT NULL DEFAULT 0 CHECK(failed_attempts >= 0),
                    locked_until_utc TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                ) STRICT;

                CREATE INDEX IF NOT EXISTS ix_remote_accounts_subject
                    ON remote_accounts(credential_subject, enabled, username);

                CREATE TABLE IF NOT EXISTS remote_account_grants (
                    username TEXT NOT NULL,
                    permission_code TEXT NOT NULL,
                    scope_kind INTEGER NOT NULL CHECK(scope_kind IN (0, 1)),
                    scope_value TEXT NOT NULL,
                    PRIMARY KEY(username, permission_code, scope_kind, scope_value),
                    FOREIGN KEY(username) REFERENCES remote_accounts(username) ON DELETE CASCADE
                ) STRICT;

                CREATE INDEX IF NOT EXISTS ix_remote_account_grants_username
                    ON remote_account_grants(username, permission_code);

                CREATE TABLE IF NOT EXISTS remote_remembered_devices (
                    device_id TEXT PRIMARY KEY NOT NULL,
                    username TEXT NOT NULL,
                    label TEXT NOT NULL,
                    generation INTEGER NOT NULL CHECK(generation >= 1),
                    secret_hash BLOB NOT NULL CHECK(length(secret_hash) = 32),
                    previous_generation INTEGER NULL CHECK(previous_generation IS NULL OR previous_generation >= 1),
                    previous_secret_hash BLOB NULL CHECK(previous_secret_hash IS NULL OR length(previous_secret_hash) = 32),
                    last_refresh_request_id TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    last_used_at_utc TEXT NOT NULL,
                    idle_expires_at_utc TEXT NOT NULL,
                    absolute_expires_at_utc TEXT NOT NULL,
                    revoked_at_utc TEXT NULL,
                    revocation_reason TEXT NULL,
                    FOREIGN KEY(username) REFERENCES remote_accounts(username) ON DELETE CASCADE
                ) STRICT;

                CREATE INDEX IF NOT EXISTS ix_remote_remembered_devices_username
                    ON remote_remembered_devices(username, revoked_at_utc, last_used_at_utc DESC);

                CREATE TABLE IF NOT EXISTS product_sequences (
                    sequence_name TEXT PRIMARY KEY NOT NULL,
                    next_value INTEGER NOT NULL CHECK(next_value >= 1)
                ) STRICT;

                """,
                cancellationToken,
                transaction).ConfigureAwait(false);

            // Schema 2 already contained the remembered-device table but did not persist the
            // refresh request id used for idempotent token rotation. CREATE TABLE IF NOT EXISTS
            // cannot add that column, so perform the additive migration inside the same durable
            // transaction that advances schema_versions. Introspection also repairs prerelease
            // databases whose version row was lost while their tables remained intact.
            if (!await ColumnExistsAsync(
                    connection,
                    transaction,
                    "remote_remembered_devices",
                    "last_refresh_request_id",
                    cancellationToken).ConfigureAwait(false))
            {
                await ExecuteNonQueryAsync(
                        connection,
                        "ALTER TABLE remote_remembered_devices ADD COLUMN last_refresh_request_id TEXT NULL;",
                        cancellationToken,
                        transaction)
                    .ConfigureAwait(false);
            }

            // Schema 4 introduces durable governance roles. Existing installations did not
            // have an owner, so choose one deterministically and grant only the three global
            // permissions required to retain account/permission administration. The column,
            // owner selection, grants, and schema advance share this FULL-synchronous
            // transaction; a crash can therefore expose either the complete old schema or the
            // complete new invariant, never a half-migrated account catalog.
            if (!await ColumnExistsAsync(
                    connection,
                    transaction,
                    "remote_accounts",
                    "role",
                    cancellationToken).ConfigureAwait(false))
            {
                await ExecuteNonQueryAsync(
                        connection,
                        "ALTER TABLE remote_accounts ADD COLUMN role INTEGER NOT NULL DEFAULT 4 CHECK(role BETWEEN 1 AND 4);",
                        cancellationToken,
                        transaction)
                    .ConfigureAwait(false);

            }

            if (existingVersion < 4)
            {
                await ExecuteNonQueryAsync(
                        connection,
                        """
                        UPDATE remote_accounts
                        SET role = 1
                        WHERE NOT EXISTS (
                            SELECT 1 FROM remote_accounts WHERE role = 1
                        ) AND username = (
                            SELECT username
                            FROM remote_accounts
                            ORDER BY enabled DESC, created_at_utc, username
                            LIMIT 1
                        );

                        INSERT OR IGNORE INTO remote_account_grants(
                            username, permission_code, scope_kind, scope_value)
                        SELECT username, required.permission_code, 0, '*'
                        FROM remote_accounts
                        CROSS JOIN (
                            SELECT 'user.read' AS permission_code
                            UNION ALL SELECT 'user.manage'
                            UNION ALL SELECT 'permission.manage'
                        ) AS required
                        WHERE role = 1;
                        """,
                        cancellationToken,
                        transaction)
                    .ConfigureAwait(false);
            }

            await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO schema_versions(component, version, applied_at_utc)
                    VALUES ('product_data', 4, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    ON CONFLICT(component) DO UPDATE SET
                        version = MAX(schema_versions.version, excluded.version),
                        applied_at_utc = CASE
                            WHEN schema_versions.version < excluded.version THEN excluded.applied_at_utc
                            ELSE schema_versions.applied_at_utc
                        END;
                    """,
                    cancellationToken,
                    transaction)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal string GetMigrationBackupPath(int sourceVersion)
        => DatabasePath + $".schema-v{sourceVersion}.pre-v{CurrentProductDataSchemaVersion}.backup";

    private async Task EnsureMigrationBackupAsync(
        SqliteConnection source,
        int sourceVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destinationPath = GetMigrationBackupPath(sourceVersion);
        if (File.Exists(destinationPath))
        {
            var attributes = File.GetAttributes(destinationPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("Database migration backup cannot be a reparse point.");
            }

            await ValidateMigrationBackupAsync(destinationPath, sourceVersion, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var temporaryPath = destinationPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            await using (var destination = new SqliteConnection(connectionString))
            {
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            await ValidateMigrationBackupAsync(temporaryPath, sourceVersion, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: false);
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

    private static async Task ValidateMigrationBackupAsync(
        string path,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Database migration backup failed its integrity check.");
            }
        }

        var version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version != expectedVersion)
        {
            throw new InvalidDataException("Database migration backup schema does not match the source.");
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT version FROM schema_versions WHERE component = 'product_data' LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA foreign_keys = ON;",
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
