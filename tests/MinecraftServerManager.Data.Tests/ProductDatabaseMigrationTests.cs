using Microsoft.Data.Sqlite;

namespace MinecraftServerManager.Data.Tests;

public sealed class ProductDatabaseMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mcsv-data-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeAsync_UpgradesSchemaTwoRememberedDevicesInPlace()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "product.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_versions (
                    component TEXT PRIMARY KEY NOT NULL,
                    version INTEGER NOT NULL,
                    applied_at_utc TEXT NOT NULL
                ) STRICT;
                INSERT INTO schema_versions VALUES ('product_data', 2, '2026-01-01T00:00:00Z');
                CREATE TABLE remote_remembered_devices (
                    device_id TEXT PRIMARY KEY NOT NULL,
                    username TEXT NOT NULL,
                    label TEXT NOT NULL,
                    generation INTEGER NOT NULL CHECK(generation >= 1),
                    secret_hash BLOB NOT NULL CHECK(length(secret_hash) = 32),
                    previous_generation INTEGER NULL CHECK(previous_generation IS NULL OR previous_generation >= 1),
                    previous_secret_hash BLOB NULL CHECK(previous_secret_hash IS NULL OR length(previous_secret_hash) = 32),
                    created_at_utc TEXT NOT NULL,
                    last_used_at_utc TEXT NOT NULL,
                    idle_expires_at_utc TEXT NOT NULL,
                    absolute_expires_at_utc TEXT NOT NULL,
                    revoked_at_utc TEXT NULL,
                    revocation_reason TEXT NULL
                ) STRICT;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var database = new ProductDatabase(path);
        await database.InitializeAsync();

        var backupPath = database.GetMigrationBackupPath(2);
        Assert.True(File.Exists(backupPath));
        await using (var backup = new SqliteConnection(
                         $"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
        {
            await backup.OpenAsync();
            await using var backupVersion = backup.CreateCommand();
            backupVersion.CommandText =
                "SELECT version FROM schema_versions WHERE component = 'product_data';";
            Assert.Equal(2L, (long)(await backupVersion.ExecuteScalarAsync())!);
        }

        await using var verified = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await verified.OpenAsync();
        await using (var version = verified.CreateCommand())
        {
            version.CommandText =
                "SELECT version FROM schema_versions WHERE component = 'product_data';";
            Assert.Equal(4L, (long)(await version.ExecuteScalarAsync())!);
        }

        await using (var columns = verified.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(remote_remembered_devices);";
            await using var reader = await columns.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(1));
            }

            Assert.Contains("last_refresh_request_id", names);
        }
    }

    [Fact]
    public async Task InitializeAsync_RejectsUnknownFutureSchemaWithoutDowngradingIt()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "future.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_versions (
                    component TEXT PRIMARY KEY NOT NULL,
                    version INTEGER NOT NULL,
                    applied_at_utc TEXT NOT NULL
                ) STRICT;
                INSERT INTO schema_versions VALUES ('product_data', 99, '2026-01-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ProductDatabase(path).InitializeAsync());

        await using var verified = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await verified.OpenAsync();
        await using var version = verified.CreateCommand();
        version.CommandText = "SELECT version FROM schema_versions WHERE component = 'product_data';";
        Assert.Equal(99L, (long)(await version.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SchemaThreeMigration_SelectsDeterministicOwnerAndCommitsRequiredGrantsAtomically()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "roles.db");
        var database = new ProductDatabase(path);
        await database.InitializeAsync();

        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO remote_accounts(
                    username, credential_subject, email, enabled, role, pin_salt, pin_verifier,
                    pin_secret_reference, security_stamp, failed_attempts, locked_until_utc,
                    created_at_utc, updated_at_utc)
                VALUES
                    ('later2', 'subject', NULL, 1, 4, zeroblob(32), zeroblob(32),
                     'pin-later', 'stamp-later', 0, NULL,
                     '2026-02-01T00:00:00.0000000+00:00', '2026-02-01T00:00:00.0000000+00:00'),
                    ('first1', 'subject', NULL, 0, 4, zeroblob(32), zeroblob(32),
                     'pin-first', 'stamp-first', 0, NULL,
                     '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                ALTER TABLE remote_accounts DROP COLUMN role;
                UPDATE schema_versions SET version = 3 WHERE component = 'product_data';
                """;
            await command.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        var backupPath = database.GetMigrationBackupPath(3);
        Assert.True(File.Exists(backupPath));
        await using (var backup = new SqliteConnection(
                         $"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
        {
            await backup.OpenAsync();
            await using var roleColumn = backup.CreateCommand();
            roleColumn.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('remote_accounts') WHERE name = 'role';";
            Assert.Equal(0L, (long)(await roleColumn.ExecuteScalarAsync())!);
        }

        await using var verified = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        await verified.OpenAsync();
        await using (var roles = verified.CreateCommand())
        {
            roles.CommandText = "SELECT username, role FROM remote_accounts ORDER BY username;";
            await using var reader = await roles.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("first1", reader.GetString(0));
            Assert.Equal(4L, reader.GetInt64(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("later2", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }

        await using (var grants = verified.CreateCommand())
        {
            grants.CommandText =
                """
                SELECT permission_code
                FROM remote_account_grants
                WHERE username = 'later2' AND scope_kind = 0 AND scope_value = '*'
                ORDER BY permission_code;
                """;
            await using var reader = await grants.ExecuteReaderAsync();
            var codes = new List<string>();
            while (await reader.ReadAsync()) codes.Add(reader.GetString(0));
            Assert.Equal(["permission.manage", "user.manage", "user.read"], codes);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
