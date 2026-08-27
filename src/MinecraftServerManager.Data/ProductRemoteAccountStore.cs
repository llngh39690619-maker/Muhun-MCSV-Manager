using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using Microsoft.Data.Sqlite;

namespace MinecraftServerManager.Data;

public enum ProductRemoteAuthenticationStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
}

public sealed record ProductRemoteAccountInfo(
    string Username,
    string CredentialSubject,
    string? Email,
    bool Enabled,
    string SecurityStamp,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LockedUntilUtc,
    IReadOnlyList<ProductPermissionGrant> Grants,
    ProductRemoteAccountRole Role);

public sealed record ProductRemoteAuthenticationResult(
    ProductRemoteAuthenticationStatus Status,
    ProductRemoteAccountInfo? Account = null,
    DateTimeOffset? LockedUntilUtc = null);

/// <summary>
/// Durable remote-account authority. PIN verification data stays in SQLite while the optional
/// recoverable PIN requested by the desktop UI is stored under DPAPI in the product secret vault.
/// Neither value is returned by the remote Web API.
/// </summary>
public sealed class ProductRemoteAccountStore
{
    public const int MaximumAccounts = 32;
    public const int MaximumGrantsPerAccount = 256;
    public const int DefaultKdfIterations = 600_000;
    public const int MaximumFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private const int SaltBytes = 32;
    private const int VerifierBytes = 32;
    private static readonly string[] OwnerManagementPermissionCodes =
    [
        ProductPermissionCodes.UserRead,
        ProductPermissionCodes.UserManage,
        ProductPermissionCodes.PermissionManage,
    ];
    private readonly ProductDatabase _database;
    private readonly IProductSecretVault _secretVault;
    private readonly TimeProvider _timeProvider;
    private readonly int _kdfIterations;
    private readonly byte[] _dummySalt = RandomNumberGenerator.GetBytes(SaltBytes);
    private readonly byte[] _dummyVerifier = RandomNumberGenerator.GetBytes(VerifierBytes);

    public ProductRemoteAccountStore(
        ProductDatabase database,
        IProductSecretVault secretVault,
        TimeProvider? timeProvider = null)
        : this(database, secretVault, timeProvider, DefaultKdfIterations)
    {
    }

    internal ProductRemoteAccountStore(
        ProductDatabase database,
        IProductSecretVault secretVault,
        TimeProvider? timeProvider,
        int kdfIterations)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (kdfIterations is < 10_000 or > 2_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(kdfIterations));
        }

        _kdfIterations = kdfIterations;
    }

    public async Task<ProductRemoteAccountInfo> CreateAsync(
        string username,
        string credentialSubject,
        string? email,
        string pin,
        IReadOnlyCollection<ProductPermissionGrant> grants,
        CancellationToken cancellationToken = default,
        ProductRemoteAccountRole? role = null)
    {
        username = NormalizeUsername(username);
        credentialSubject = NormalizeSubject(credentialSubject);
        email = NormalizeEmail(email);
        ValidatePin(pin);
        var immutableGrants = ValidateGrants(grants);
        var requestedRole = ValidateOptionalRole(role);
        var now = _timeProvider.GetUtcNow();
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var verifier = DeriveVerifier(pin, salt);
        var secretReference = $"remote.account.pin.{Guid.NewGuid():N}";
        var stamp = CreateSecurityStamp();
        var secretSaved = false;
        var committed = false;
        try
        {
            await _secretVault.SetSecretAsync(secretReference, pin, cancellationToken)
                .ConfigureAwait(false);
            secretSaved = true;

            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                int existing;
                await using (var count = connection.CreateCommand())
                {
                    count.Transaction = transaction;
                    count.CommandText = "SELECT COUNT(*) FROM remote_accounts;";
                    existing = Convert.ToInt32(
                        await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture);
                    if (existing >= MaximumAccounts)
                    {
                        throw new InvalidOperationException("The remote account limit has been reached.");
                    }
                }

                // The first locally-created account is always the recovery owner, regardless
                // of an older client's omitted/default role. Later accounts default to Viewer.
                var enabledOwnerCount = await CountEnabledOwnersAsync(
                        connection,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                var persistedRole = existing == 0 || enabledOwnerCount == 0
                    ? ProductRemoteAccountRole.Owner
                    : requestedRole ?? ProductRemoteAccountRole.Viewer;
                if (persistedRole == ProductRemoteAccountRole.Owner)
                {
                    immutableGrants = AddRequiredOwnerManagementGrants(immutableGrants);
                }

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO remote_accounts(
                            username, credential_subject, email, enabled, role, pin_salt, pin_verifier,
                            pin_secret_reference, security_stamp, failed_attempts, locked_until_utc,
                            created_at_utc, updated_at_utc)
                        VALUES(
                            $username, $subject, $email, 1, $role, $salt, $verifier, $secret_reference,
                            $security_stamp, 0, NULL, $created, $updated);
                        """;
                    insert.Parameters.AddWithValue("$username", username);
                    insert.Parameters.AddWithValue("$subject", credentialSubject);
                    insert.Parameters.AddWithValue("$email", (object?)email ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$role", (int)persistedRole);
                    insert.Parameters.Add("$salt", SqliteType.Blob).Value = salt;
                    insert.Parameters.Add("$verifier", SqliteType.Blob).Value = verifier;
                    insert.Parameters.AddWithValue("$secret_reference", secretReference);
                    insert.Parameters.AddWithValue("$security_stamp", stamp);
                    insert.Parameters.AddWithValue("$created", FormatUtc(now));
                    insert.Parameters.AddWithValue("$updated", FormatUtc(now));
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await ReplaceGrantsAsync(
                        connection,
                        transaction,
                        username,
                        immutableGrants,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            return GetRequired(username);
        }
        catch
        {
            if (secretSaved && !committed)
            {
                try
                {
                    await _secretVault.DeleteSecretAsync(secretReference, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                {
                    // A random, unreferenced vault entry cannot authenticate. Preserve the
                    // original database failure and let maintenance remove orphaned secrets.
                }
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(verifier);
        }
    }

    public IReadOnlyList<ProductRemoteAccountInfo> List()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT username, credential_subject, email, enabled, security_stamp,
                   created_at_utc, updated_at_utc, locked_until_utc, role
            FROM remote_accounts
            ORDER BY username;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<AccountRow>();
        while (reader.Read())
        {
            rows.Add(ReadAccountRow(reader));
        }

        reader.Close();
        var accounts = rows.Select(row => ToAccount(row, connection)).ToArray();
        return accounts.AsReadOnly();
    }

    public bool TryGet(string username, out ProductRemoteAccountInfo account)
    {
        if (!TryNormalizeUsername(username, out var normalized))
        {
            account = null!;
            return false;
        }

        using var connection = _database.OpenConnection();
        account = ReadAccount(connection, normalized)!;
        return account is not null;
    }

    public bool HasEnabledAccountForSubject(string credentialSubject)
    {
        credentialSubject = NormalizeSubject(credentialSubject);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM remote_accounts WHERE credential_subject = $subject AND enabled = 1);";
        command.Parameters.AddWithValue("$subject", credentialSubject);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public ProductRemoteAuthenticationResult Authenticate(
        string credentialSubject,
        string username,
        string pin)
    {
        var validUsername = TryNormalizeUsername(username, out var normalizedUsername);
        var validSubject = TryNormalizeSubject(credentialSubject, out var normalizedSubject);
        var validPin = IsValidPin(pin);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var row = validUsername ? ReadAuthenticationRow(connection, transaction, normalizedUsername) : null;
        var now = _timeProvider.GetUtcNow();
        var verifier = DeriveVerifier(validPin ? pin : "000000000000", row?.Salt ?? _dummySalt);
        try
        {
            var hashMatches = CryptographicOperations.FixedTimeEquals(
                verifier,
                row?.Verifier ?? _dummyVerifier);
            if (row is null || !validSubject || !validPin || !row.Enabled ||
                !string.Equals(row.CredentialSubject, normalizedSubject, StringComparison.Ordinal) ||
                !hashMatches)
            {
                if (row is not null)
                {
                    var lockedUntil = RegisterFailedAttempt(connection, transaction, row, now);
                    transaction.Commit();
                    return lockedUntil is not null
                        ? new ProductRemoteAuthenticationResult(
                            ProductRemoteAuthenticationStatus.LockedOut,
                            LockedUntilUtc: lockedUntil)
                        : new ProductRemoteAuthenticationResult(
                            ProductRemoteAuthenticationStatus.InvalidCredentials);
                }

                transaction.Commit();
                return new ProductRemoteAuthenticationResult(
                    ProductRemoteAuthenticationStatus.InvalidCredentials);
            }

            if (row.LockedUntilUtc is { } locked && locked > now)
            {
                transaction.Commit();
                return new ProductRemoteAuthenticationResult(
                    ProductRemoteAuthenticationStatus.LockedOut,
                    LockedUntilUtc: locked);
            }

            using (var reset = connection.CreateCommand())
            {
                reset.Transaction = transaction;
                reset.CommandText =
                    "UPDATE remote_accounts SET failed_attempts = 0, locked_until_utc = NULL WHERE username = $username;";
                reset.Parameters.AddWithValue("$username", normalizedUsername);
                reset.ExecuteNonQuery();
            }

            transaction.Commit();
            return new ProductRemoteAuthenticationResult(
                ProductRemoteAuthenticationStatus.Success,
                GetRequired(normalizedUsername));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifier);
            if (row is not null)
            {
                CryptographicOperations.ZeroMemory(row.Salt);
                CryptographicOperations.ZeroMemory(row.Verifier);
            }
        }
    }

    public async Task<ProductRemoteAccountInfo> UpdatePinAsync(
        string username,
        string newPin,
        CancellationToken cancellationToken = default)
    {
        username = NormalizeUsername(username);
        ValidatePin(newPin);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var verifier = DeriveVerifier(newPin, salt);
        var newReference = $"remote.account.pin.{Guid.NewGuid():N}";
        string? oldReference = null;
        var newSaved = false;
        var committed = false;
        try
        {
            await _secretVault.SetSecretAsync(newReference, newPin, cancellationToken)
                .ConfigureAwait(false);
            newSaved = true;
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            try
            {
                await using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT pin_secret_reference FROM remote_accounts WHERE username = $username;";
                    select.Parameters.AddWithValue("$username", username);
                    oldReference = (string?)await select.ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new KeyNotFoundException("The remote account was not found.");
                }

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        UPDATE remote_accounts
                        SET pin_salt = $salt, pin_verifier = $verifier,
                            pin_secret_reference = $secret_reference,
                            security_stamp = $security_stamp,
                            failed_attempts = 0, locked_until_utc = NULL,
                            updated_at_utc = $updated
                        WHERE username = $username;
                        """;
                    update.Parameters.Add("$salt", SqliteType.Blob).Value = salt;
                    update.Parameters.Add("$verifier", SqliteType.Blob).Value = verifier;
                    update.Parameters.AddWithValue("$secret_reference", newReference);
                    update.Parameters.AddWithValue("$security_stamp", CreateSecurityStamp());
                    update.Parameters.AddWithValue("$updated", FormatUtc(_timeProvider.GetUtcNow()));
                    update.Parameters.AddWithValue("$username", username);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await RevokeDevicesAsync(connection, transaction, username, "credential_changed", cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (oldReference is not null)
            {
                try
                {
                    await _secretVault.DeleteSecretAsync(oldReference, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                {
                    // The committed account points only at the new secret. An undeleted old
                    // entry is an inert maintenance orphan and must not roll back the new PIN.
                }
            }

            return GetRequired(username);
        }
        catch
        {
            if (newSaved && !committed)
            {
                try
                {
                    await _secretVault.DeleteSecretAsync(newReference, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                {
                }
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(verifier);
        }
    }

    public async Task<ProductRemoteAccountInfo> UpdateAuthorizationAsync(
        string username,
        bool enabled,
        IReadOnlyCollection<ProductPermissionGrant> grants,
        CancellationToken cancellationToken = default,
        ProductRemoteAccountRole? role = null)
    {
        username = NormalizeUsername(username);
        var immutableGrants = ValidateGrants(grants);
        var requestedRole = ValidateOptionalRole(role);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            ProductRemoteAccountRole currentRole;
            bool currentlyEnabled;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    "SELECT enabled, role FROM remote_accounts WHERE username = $username;";
                select.Parameters.AddWithValue("$username", username);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new KeyNotFoundException("The remote account was not found.");
                }

                currentlyEnabled = reader.GetInt64(0) == 1;
                currentRole = ReadRole(reader.GetInt32(1));
            }

            var persistedRole = requestedRole ?? currentRole;
            if (persistedRole == ProductRemoteAccountRole.Owner &&
                !HasRequiredOwnerManagementGrants(immutableGrants))
            {
                throw new InvalidOperationException(
                    "An owner must retain global user.read, user.manage, and permission.manage grants.");
            }

            if (currentlyEnabled && currentRole == ProductRemoteAccountRole.Owner &&
                (!enabled || persistedRole != ProductRemoteAccountRole.Owner) &&
                await CountEnabledOwnersAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false) <= 1)
            {
                throw new InvalidOperationException(
                    "The last enabled owner cannot be disabled or assigned another role.");
            }

            if (enabled && persistedRole != ProductRemoteAccountRole.Owner &&
                await CountEnabledOwnersAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException(
                    "A non-owner cannot be enabled while no enabled owner exists.");
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE remote_accounts
                    SET enabled = $enabled, role = $role, security_stamp = $security_stamp,
                        updated_at_utc = $updated
                    WHERE username = $username;
                    """;
                update.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                update.Parameters.AddWithValue("$role", (int)persistedRole);
                update.Parameters.AddWithValue("$security_stamp", CreateSecurityStamp());
                update.Parameters.AddWithValue("$updated", FormatUtc(_timeProvider.GetUtcNow()));
                update.Parameters.AddWithValue("$username", username);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new KeyNotFoundException("The remote account was not found.");
                }
            }

            await ReplaceGrantsAsync(
                    connection,
                    transaction,
                    username,
                    immutableGrants,
                    cancellationToken)
                .ConfigureAwait(false);
            await RevokeDevicesAsync(connection, transaction, username, "authorization_changed", cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return GetRequired(username);
    }

    public async Task DeleteAsync(string username, CancellationToken cancellationToken = default)
    {
        username = NormalizeUsername(username);
        string secretReference;
        await using (var connection = await _database.OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            try
            {
                await using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText =
                        "SELECT pin_secret_reference, enabled, role FROM remote_accounts WHERE username = $username;";
                    select.Parameters.AddWithValue("$username", username);
                    await using var reader = await select.ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new KeyNotFoundException("The remote account was not found.");
                    }

                    secretReference = reader.GetString(0);
                    var isEnabledOwner = reader.GetInt64(1) == 1 &&
                                         ReadRole(reader.GetInt32(2)) == ProductRemoteAccountRole.Owner;
                    await reader.DisposeAsync().ConfigureAwait(false);
                    if (isEnabledOwner &&
                        await CountEnabledOwnersAsync(connection, transaction, cancellationToken)
                            .ConfigureAwait(false) <= 1)
                    {
                        throw new InvalidOperationException(
                            "The last enabled owner cannot be deleted.");
                    }
                }

                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM remote_accounts WHERE username = $username;";
                    delete.Parameters.AddWithValue("$username", username);
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        await _secretVault.DeleteSecretAsync(secretReference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RevealPinAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        username = NormalizeUsername(username);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pin_secret_reference FROM remote_accounts WHERE username = $username;";
        command.Parameters.AddWithValue("$username", username);
        var secretReference = command.ExecuteScalar() as string
            ?? throw new KeyNotFoundException("The remote account was not found.");
        return await _secretVault.GetSecretAsync(secretReference, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("The recoverable remote PIN is unavailable.");
    }

    private ProductRemoteAccountInfo GetRequired(string username)
        => TryGet(username, out var account)
            ? account
            : throw new KeyNotFoundException("The remote account was not found.");

    private static ProductRemoteAccountInfo? ReadAccount(SqliteConnection connection, string username)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT username, credential_subject, email, enabled, security_stamp,
                   created_at_utc, updated_at_utc, locked_until_utc, role
            FROM remote_accounts
            WHERE username = $username;
            """;
        command.Parameters.AddWithValue("$username", username);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var row = ReadAccountRow(reader);
        reader.Close();
        return ToAccount(row, connection);
    }

    private static AccountRow ReadAccountRow(SqliteDataReader reader)
    {
        return new AccountRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3) == 1,
            reader.GetString(4),
            ParseUtc(reader.GetString(5)),
            ParseUtc(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
            ReadRole(reader.GetInt32(8)));
    }

    private static ProductRemoteAccountInfo ToAccount(AccountRow row, SqliteConnection connection)
        => new(
            row.Username,
            row.CredentialSubject,
            row.Email,
            row.Enabled,
            row.SecurityStamp,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.LockedUntilUtc,
            ReadGrants(connection, row.Username),
            row.Role);

    private static IReadOnlyList<ProductPermissionGrant> ReadGrants(
        SqliteConnection connection,
        string username)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT permission_code, scope_kind, scope_value
            FROM remote_account_grants
            WHERE username = $username
            ORDER BY permission_code, scope_kind, scope_value;
            """;
        command.Parameters.AddWithValue("$username", username);
        using var reader = command.ExecuteReader();
        var grants = new List<ProductPermissionGrant>();
        while (reader.Read())
        {
            var kind = (ProductPermissionScopeKind)reader.GetInt32(1);
            var scope = kind == ProductPermissionScopeKind.Global
                ? ProductPermissionScope.Global
                : ProductPermissionScope.ForServer(Guid.ParseExact(reader.GetString(2), "D"));
            grants.Add(new ProductPermissionGrant(reader.GetString(0), scope));
        }

        return grants.AsReadOnly();
    }

    private static AuthenticationRow? ReadAuthenticationRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string username)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT credential_subject, enabled, pin_salt, pin_verifier,
                   failed_attempts, locked_until_utc
            FROM remote_accounts
            WHERE username = $username;
            """;
        command.Parameters.AddWithValue("$username", username);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AuthenticationRow(
            username,
            reader.GetString(0),
            reader.GetInt64(1) == 1,
            (byte[])reader[2],
            (byte[])reader[3],
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)));
    }

    private static async Task ReplaceGrantsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string username,
        IReadOnlyList<ProductPermissionGrant> grants,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM remote_account_grants WHERE username = $username;";
            delete.Parameters.AddWithValue("$username", username);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var grant in grants)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO remote_account_grants(username, permission_code, scope_kind, scope_value)
                VALUES($username, $permission_code, $scope_kind, $scope_value);
                """;
            insert.Parameters.AddWithValue("$username", username);
            insert.Parameters.AddWithValue("$permission_code", grant.PermissionCode);
            insert.Parameters.AddWithValue("$scope_kind", (int)grant.Scope.Kind);
            insert.Parameters.AddWithValue(
                "$scope_value",
                grant.Scope.Kind == ProductPermissionScopeKind.Global
                    ? "*"
                    : grant.Scope.ServerId!.Value.ToString("D"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RevokeDevicesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string username,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE remote_remembered_devices
            SET revoked_at_utc = COALESCE(revoked_at_utc, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                revocation_reason = COALESCE(revocation_reason, $reason)
            WHERE username = $username AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountEnabledOwnersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM remote_accounts WHERE enabled = 1 AND role = 1;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static ProductRemoteAccountRole? ValidateOptionalRole(ProductRemoteAccountRole? role)
    {
        if (role is null)
        {
            return null;
        }

        return IsDefinedRole(role.Value)
            ? role
            : throw new ArgumentOutOfRangeException(nameof(role), "Remote account role is invalid.");
    }

    private static ProductRemoteAccountRole ReadRole(int value)
    {
        var role = (ProductRemoteAccountRole)value;
        return IsDefinedRole(role)
            ? role
            : throw new InvalidDataException("Remote account role is invalid.");
    }

    private static bool IsDefinedRole(ProductRemoteAccountRole role)
        => role is ProductRemoteAccountRole.Owner or ProductRemoteAccountRole.Admin or
            ProductRemoteAccountRole.Operator or ProductRemoteAccountRole.Viewer;

    private static IReadOnlyList<ProductPermissionGrant> AddRequiredOwnerManagementGrants(
        IReadOnlyList<ProductPermissionGrant> grants)
    {
        if (HasRequiredOwnerManagementGrants(grants))
        {
            return grants;
        }

        var merged = grants
            .Concat(OwnerManagementPermissionCodes.Select(code =>
                new ProductPermissionGrant(code, ProductPermissionScope.Global)))
            .DistinctBy(grant => (
                grant.PermissionCode,
                grant.Scope.Kind,
                grant.Scope.ServerId))
            .ToArray();
        if (merged.Length > MaximumGrantsPerAccount)
        {
            throw new InvalidOperationException(
                "The owner management grants exceed the remote account grant limit.");
        }

        return merged;
    }

    private static bool HasRequiredOwnerManagementGrants(
        IReadOnlyCollection<ProductPermissionGrant> grants)
        => OwnerManagementPermissionCodes.All(code => grants.Any(grant =>
            string.Equals(grant.PermissionCode, code, StringComparison.Ordinal) &&
            grant.Scope.Kind == ProductPermissionScopeKind.Global));

    private DateTimeOffset? RegisterFailedAttempt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticationRow row,
        DateTimeOffset now)
    {
        if (row.LockedUntilUtc is { } existingLock && existingLock > now)
        {
            return existingLock;
        }

        var failures = row.FailedAttempts + 1;
        DateTimeOffset? lockedUntil = failures >= MaximumFailedAttempts
            ? now.Add(LockoutDuration)
            : null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE remote_accounts
            SET failed_attempts = $failed_attempts, locked_until_utc = $locked_until
            WHERE username = $username;
            """;
        command.Parameters.AddWithValue("$failed_attempts", lockedUntil is null ? failures : 0);
        command.Parameters.AddWithValue(
            "$locked_until",
            lockedUntil is { } value ? FormatUtc(value) : DBNull.Value);
        command.Parameters.AddWithValue("$username", row.Username);
        command.ExecuteNonQuery();
        return lockedUntil;
    }

    private byte[] DeriveVerifier(string pin, byte[] salt)
    {
        var bytes = Encoding.UTF8.GetBytes(pin);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                bytes,
                salt,
                _kdfIterations,
                HashAlgorithmName.SHA256,
                VerifierBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static IReadOnlyList<ProductPermissionGrant> ValidateGrants(
        IReadOnlyCollection<ProductPermissionGrant>? grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count > MaximumGrantsPerAccount || grants.Any(grant => !ProductAuthorization.TryValidateGrant(grant)))
        {
            throw new ArgumentException("Remote account grants are invalid.", nameof(grants));
        }

        var distinct = grants
            .DistinctBy(grant => (
                grant.PermissionCode,
                grant.Scope.Kind,
                grant.Scope.ServerId))
            .ToArray();
        if (distinct.Length != grants.Count)
        {
            throw new ArgumentException("Remote account grants contain duplicates.", nameof(grants));
        }

        return distinct;
    }

    private static string NormalizeUsername(string value)
        => TryNormalizeUsername(value, out var normalized)
            ? normalized
            : throw new ArgumentException("Remote username is invalid.", nameof(value));

    public static bool TryNormalizeUsername(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length is < 6 or > 32 || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        if (value.Skip(1).Any(character => !IsAsciiLetter(character) && !IsAsciiDigit(character)))
        {
            return false;
        }

        normalized = value.ToLowerInvariant();
        return true;
    }

    private static string NormalizeSubject(string value)
        => TryNormalizeSubject(value, out var normalized)
            ? normalized
            : throw new ArgumentException("Credential subject is invalid.", nameof(value));

    private static bool TryNormalizeSubject(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length is > 0 and <= 254 &&
               normalized.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }

    private static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 254 ||
            normalized.Count(character => character == '@') != 1 ||
            normalized.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("Remote account email is invalid.", nameof(value));
        }

        return normalized;
    }

    private static void ValidatePin(string pin)
    {
        if (!IsValidPin(pin))
        {
            throw new ArgumentException("Remote PIN must contain 4 to 12 digits.", nameof(pin));
        }
    }

    public static bool IsValidPin(string? value)
        => value is { Length: >= 4 and <= 12 } && value.All(IsAsciiDigit);

    private static string CreateSecurityStamp() => Convert.ToHexString(
        RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool IsAsciiLetter(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed record AuthenticationRow(
        string Username,
        string CredentialSubject,
        bool Enabled,
        byte[] Salt,
        byte[] Verifier,
        int FailedAttempts,
        DateTimeOffset? LockedUntilUtc);

    private sealed record AccountRow(
        string Username,
        string CredentialSubject,
        string? Email,
        bool Enabled,
        string SecurityStamp,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? LockedUntilUtc,
        ProductRemoteAccountRole Role);
}
