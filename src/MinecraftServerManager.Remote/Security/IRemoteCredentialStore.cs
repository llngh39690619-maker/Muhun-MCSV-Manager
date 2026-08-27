namespace MinecraftServerManager.Remote;

[Flags]
public enum RemoteWebPermission
{
    None = 0,
    StartServer = 1 << 0,
    StopServer = 1 << 1,
    RestartServer = 1 << 2,
    SendConsoleCommand = 1 << 3,
    ManagePlayers = 1 << 4,
    CreateBackup = 1 << 5,
    All = StartServer | StopServer | RestartServer | SendConsoleCommand | ManagePlayers | CreateBackup
}

public enum RemoteCredentialAuthenticationStatus
{
    Success,
    InvalidCredentials,
    LockedOut
}

public sealed record RemoteCredentialAuthenticationResult(
    RemoteCredentialAuthenticationStatus Status,
    string? Username = null,
    DateTimeOffset? LockedUntilUtc = null,
    RemoteWebPermission Permissions = RemoteWebPermission.All);

/// <summary>
/// Supplies the persistent desktop-approved credential used by the mobile site.
/// The implementation is owned by the desktop process so credentials outlive an
/// embedded web-host restart without being placed in manager.json.
/// </summary>
public interface IRemoteCredentialStore
{
    bool HasCredentialForLogin(string tailscaleLogin);

    RemoteCredentialAuthenticationResult Authenticate(
        string tailscaleLogin,
        string username,
        string pin);
}

public enum RemoteRememberedDeviceStatus
{
    Active,
    Revoked,
    Expired
}

public enum RemoteRememberedDeviceRefreshStatus
{
    Success,
    Invalid,
    Expired,
    Revoked,
    ReplayDetected
}

/// <summary>
/// Non-secret desktop-facing metadata for one remembered browser or PWA installation.
/// No token secret, PIN verifier, or recoverable PIN is exposed through this model.
/// </summary>
public sealed record RemoteRememberedDeviceInfo(
    Guid DeviceId,
    string Username,
    string Label,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUsedAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    RemoteRememberedDeviceStatus Status,
    DateTimeOffset? RevokedAtUtc = null,
    string? RevocationReason = null);

public sealed record IssuedRemoteRememberedDevice(
    string Token,
    RemoteRememberedDeviceInfo Device,
    RemoteWebPermission Permissions);

public sealed record RemoteRememberedDeviceRefreshResult(
    RemoteRememberedDeviceRefreshStatus Status,
    string? ReplacementToken = null,
    RemoteRememberedDeviceInfo? Device = null,
    string? Username = null,
    RemoteWebPermission Permissions = RemoteWebPermission.None);

/// <summary>
/// Persists revocable, rotating device credentials independently from the user's PIN.
/// Implementations must never persist an issued token verbatim.
/// </summary>
public interface IRemoteRememberedDeviceStore
{
    IssuedRemoteRememberedDevice IssueRememberedDevice(
        string login,
        string username,
        string label);

    RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
        string login,
        string token,
        Guid requestId);

    IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices();

    bool RevokeRememberedDevice(string login, string token);

    bool RevokeRememberedDevice(Guid deviceId);

    int RevokeRememberedDevicesForAccount(string username);

    int RevokeAllRememberedDevices();
}

public static class RemoteCredentialRules
{
    public const int MinimumUsernameLength = 6;
    public const int MaximumUsernameLength = 32;
    public const int MinimumPinLength = 4;
    public const int MaximumPinLength = 12;

    public static bool TryNormalizeUsername(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length is < MinimumUsernameLength or > MaximumUsernameLength)
        {
            return false;
        }

        if (!IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsAsciiLetter(value[index]) && !IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        normalized = value.ToLowerInvariant();
        return true;
    }

    public static bool IsValidPin(string? value)
    {
        if (value is null || value.Length is < MinimumPinLength or > MaximumPinLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value)
        => value is >= '0' and <= '9';
}
