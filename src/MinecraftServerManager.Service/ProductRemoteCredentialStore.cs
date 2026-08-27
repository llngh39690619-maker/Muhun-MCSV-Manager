using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Data;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Service;

/// <summary>
/// Formal Service adapter for remote credentials, scoped authorization, and rotating remembered
/// devices. Browser tokens are never stored verbatim and recoverable PINs never cross this type.
/// </summary>
public sealed class ProductRemoteCredentialStore(
    ProductRemoteAccountStore accounts,
    ProductRememberedDeviceStore devices) :
    IRemoteCredentialStore,
    IRemoteAuthorizationStore,
    IRemoteRememberedDeviceStore
{
    private const string TokenPrefix = "v1";
    private const int TokenSecretBytes = 32;
    private const int MaximumTokenCharacters = 160;
    private static readonly byte[] RefreshDomain = "Muhun MCSV remembered refresh v1\0"u8.ToArray();

    public bool HasCredentialForLogin(string tailscaleLogin)
    {
        try
        {
            return accounts.HasEnabledAccountForSubject(tailscaleLogin);
        }
        catch (Exception error) when (IsDurableStoreFailure(error))
        {
            return false;
        }
    }

    public RemoteCredentialAuthenticationResult Authenticate(
        string tailscaleLogin,
        string username,
        string pin)
    {
        try
        {
            var result = accounts.Authenticate(tailscaleLogin, username, pin);
            return result.Status switch
            {
                ProductRemoteAuthenticationStatus.Success when result.Account is { } account =>
                    new RemoteCredentialAuthenticationResult(
                        RemoteCredentialAuthenticationStatus.Success,
                        account.Username,
                        Permissions: MapLegacyPermissions(account.Grants)),
                ProductRemoteAuthenticationStatus.LockedOut =>
                    new RemoteCredentialAuthenticationResult(
                        RemoteCredentialAuthenticationStatus.LockedOut,
                        LockedUntilUtc: result.LockedUntilUtc),
                _ => new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials),
            };
        }
        catch (Exception error) when (IsDurableStoreFailure(error))
        {
            return new RemoteCredentialAuthenticationResult(
                RemoteCredentialAuthenticationStatus.InvalidCredentials);
        }
    }

    public bool TryGetAuthorization(
        string credentialSubject,
        string username,
        out RemoteAuthorizationSnapshot snapshot)
    {
        snapshot = default!;
        try
        {
            if (!accounts.TryGet(username, out var account) ||
                !account.Enabled ||
                !string.Equals(
                    account.CredentialSubject,
                    credentialSubject?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return RemoteAuthorizationSnapshotValidator.TryCreateImmutable(
                new RemoteAuthorizationSnapshot(account.SecurityStamp, account.Grants),
                out snapshot);
        }
        catch (Exception error) when (IsDurableStoreFailure(error))
        {
            return false;
        }
    }

    public IssuedRemoteRememberedDevice IssueRememberedDevice(
        string login,
        string username,
        string label)
    {
        if (!TryGetAuthorizedAccount(login, username, out var account))
        {
            throw new UnauthorizedAccessException("Remote account authorization is unavailable.");
        }

        var secret = RandomNumberGenerator.GetBytes(TokenSecretBytes);
        var hash = SHA256.HashData(secret);
        try
        {
            var device = devices.Issue(account.Username, label, hash);
            return new IssuedRemoteRememberedDevice(
                EncodeToken(device.DeviceId, device.Generation, secret),
                MapDevice(device),
                MapLegacyPermissions(account.Grants));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
        string login,
        string token,
        Guid requestId)
    {
        if (requestId == Guid.Empty || !TryParseToken(token, out var parsed))
        {
            return InvalidRefresh();
        }

        byte[]? presentedHash = null;
        byte[]? replacement = null;
        byte[]? replacementHash = null;
        try
        {
            var nextGeneration = checked(parsed.Generation + 1);
            replacement = DeriveReplacementSecret(
                parsed.Secret,
                requestId,
                nextGeneration);
            presentedHash = SHA256.HashData(parsed.Secret);
            replacementHash = SHA256.HashData(replacement);
            var result = devices.Rotate(
                parsed.DeviceId,
                parsed.Generation,
                presentedHash,
                replacementHash,
                requestId);
            if (result.Status != ProductRememberedDeviceRefreshStatus.Success ||
                result.Device is null ||
                result.Username is null)
            {
                return MapRefreshFailure(result);
            }

            if (!TryGetAuthorizedAccount(login, result.Username, out var account))
            {
                devices.Revoke(result.Device.DeviceId, "authorization_unavailable");
                return new RemoteRememberedDeviceRefreshResult(
                    RemoteRememberedDeviceRefreshStatus.Revoked);
            }

            return new RemoteRememberedDeviceRefreshResult(
                RemoteRememberedDeviceRefreshStatus.Success,
                EncodeToken(result.Device.DeviceId, result.Device.Generation, replacement),
                MapDevice(result.Device),
                account.Username,
                MapLegacyPermissions(account.Grants));
        }
        catch (OverflowException)
        {
            devices.Revoke(parsed.DeviceId, "generation_exhausted");
            return new RemoteRememberedDeviceRefreshResult(
                RemoteRememberedDeviceRefreshStatus.Revoked);
        }
        catch (Exception error) when (IsDurableStoreFailure(error))
        {
            return InvalidRefresh();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
            if (presentedHash is not null) CryptographicOperations.ZeroMemory(presentedHash);
            if (replacement is not null) CryptographicOperations.ZeroMemory(replacement);
            if (replacementHash is not null) CryptographicOperations.ZeroMemory(replacementHash);
        }
    }

    public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices()
        => devices.List().Select(MapDevice).ToArray();

    public bool RevokeRememberedDevice(string login, string token)
    {
        if (!TryParseToken(token, out var parsed))
        {
            return false;
        }

        try
        {
            var device = devices.List().FirstOrDefault(candidate => candidate.DeviceId == parsed.DeviceId);
            return device is not null &&
                   TryGetAuthorizedAccount(login, device.Username, out _) &&
                   devices.Revoke(parsed.DeviceId, "browser_signout");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    public bool RevokeRememberedDevice(Guid deviceId)
        => devices.Revoke(deviceId);

    public int RevokeRememberedDevicesForAccount(string username)
        => devices.RevokeForAccount(username);

    public int RevokeAllRememberedDevices()
        => devices.RevokeAll();

    private bool TryGetAuthorizedAccount(
        string subject,
        string username,
        out ProductRemoteAccountInfo account)
    {
        account = null!;
        return accounts.TryGet(username, out var candidate) &&
               candidate.Enabled &&
               string.Equals(
                   candidate.CredentialSubject,
                   subject?.Trim(),
                   StringComparison.OrdinalIgnoreCase) &&
               (account = candidate) is not null;
    }

    private static RemoteRememberedDeviceRefreshResult MapRefreshFailure(
        ProductRememberedDeviceRefreshResult result)
        => new(result.Status switch
        {
            ProductRememberedDeviceRefreshStatus.Expired => RemoteRememberedDeviceRefreshStatus.Expired,
            ProductRememberedDeviceRefreshStatus.Revoked => RemoteRememberedDeviceRefreshStatus.Revoked,
            ProductRememberedDeviceRefreshStatus.ReplayDetected => RemoteRememberedDeviceRefreshStatus.ReplayDetected,
            _ => RemoteRememberedDeviceRefreshStatus.Invalid,
        });

    private static RemoteRememberedDeviceRefreshResult InvalidRefresh()
        => new(RemoteRememberedDeviceRefreshStatus.Invalid);

    private static RemoteRememberedDeviceInfo MapDevice(ProductRememberedDeviceInfo device)
        => new(
            device.DeviceId,
            device.Username,
            device.Label,
            device.CreatedAtUtc,
            device.LastUsedAtUtc,
            device.IdleExpiresAtUtc,
            device.AbsoluteExpiresAtUtc,
            device.Status switch
            {
                ProductRememberedDeviceStatus.Active => RemoteRememberedDeviceStatus.Active,
                ProductRememberedDeviceStatus.Expired => RemoteRememberedDeviceStatus.Expired,
                _ => RemoteRememberedDeviceStatus.Revoked,
            },
            device.RevokedAtUtc,
            device.RevocationReason);

    private static RemoteWebPermission MapLegacyPermissions(
        IReadOnlyList<ProductPermissionGrant> grants)
    {
        var permissions = RemoteWebPermission.None;
        foreach (var grant in grants)
        {
            permissions |= grant.PermissionCode switch
            {
                ProductPermissionCodes.ServerStart => RemoteWebPermission.StartServer,
                ProductPermissionCodes.ServerStop => RemoteWebPermission.StopServer,
                ProductPermissionCodes.ServerRestart => RemoteWebPermission.RestartServer,
                ProductPermissionCodes.ConsoleWrite => RemoteWebPermission.SendConsoleCommand,
                ProductPermissionCodes.PlayerManage => RemoteWebPermission.ManagePlayers,
                ProductPermissionCodes.BackupCreate => RemoteWebPermission.CreateBackup,
                _ => RemoteWebPermission.None,
            };
        }

        return permissions;
    }

    private static byte[] DeriveReplacementSecret(
        ReadOnlySpan<byte> currentSecret,
        Guid requestId,
        ulong nextGeneration)
    {
        var material = new byte[RefreshDomain.Length + 16 + sizeof(ulong)];
        RefreshDomain.CopyTo(material, 0);
        requestId.TryWriteBytes(material.AsSpan(RefreshDomain.Length, 16));
        BinaryPrimitives.WriteUInt64BigEndian(material.AsSpan(RefreshDomain.Length + 16), nextGeneration);
        try
        {
            return HMACSHA256.HashData(currentSecret, material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static string EncodeToken(Guid deviceId, ulong generation, ReadOnlySpan<byte> secret)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{TokenPrefix}.{deviceId:D}.{generation}.{Base64UrlEncode(secret)}");

    private static bool TryParseToken(string? value, out ParsedToken token)
    {
        token = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTokenCharacters)
        {
            return false;
        }

        var parts = value.Split('.');
        byte[] secret = [];
        if (parts.Length != 4 ||
            !string.Equals(parts[0], TokenPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(parts[1], "D", out var deviceId) ||
            deviceId == Guid.Empty ||
            !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var generation) ||
            generation == 0 ||
            !TryBase64UrlDecode(parts[3], out secret) ||
            secret.Length != TokenSecretBytes)
        {
            CryptographicOperations.ZeroMemory(secret);
            return false;
        }

        token = new ParsedToken(deviceId, generation, secret);
        return true;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] secret)
    {
        secret = [];
        if (value.Length != 43 || value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        try
        {
            secret = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsDurableStoreFailure(Exception error)
        => error is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or CryptographicException or Microsoft.Data.Sqlite.SqliteException;

    private readonly record struct ParsedToken(Guid DeviceId, ulong Generation, byte[] Secret);
}
