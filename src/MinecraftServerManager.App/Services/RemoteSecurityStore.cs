using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.Services;

internal sealed record RemoteApprovedAccount(
    string Username,
    string? Gmail,
    DateTimeOffset? EmailVerifiedAtUtc,
    DateTimeOffset CreatedAtUtc,
    RemoteWebPermission Permissions,
    bool HasRecoverablePin = false);

internal sealed class GmailSmtpCredential
{
    public GmailSmtpCredential(string senderGmail, string appPassword)
    {
        SenderGmail = senderGmail;
        AppPassword = appPassword;
    }

    public string SenderGmail { get; }
    public string AppPassword { get; }

    public override string ToString() => SenderGmail;
}

internal sealed class CloudflareNamedTunnelCredential
{
    public CloudflareNamedTunnelCredential(string token)
    {
        Token = token;
    }

    public string Token { get; }

    public override string ToString() => "[REDACTED]";
}

internal interface IRemoteSecurityStore : IRemoteCredentialStore, IRemoteRememberedDeviceStore
{
    bool IsAvailable { get; }
    string? AvailabilityError { get; }
    string? SmtpSenderGmail { get; }
    bool HasCloudflareNamedTunnelToken { get; }
    bool HasCloudflaredInstallationReceipt { get; }
    RemoteApprovedAccount? ApprovedAccount { get; }
    IReadOnlyList<RemoteApprovedAccount> ApprovedAccounts { get; }

    GmailSmtpCredential GetSmtpCredential();
    void SaveSmtpCredential(string senderGmail, string appPassword);
    void DeleteSmtpCredential();
    CloudflareNamedTunnelCredential GetCloudflareNamedTunnelCredential();
    void SaveCloudflareNamedTunnelToken(string token);
    void DeleteCloudflareNamedTunnelToken();
    CloudflaredInstallationReceipt GetCloudflaredInstallationReceipt();
    void SaveCloudflaredInstallationReceipt(CloudflaredInstallationReceipt receipt);
    void DeleteCloudflaredInstallationReceipt();
    void RegisterAccount(
        string? verifiedGmail,
        string username,
        string pin,
        RemoteWebPermission permissions = RemoteWebPermission.All);
    void UpdateAccountPermissions(string username, RemoteWebPermission permissions);
    void ResetAccountPin(string username, string newPin);
    void DeleteAccount(string username);
    string? GetRecoverablePin(string username);
}

/// <summary>
/// Stores SMTP and remote-account secrets in one DPAPI CurrentUser protected file.
/// Nothing from this file is copied into manager.json.
/// </summary>
internal sealed class RemoteSecurityStore : IRemoteSecurityStore
{
    private const int CurrentSchemaVersion = 7;
    private const int MaximumAccounts = 32;
    private const int MaximumRememberedDevicesPerAccount = 8;
    private const int MaximumRememberedDevices = 64;
    private const int KdfIterations = 600_000;
    private const int SaltBytes = 32;
    private const int VerifierBytes = 32;
    private const int DeviceSecretBytes = 32;
    private const int DeviceMasterKeyBytes = 32;
    private const int DeviceSaltBytes = 32;
    private const int MaximumDeviceLabelCharacters = 64;
    private const int MaximumFileBytes = 128 * 1024;
    private static readonly TimeSpan RememberedDeviceIdleLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan RememberedDeviceAbsoluteLifetime = TimeSpan.FromDays(365);
    private static readonly byte[] FileHeader = "MCSV-REMOTE-SECURITY-1\n"u8.ToArray();
    private static readonly byte[] OptionalEntropy =
        SHA256.HashData("Muhun MCSV Manager remote security v1"u8);
    private static readonly byte[] RecoverablePinEntropyDomain =
        "Muhun MCSV Manager recoverable remote PIN v1\0"u8.ToArray();
    private static readonly byte[] RememberedDeviceSecretDomain =
        "Muhun MCSV Manager remembered device token v1\0"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _dummySalt = RandomNumberGenerator.GetBytes(SaltBytes);
    private readonly byte[] _dummyVerifier = RandomNumberGenerator.GetBytes(VerifierBytes);
    private VaultDocument _document = VaultDocument.CreateEmpty();

    public RemoteSecurityStore(string filePath, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A remote security file path is required.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        LoadFailClosed();
    }

    public bool IsAvailable { get; private set; } = true;
    public string? AvailabilityError { get; private set; }

    public string? SmtpSenderGmail
    {
        get
        {
            lock (_gate)
            {
                return _document.Smtp?.SenderGmail;
            }
        }
    }

    public bool HasCloudflareNamedTunnelToken
    {
        get
        {
            lock (_gate)
            {
                return _document.CloudflareNamedTunnelToken is not null;
            }
        }
    }

    public bool HasCloudflaredInstallationReceipt
    {
        get
        {
            lock (_gate)
            {
                return _document.CloudflaredInstallationReceipt is not null;
            }
        }
    }

    public RemoteApprovedAccount? ApprovedAccount
        => ApprovedAccounts.FirstOrDefault();

    public IReadOnlyList<RemoteApprovedAccount> ApprovedAccounts
    {
        get
        {
            lock (_gate)
            {
                return GetCredentials(_document)
                    .Select(ToApprovedAccount)
                    .ToArray();
            }
        }
    }

    public bool HasCredentialForLogin(string tailscaleLogin)
    {
        lock (_gate)
        {
            EnsureAvailable();
            return GetCredentials(_document)
                .Any(credential => CredentialBelongsToSubject(credential, tailscaleLogin));
        }
    }

    public GmailSmtpCredential GetSmtpCredential()
    {
        lock (_gate)
        {
            EnsureAvailable();
            var smtp = _document.Smtp
                ?? throw new InvalidOperationException("請先儲存 Gmail SMTP 寄件設定。");
            return new GmailSmtpCredential(smtp.SenderGmail, smtp.AppPassword);
        }
    }

    public void SaveSmtpCredential(string senderGmail, string appPassword)
    {
        senderGmail = senderGmail?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RemoteIdentity.IsCanonicalGmailLogin(senderGmail))
        {
            throw new InvalidOperationException("寄件帳號必須是完整且有效的 @gmail.com 帳號。");
        }

        if (!TryNormalizeGoogleAppPassword(appPassword, out var normalizedPassword))
        {
            throw new InvalidOperationException("Gmail 應用程式密碼必須是 Google 產生的 16 個英文字母；可直接貼上含空格的格式。");
        }

        lock (_gate)
        {
            EnsureAvailable();
            var updated = _document with
            {
                Smtp = new SmtpRecord(senderGmail, normalizedPassword)
            };
            SaveAndCommit(updated);
        }
    }

    public void DeleteSmtpCredential()
    {
        lock (_gate)
        {
            EnsureAvailable();
            if (_document.Smtp is null) return;
            SaveAndCommit(_document with { Smtp = null });
        }
    }

    public CloudflareNamedTunnelCredential GetCloudflareNamedTunnelCredential()
    {
        lock (_gate)
        {
            EnsureAvailable();
            var token = _document.CloudflareNamedTunnelToken
                ?? throw new InvalidOperationException("請先儲存 Cloudflare Named Tunnel Token。");
            return new CloudflareNamedTunnelCredential(token);
        }
    }

    public void SaveCloudflareNamedTunnelToken(string token)
    {
        if (!TryNormalizeCloudflareNamedTunnelToken(token, out var normalizedToken))
        {
            throw new InvalidOperationException(
                "Cloudflare Named Tunnel Token 格式無效；請貼上 Dashboard 提供的完整 Token。");
        }

        lock (_gate)
        {
            EnsureAvailable();
            SaveAndCommit(_document with { CloudflareNamedTunnelToken = normalizedToken });
        }
    }

    public void DeleteCloudflareNamedTunnelToken()
    {
        lock (_gate)
        {
            EnsureAvailable();
            if (_document.CloudflareNamedTunnelToken is null) return;
            SaveAndCommit(_document with { CloudflareNamedTunnelToken = null });
        }
    }

    public CloudflaredInstallationReceipt GetCloudflaredInstallationReceipt()
    {
        lock (_gate)
        {
            EnsureAvailable();
            return _document.CloudflaredInstallationReceipt
                ?? throw new InvalidOperationException(
                    "請先以 MCSV 安全下載 cloudflared.exe 並建立安裝收據。");
        }
    }

    public void SaveCloudflaredInstallationReceipt(CloudflaredInstallationReceipt receipt)
    {
        CloudflaredInstallationReceipt.ValidateAndThrow(receipt);
        lock (_gate)
        {
            EnsureAvailable();
            SaveAndCommit(_document with { CloudflaredInstallationReceipt = receipt });
        }
    }

    public void DeleteCloudflaredInstallationReceipt()
    {
        lock (_gate)
        {
            EnsureAvailable();
            if (_document.CloudflaredInstallationReceipt is null) return;
            SaveAndCommit(_document with { CloudflaredInstallationReceipt = null });
        }
    }

    public void RegisterAccount(
        string? verifiedGmail,
        string username,
        string pin,
        RemoteWebPermission permissions = RemoteWebPermission.All)
    {
        verifiedGmail = string.IsNullOrWhiteSpace(verifiedGmail)
            ? null
            : verifiedGmail.Trim().ToLowerInvariant();
        if (verifiedGmail is not null && !RemoteIdentity.IsCanonicalGmailLogin(verifiedGmail))
        {
            throw new InvalidOperationException("通過驗證的 Gmail 格式無效。");
        }

        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalizedUsername))
        {
            throw new InvalidOperationException("帳號必須是 6–32 位英文字母與數字，且第一個字元必須是英文字母。");
        }

        if (!RemoteCredentialRules.IsValidPin(pin))
        {
            throw new InvalidOperationException("密碼必須是 4–12 位半形數字。");
        }

        ValidatePermissions(permissions);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[]? verifier = null;
        try
        {
            // Keep KDF and inner DPAPI operations inside the same zeroization boundary. Any
            // cryptographic failure must not bypass clearing already-created buffers.
            verifier = DeriveVerifier(pin, salt, KdfIterations);
            var recoverablePinCiphertext = ProtectRecoverablePin(pin, normalizedUsername);
            lock (_gate)
            {
                EnsureAvailable();
                var credentials = GetCredentials(_document);
                if (credentials.Count >= MaximumAccounts)
                {
                    throw new InvalidOperationException($"遠端帳號最多可建立 {MaximumAccounts} 個。");
                }

                if (credentials.Any(credential =>
                        string.Equals(
                            credential.Username,
                            normalizedUsername,
                            StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("此遠端帳號名稱已存在；請使用其他帳號名稱。");
                }

                var now = _timeProvider.GetUtcNow();
                var added = new CredentialRecord(
                    normalizedUsername,
                    verifiedGmail,
                    Convert.ToBase64String(salt),
                    Convert.ToBase64String(verifier),
                    KdfIterations,
                    verifiedGmail is null ? null : now,
                    now,
                    null,
                    0,
                    0,
                    null,
                    permissions,
                    recoverablePinCiphertext);
                var updated = _document with
                {
                    Credentials = [.. credentials, added]
                };
                SaveAndCommit(updated);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            if (verifier is not null) CryptographicOperations.ZeroMemory(verifier);
        }
    }

    public void UpdateAccountPermissions(string username, RemoteWebPermission permissions)
    {
        var normalizedUsername = NormalizeExistingUsername(username);
        ValidatePermissions(permissions);
        lock (_gate)
        {
            EnsureAvailable();
            var credential = FindCredential(_document, normalizedUsername)
                ?? throw new InvalidOperationException("找不到指定的遠端帳號。");
            if (credential.Permissions == permissions) return;
            SaveAndCommit(ReplaceCredential(
                _document,
                credential with { Permissions = permissions }));
        }
    }

    public void ResetAccountPin(string username, string newPin)
    {
        var normalizedUsername = NormalizeExistingUsername(username);
        if (!RemoteCredentialRules.IsValidPin(newPin))
        {
            throw new InvalidOperationException("密碼必須是 4–12 位半形數字。");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[]? verifier = null;
        try
        {
            verifier = DeriveVerifier(newPin, salt, KdfIterations);
            lock (_gate)
            {
                EnsureAvailable();
                var credential = FindCredential(_document, normalizedUsername)
                    ?? throw new InvalidOperationException("找不到指定的遠端帳號。");
                var updated = ReplaceCredential(
                    _document,
                    credential with
                    {
                        Salt = Convert.ToBase64String(salt),
                        Verifier = Convert.ToBase64String(verifier),
                        Iterations = KdfIterations,
                        LastAuthenticatedAtUtc = null,
                        FailedAttempts = 0,
                        LockoutLevel = 0,
                        LockedUntilUtc = null,
                        RecoverablePinCiphertext = ProtectRecoverablePin(
                            newPin,
                            normalizedUsername)
                    });
                updated = RevokeDevices(
                    updated,
                    device => string.Equals(
                        device.Username,
                        normalizedUsername,
                        StringComparison.Ordinal),
                    _timeProvider.GetUtcNow(),
                    "pin-reset",
                    out _);
                SaveAndCommit(updated);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            if (verifier is not null) CryptographicOperations.ZeroMemory(verifier);
        }
    }

    public void DeleteAccount(string username)
    {
        var normalizedUsername = NormalizeExistingUsername(username);
        lock (_gate)
        {
            EnsureAvailable();
            var credentials = GetCredentials(_document);
            var remaining = credentials
                .Where(credential => !string.Equals(
                    credential.Username,
                    normalizedUsername,
                    StringComparison.Ordinal))
                .ToArray();
            if (remaining.Length == credentials.Count)
            {
                throw new InvalidOperationException("找不到指定的遠端帳號。");
            }

            SaveAndCommit(_document with
            {
                Credentials = remaining,
                // Account deletion and device invalidation are one atomic DPAPI commit.
                Devices = GetDevices(_document)
                    .Where(device => !string.Equals(
                        device.Username,
                        normalizedUsername,
                        StringComparison.Ordinal))
                    .ToArray()
            });
        }
    }

    public string? GetRecoverablePin(string username)
    {
        var normalizedUsername = NormalizeExistingUsername(username);
        lock (_gate)
        {
            EnsureAvailable();
            var credential = FindCredential(_document, normalizedUsername)
                ?? throw new InvalidOperationException("找不到指定的遠端帳號。");
            if (credential.RecoverablePinCiphertext is null) return null;
            byte[]? plaintext = null;
            try
            {
                plaintext = UnprotectRecoverablePinBytes(
                    credential.RecoverablePinCiphertext,
                    normalizedUsername);
                return Encoding.ASCII.GetString(plaintext);
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public IssuedRemoteRememberedDevice IssueRememberedDevice(
        string login,
        string username,
        string label)
    {
        var normalizedUsername = NormalizeExistingUsername(username);
        var normalizedLabel = NormalizeDeviceLabel(label);
        var salt = RandomNumberGenerator.GetBytes(DeviceSaltBytes);
        try
        {
            lock (_gate)
            {
                EnsureAvailable();
                var credential = FindCredential(_document, normalizedUsername)
                    ?? throw new InvalidOperationException("找不到指定的遠端帳號。");
                if (!CredentialBelongsToSubject(credential, login))
                {
                    throw new InvalidOperationException("遠端帳號不屬於目前連線身分。");
                }

                var now = _timeProvider.GetUtcNow();
                var devices = GetDevices(_document).ToList();
                devices.RemoveAll(device =>
                    string.Equals(device.Username, normalizedUsername, StringComparison.Ordinal) &&
                    GetDeviceStatus(device, now) != RemoteRememberedDeviceStatus.Active);
                var activeForAccount = devices.Count(device =>
                    string.Equals(device.Username, normalizedUsername, StringComparison.Ordinal) &&
                    GetDeviceStatus(device, now) == RemoteRememberedDeviceStatus.Active);
                if (activeForAccount >= MaximumRememberedDevicesPerAccount)
                {
                    throw new InvalidOperationException(
                        $"每個遠端帳號最多可記住 {MaximumRememberedDevicesPerAccount} 台裝置。");
                }

                if (devices.Count >= MaximumRememberedDevices)
                {
                    devices.RemoveAll(device =>
                        GetDeviceStatus(device, now) != RemoteRememberedDeviceStatus.Active);
                }
                if (devices.Count >= MaximumRememberedDevices)
                {
                    throw new InvalidOperationException(
                        $"最多可保存 {MaximumRememberedDevices} 台遠端裝置。");
                }

                var absoluteExpiresAtUtc = now.Add(RememberedDeviceAbsoluteLifetime);
                var record = new RememberedDeviceRecord(
                    Guid.NewGuid(),
                    normalizedUsername,
                    normalizedLabel,
                    Convert.ToBase64String(salt),
                    TokenGeneration: 0,
                    CreatedAtUtc: now,
                    LastUsedAtUtc: now,
                    IdleExpiresAtUtc: Min(now.Add(RememberedDeviceIdleLifetime), absoluteExpiresAtUtc),
                    AbsoluteExpiresAtUtc: absoluteExpiresAtUtc,
                    LastRefreshRequestId: null,
                    LastRotatedAtUtc: null,
                    RevokedAtUtc: null,
                    RevocationReason: null);
                var updated = _document with { Devices = [.. devices, record] };
                SaveAndCommit(updated);
                var token = CreateRememberedDeviceToken(updated, record);
                return new IssuedRemoteRememberedDevice(
                    token,
                    ToRememberedDeviceInfo(record, now),
                    credential.Permissions);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
        string login,
        string token,
        Guid requestId)
    {
        if (requestId == Guid.Empty ||
            !RememberedDeviceTokenCodec.TryParse(token, out var parsed))
        {
            return new RemoteRememberedDeviceRefreshResult(
                RemoteRememberedDeviceRefreshStatus.Invalid);
        }

        try
        {
            lock (_gate)
            {
                EnsureAvailable();
                var record = GetDevices(_document).FirstOrDefault(device =>
                    device.DeviceId == parsed.DeviceId);
                if (record is null ||
                    !RememberedDeviceSecretMatches(_document, record, parsed.Generation, parsed.Secret))
                {
                    // A forged token must never be able to revoke a real device merely by
                    // guessing its public device id and generation.
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Invalid);
                }

                var credential = FindCredential(_document, record.Username);
                if (credential is null || !CredentialBelongsToSubject(credential, login))
                {
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Invalid);
                }

                var now = _timeProvider.GetUtcNow();
                if (record.RevokedAtUtc is not null)
                {
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Revoked);
                }

                if (IsDeviceExpired(record, now))
                {
                    var expired = RevokeDeviceRecord(record, now, "expired");
                    SaveAndCommit(ReplaceDevice(_document, expired));
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Expired);
                }

                var isSameRequest = record.LastRefreshRequestId == requestId;
                if (parsed.Generation == record.TokenGeneration)
                {
                    if (isSameRequest)
                    {
                        return CreateRememberedDeviceRefreshSuccess(
                            _document,
                            record,
                            credential.Permissions,
                            now);
                    }

                    if (record.TokenGeneration == ulong.MaxValue)
                    {
                        var exhausted = RevokeDeviceRecord(record, now, "generation-exhausted");
                        SaveAndCommit(ReplaceDevice(_document, exhausted));
                        return new RemoteRememberedDeviceRefreshResult(
                            RemoteRememberedDeviceRefreshStatus.Revoked);
                    }

                    var rotated = record with
                    {
                        TokenGeneration = record.TokenGeneration + 1,
                        LastRefreshRequestId = requestId,
                        LastRotatedAtUtc = now,
                        LastUsedAtUtc = now,
                        IdleExpiresAtUtc = Min(
                            now.Add(RememberedDeviceIdleLifetime),
                            record.AbsoluteExpiresAtUtc)
                    };
                    var updated = ReplaceDevice(_document, rotated);
                    SaveAndCommit(updated);
                    return CreateRememberedDeviceRefreshSuccess(
                        updated,
                        rotated,
                        credential.Permissions,
                        now);
                }

                if (record.TokenGeneration > 0 &&
                    parsed.Generation == record.TokenGeneration - 1 &&
                    isSameRequest)
                {
                    // The first response may have been lost before Safari stored Set-Cookie.
                    // Recreate the exact current token without another rotation.
                    return CreateRememberedDeviceRefreshSuccess(
                        _document,
                        record,
                        credential.Permissions,
                        now);
                }

                if (parsed.Generation < record.TokenGeneration)
                {
                    var replayed = RevokeDeviceRecord(record, now, "replay-detected");
                    SaveAndCommit(ReplaceDevice(_document, replayed));
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.ReplayDetected);
                }

                return new RemoteRememberedDeviceRefreshResult(
                    RemoteRememberedDeviceRefreshStatus.Invalid);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices()
    {
        lock (_gate)
        {
            EnsureAvailable();
            var now = _timeProvider.GetUtcNow();
            return GetDevices(_document)
                .OrderByDescending(device => device.LastUsedAtUtc)
                .Select(device => ToRememberedDeviceInfo(device, now))
                .ToArray();
        }
    }

    public bool RevokeRememberedDevice(string login, string token)
    {
        if (!RememberedDeviceTokenCodec.TryParse(token, out var parsed)) return false;
        try
        {
            lock (_gate)
            {
                EnsureAvailable();
                var device = GetDevices(_document).FirstOrDefault(candidate =>
                    candidate.DeviceId == parsed.DeviceId);
                if (device is null ||
                    !RememberedDeviceSecretMatches(
                        _document,
                        device,
                        parsed.Generation,
                        parsed.Secret))
                {
                    return false;
                }

                var credential = FindCredential(_document, device.Username);
                if (credential is null ||
                    !CredentialBelongsToSubject(credential, login) ||
                    device.RevokedAtUtc is not null)
                {
                    return false;
                }

                SaveAndCommit(ReplaceDevice(
                    _document,
                    RevokeDeviceRecord(
                        device,
                        _timeProvider.GetUtcNow(),
                        "device-signout")));
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    public bool RevokeRememberedDevice(Guid deviceId)
    {
        if (deviceId == Guid.Empty) return false;
        lock (_gate)
        {
            EnsureAvailable();
            var device = GetDevices(_document).FirstOrDefault(candidate =>
                candidate.DeviceId == deviceId);
            if (device is null || device.RevokedAtUtc is not null) return false;
            SaveAndCommit(ReplaceDevice(
                _document,
                RevokeDeviceRecord(device, _timeProvider.GetUtcNow(), "desktop-revoked")));
            return true;
        }
    }

    public int RevokeRememberedDevicesForAccount(string username)
    {
        var normalizedUsername = NormalizeExistingUsername(username);
        lock (_gate)
        {
            EnsureAvailable();
            var updated = RevokeDevices(
                _document,
                device => string.Equals(
                    device.Username,
                    normalizedUsername,
                    StringComparison.Ordinal),
                _timeProvider.GetUtcNow(),
                "account-revoked",
                out var count);
            if (count > 0) SaveAndCommit(updated);
            return count;
        }
    }

    public int RevokeAllRememberedDevices()
    {
        lock (_gate)
        {
            EnsureAvailable();
            var updated = RevokeDevices(
                _document,
                static _ => true,
                _timeProvider.GetUtcNow(),
                "all-devices-revoked",
                out var count);
            if (count > 0) SaveAndCommit(updated);
            return count;
        }
    }

    public RemoteCredentialAuthenticationResult Authenticate(
        string tailscaleLogin,
        string username,
        string pin)
    {
        lock (_gate)
        {
            EnsureAvailable();
            var credential = GetCredentials(_document).FirstOrDefault(candidate =>
                CredentialBelongsToSubject(candidate, tailscaleLogin) &&
                string.Equals(candidate.Username, username, StringComparison.Ordinal));
            var usernameMatches = credential is not null;
            var salt = usernameMatches
                ? Convert.FromBase64String(credential!.Salt)
                : _dummySalt.ToArray();
            var expected = usernameMatches
                ? Convert.FromBase64String(credential!.Verifier)
                : _dummyVerifier.ToArray();
            var actual = DeriveVerifier(pin, salt, usernameMatches ? credential!.Iterations : KdfIterations);
            try
            {
                var passwordMatches = CryptographicOperations.FixedTimeEquals(expected, actual);
                var now = _timeProvider.GetUtcNow();
                if (credential is not null &&
                    credential.LockedUntilUtc is { } lockedUntil &&
                    lockedUntil > now)
                {
                    return new RemoteCredentialAuthenticationResult(
                        RemoteCredentialAuthenticationStatus.LockedOut,
                        LockedUntilUtc: lockedUntil);
                }

                if (usernameMatches && passwordMatches)
                {
                    SaveAndCommit(ReplaceCredential(
                        _document,
                        credential! with
                        {
                            LastAuthenticatedAtUtc = now,
                            FailedAttempts = 0,
                            LockoutLevel = 0,
                            LockedUntilUtc = null
                        }));
                    return new RemoteCredentialAuthenticationResult(
                        RemoteCredentialAuthenticationStatus.Success,
                        credential!.Username,
                        Permissions: credential.Permissions);
                }

                // Each known username has an independent persistent lockout. Unknown names
                // still execute the dummy KDF and receive the identical response, while the
                // API-level limiter prevents an attacker from locking every registered owner.
                if (credential is not null)
                {
                    var failures = credential!.LockedUntilUtc is { } expired && expired <= now
                        ? 1
                        : credential.FailedAttempts + 1;
                    var lockoutLevel = credential.LockoutLevel;
                    DateTimeOffset? nextLockedUntil = null;
                    if (failures >= 5)
                    {
                        failures = 0;
                        lockoutLevel = Math.Min(4, lockoutLevel + 1);
                        var minutes = lockoutLevel switch
                        {
                            1 => 15,
                            2 => 30,
                            3 => 60,
                            _ => 24 * 60
                        };
                        nextLockedUntil = now.AddMinutes(minutes);
                    }

                    SaveAndCommit(ReplaceCredential(
                        _document,
                        credential with
                        {
                            FailedAttempts = failures,
                            LockoutLevel = lockoutLevel,
                            LockedUntilUtc = nextLockedUntil
                        }));

                    if (nextLockedUntil is not null)
                    {
                        return new RemoteCredentialAuthenticationResult(
                            RemoteCredentialAuthenticationStatus.LockedOut,
                            LockedUntilUtc: nextLockedUntil);
                    }
                }

                return new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    internal static bool TryNormalizeGoogleAppPassword(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null) return false;

        Span<char> buffer = stackalloc char[16];
        try
        {
            var count = 0;
            foreach (var character in value)
            {
                if (character == ' ') continue;
                if (count >= buffer.Length || character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                {
                    return false;
                }

                buffer[count++] = char.ToLowerInvariant(character);
            }

            if (count != buffer.Length) return false;
            normalized = new string(buffer);
            return true;
        }
        finally
        {
            buffer.Clear();
        }
    }

    internal static bool TryNormalizeCloudflareNamedTunnelToken(
        string? value,
        out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 64 or > 4096)
        {
            normalized = string.Empty;
            return false;
        }

        if (normalized.Any(character =>
                !char.IsAscii(character) ||
                char.IsWhiteSpace(character) ||
                char.IsControl(character)))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private void LoadFailClosed()
    {
        if (!File.Exists(_filePath)) return;
        byte[]? file = null;
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            using (var stream = new FileStream(
                       _filePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       4096,
                       FileOptions.SequentialScan))
            {
                if (stream.Length <= FileHeader.Length || stream.Length > MaximumFileBytes)
                {
                    throw new InvalidDataException("Remote security file size is invalid.");
                }

                file = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
                stream.ReadExactly(file);
            }

            if (!file.AsSpan(0, FileHeader.Length).SequenceEqual(FileHeader))
            {
                throw new InvalidDataException("Remote security file header is invalid.");
            }

            encrypted = file.AsSpan(FileHeader.Length).ToArray();
            plaintext = ProtectedData.Unprotect(
                encrypted,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            var document = JsonSerializer.Deserialize<VaultDocument>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Remote security document is empty.");
            if (document.SchemaVersion is < 1 or > CurrentSchemaVersion)
            {
                throw new InvalidDataException("Remote security schema is not supported.");
            }

            ValidateLoadedDocument(document);
            if (document.SchemaVersion is 1 or 2)
            {
                // Schema 1 predates permissions; schema 2 contains one credential. Migrate
                // both atomically into the bounded account list without changing any KDF,
                // lockout, timestamp, Gmail or schema-2 permission fields.
                var legacy = document.Credential;
                if (document.SchemaVersion == 1 && legacy is not null)
                {
                    legacy = legacy with { Permissions = RemoteWebPermission.All };
                }

                var migrated = new VaultDocument(
                    CurrentSchemaVersion,
                    document.Smtp,
                    Credential: null,
                    Credentials: legacy is null
                        ? []
                        : [legacy with { RecoverablePinCiphertext = null }],
                    DeviceTokenKey: CreateDeviceMasterKey(),
                    Devices: []);
                ValidateLoadedDocument(migrated);
                // Preserve only the original DPAPI-protected bytes. The one-time,
                // never-overwritten rollback artifact must be durable before replacing the
                // legacy vault. Its dot-prefix also matches the sensitive-file backup filter.
                EnsureMigrationBackup(
                    GetLegacyMigrationBackupPath(_filePath),
                    file);
                SaveAndCommit(migrated);
            }
            else if (document.SchemaVersion == 3)
            {
                var migrated = document with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Credentials = GetCredentials(document)
                        .Select(credential => credential with
                        {
                            RecoverablePinCiphertext = null
                        })
                        .ToArray(),
                    DeviceTokenKey = CreateDeviceMasterKey(),
                    Devices = []
                };
                ValidateLoadedDocument(migrated);
                EnsureMigrationBackup(
                    GetSchemaThreeMigrationBackupPath(_filePath),
                    file);
                SaveAndCommit(migrated);
            }
            else if (document.SchemaVersion == 4)
            {
                var migrated = document with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    DeviceTokenKey = CreateDeviceMasterKey(),
                    Devices = []
                };
                ValidateLoadedDocument(migrated);
                EnsureMigrationBackup(
                    GetSchemaFourMigrationBackupPath(_filePath),
                    file);
                SaveAndCommit(migrated);
            }
            else if (document.SchemaVersion == 5)
            {
                var migrated = document with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    CloudflareNamedTunnelToken = null,
                    CloudflaredInstallationReceipt = null
                };
                ValidateLoadedDocument(migrated);
                EnsureMigrationBackup(
                    GetSchemaFiveMigrationBackupPath(_filePath),
                    file);
                SaveAndCommit(migrated);
            }
            else if (document.SchemaVersion == 6)
            {
                var migrated = document with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    CloudflaredInstallationReceipt = null
                };
                ValidateLoadedDocument(migrated);
                EnsureMigrationBackup(
                    GetSchemaSixMigrationBackupPath(_filePath),
                    file);
                SaveAndCommit(migrated);
            }
            else
            {
                _document = document;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            IsAvailable = false;
            AvailabilityError =
                "遠端安全資料無法由目前 Windows 使用者解密或檔案已損毀；為保護帳號，遠端登入已停止。";
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (file is not null) CryptographicOperations.ZeroMemory(file);
        }
    }

    internal static string GetLegacyMigrationBackupPath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("遠端安全檔案沒有有效的父資料夾。");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.schema2-migration-backup");
    }

    internal static string GetSchemaThreeMigrationBackupPath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("遠端安全檔案沒有有效的父資料夾。");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.schema3-migration-backup");
    }

    internal static string GetSchemaFourMigrationBackupPath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("遠端安全檔案沒有有效的父資料夾。");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.schema4-migration-backup");
    }

    internal static string GetSchemaFiveMigrationBackupPath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("遠端安全檔案沒有有效的父資料夾。");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.schema5-migration-backup");
    }

    internal static string GetSchemaSixMigrationBackupPath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("遠端安全檔案沒有有效的父資料夾。");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.schema6-migration-backup");
    }

    private static void EnsureMigrationBackup(
        string backupPath,
        ReadOnlySpan<byte> originalProtectedVault)
    {
        if (File.Exists(backupPath))
        {
            ValidateMigrationBackup(backupPath, originalProtectedVault);
            return;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                backupPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
        }
        catch (IOException) when (File.Exists(backupPath))
        {
            // Another startup may have won CreateNew. Never overwrite it; only accept the
            // artifact if it is byte-for-byte the same protected legacy vault.
            ValidateMigrationBackup(backupPath, originalProtectedVault);
            return;
        }

        try
        {
            using (stream)
            {
                stream.Write(originalProtectedVault);
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            try
            {
                File.Delete(backupPath);
            }
            catch
            {
                // Preserve the primary failure; a partial artifact is fail-closed on retry.
            }

            throw;
        }
    }

    private static void ValidateMigrationBackup(
        string backupPath,
        ReadOnlySpan<byte> expectedProtectedVault)
    {
        byte[]? existing = null;
        try
        {
            var info = new FileInfo(backupPath);
            if (info.Length != expectedProtectedVault.Length || info.Length > MaximumFileBytes)
            {
                throw new InvalidDataException(
                    "Existing remote-security migration backup does not match the legacy vault.");
            }

            existing = File.ReadAllBytes(backupPath);
            if (!CryptographicOperations.FixedTimeEquals(existing, expectedProtectedVault))
            {
                throw new InvalidDataException(
                    "Existing remote-security migration backup does not match the legacy vault.");
            }
        }
        finally
        {
            if (existing is not null)
            {
                CryptographicOperations.ZeroMemory(existing);
            }
        }
    }

    private static void ValidateLoadedDocument(VaultDocument document)
    {
        if (document.Smtp is { } smtp &&
            (!RemoteIdentity.IsCanonicalGmailLogin(smtp.SenderGmail) ||
             !TryNormalizeGoogleAppPassword(smtp.AppPassword, out _)))
        {
            throw new InvalidDataException("Stored SMTP credentials are invalid.");
        }

        IReadOnlyList<CredentialRecord> credentials;
        if (document.SchemaVersion is 1 or 2)
        {
            if (document.Credentials is not null)
            {
                throw new InvalidDataException("Legacy remote security document contains an unexpected account list.");
            }

            credentials = document.Credential is null ? [] : [document.Credential];
        }
        else
        {
            if (document.Credential is not null ||
                document.Credentials is null ||
                document.Credentials.Count > MaximumAccounts)
            {
                throw new InvalidDataException("Stored remote account list is invalid.");
            }

            credentials = document.Credentials;
        }

        var usernames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credential in credentials)
        {
            var validIdentity = document.SchemaVersion == 1
                ? credential.Gmail is not null &&
                  credential.EmailVerifiedAtUtc is not null &&
                  RemoteIdentity.IsCanonicalGmailLogin(credential.Gmail)
                : credential.Gmail is null
                    ? credential.EmailVerifiedAtUtc is null
                    : credential.EmailVerifiedAtUtc is not null &&
                      RemoteIdentity.IsCanonicalGmailLogin(credential.Gmail);
            if (!validIdentity ||
                !RemoteCredentialRules.TryNormalizeUsername(credential.Username, out var normalized) ||
                !string.Equals(normalized, credential.Username, StringComparison.Ordinal) ||
                !usernames.Add(credential.Username) ||
                credential.Iterations is < 100_000 or > 2_000_000 ||
                Convert.FromBase64String(credential.Salt).Length != SaltBytes ||
                Convert.FromBase64String(credential.Verifier).Length != VerifierBytes ||
                credential.FailedAttempts is < 0 or > 4 ||
                credential.LockoutLevel is < 0 or > 4 ||
                (document.SchemaVersion >= 2 &&
                 (credential.Permissions & ~RemoteWebPermission.All) != 0))
            {
                throw new InvalidDataException("Stored remote credential is invalid.");
            }

            if (document.SchemaVersion <= 3)
            {
                if (credential.RecoverablePinCiphertext is not null)
                {
                    throw new InvalidDataException(
                        "Legacy remote credential contains unsupported recoverable data.");
                }
            }
            else if (credential.RecoverablePinCiphertext is { } recoverablePinCiphertext)
            {
                byte[]? plaintext = null;
                try
                {
                    // Validation deliberately stays byte-only; startup must not materialize
                    // every stored PIN as an immutable managed string.
                    plaintext = UnprotectRecoverablePinBytes(
                        recoverablePinCiphertext,
                        credential.Username);
                }
                catch (Exception exception) when (
                    exception is CryptographicException or FormatException or InvalidDataException)
                {
                    throw new InvalidDataException(
                        "Stored recoverable remote credential is invalid.",
                        exception);
                }
                finally
                {
                    if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }

        if (document.SchemaVersion <= 4)
        {
            if (document.DeviceTokenKey is not null ||
                document.Devices is not null ||
                document.CloudflareNamedTunnelToken is not null ||
                document.CloudflaredInstallationReceipt is not null)
            {
                throw new InvalidDataException(
                    "Legacy remote security document contains unsupported security data.");
            }

            return;
        }

        byte[]? deviceMasterKey = null;
        try
        {
            deviceMasterKey = Convert.FromBase64String(
                document.DeviceTokenKey
                ?? throw new InvalidDataException("Remembered-device master key is missing."));
            if (deviceMasterKey.Length != DeviceMasterKeyBytes ||
                document.Devices is null ||
                document.Devices.Count > MaximumRememberedDevices)
            {
                throw new InvalidDataException("Stored remembered-device data is invalid.");
            }
        }
        finally
        {
            if (deviceMasterKey is not null)
            {
                CryptographicOperations.ZeroMemory(deviceMasterKey);
            }
        }

        var deviceIds = new HashSet<Guid>();
        foreach (var device in document.Devices)
        {
            var credentialExists = usernames.Contains(device.Username);
            var validLabel = string.Equals(
                device.Label,
                device.Label.Trim(),
                StringComparison.Ordinal) &&
                             device.Label.Length is >= 1 and <= MaximumDeviceLabelCharacters &&
                             !device.Label.Any(character => char.IsControl(character));
            var validRevocation = device.RevokedAtUtc is null
                ? device.RevocationReason is null
                : device.RevocationReason is { Length: >= 1 and <= 64 } &&
                  device.RevokedAtUtc >= device.CreatedAtUtc;
            byte[]? deviceSalt = null;
            try
            {
                deviceSalt = Convert.FromBase64String(device.DeviceSalt);
                if (device.DeviceId == Guid.Empty ||
                    !deviceIds.Add(device.DeviceId) ||
                    !credentialExists ||
                    !validLabel ||
                    deviceSalt.Length != DeviceSaltBytes ||
                    device.CreatedAtUtc > device.LastUsedAtUtc ||
                    device.LastUsedAtUtc > device.IdleExpiresAtUtc ||
                    device.IdleExpiresAtUtc > device.AbsoluteExpiresAtUtc ||
                    device.CreatedAtUtc >= device.AbsoluteExpiresAtUtc ||
                    (device.LastRefreshRequestId is null) !=
                    (device.LastRotatedAtUtc is null) ||
                    device.LastRotatedAtUtc < device.CreatedAtUtc ||
                    !validRevocation)
                {
                    throw new InvalidDataException("Stored remembered device is invalid.");
                }
            }
            finally
            {
                if (deviceSalt is not null)
                {
                    CryptographicOperations.ZeroMemory(deviceSalt);
                }
            }
        }

        if (document.Devices
            .Where(device => device.RevokedAtUtc is null)
            .GroupBy(device => device.Username, StringComparer.Ordinal)
            .Any(group => group.Count() > MaximumRememberedDevicesPerAccount))
        {
            throw new InvalidDataException("A remote account has too many remembered devices.");
        }


        if (document.SchemaVersion == 5)
        {
            if (document.CloudflareNamedTunnelToken is not null ||
                document.CloudflaredInstallationReceipt is not null)
            {
                throw new InvalidDataException(
                    "Schema-5 remote security document contains unsupported Named Tunnel data.");
            }

            return;
        }

        if (document.CloudflareNamedTunnelToken is { } namedTunnelToken &&
            (!TryNormalizeCloudflareNamedTunnelToken(namedTunnelToken, out var normalizedToken) ||
             !string.Equals(namedTunnelToken, normalizedToken, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Stored Cloudflare Named Tunnel token is invalid or not canonical.");
        }

        if (document.SchemaVersion == 6)
        {
            if (document.CloudflaredInstallationReceipt is not null)
            {
                throw new InvalidDataException(
                    "Schema-6 remote security document contains a cloudflared installation receipt.");
            }

            return;
        }

        if (document.CloudflaredInstallationReceipt is { } receipt &&
            !CloudflaredInstallationReceipt.IsValid(receipt))
        {
            throw new InvalidDataException("Stored cloudflared installation receipt is invalid.");
        }
    }

    private void SaveAndCommit(VaultDocument updated)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("遠端安全檔案沒有有效的父資料夾。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        byte[]? plaintext = null;
        byte[]? encrypted = null;
        try
        {
            // DPAPI itself can fail. Keep serialized SMTP and account material inside this
            // zeroization boundary so that failure cannot leave plaintext buffers uncleared.
            plaintext = JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions);
            encrypted = ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(FileHeader);
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            _document = updated;
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(AvailabilityError ?? "遠端安全資料目前無法使用。");
        }
    }

    private static byte[] DeriveVerifier(string pin, byte[] salt, int iterations)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(pin);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                VerifierBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static string ProtectRecoverablePin(string pin, string normalizedUsername)
    {
        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? ciphertext = null;
        try
        {
            plaintext = Encoding.UTF8.GetBytes(pin);
            entropy = DeriveRecoverablePinEntropy(normalizedUsername);
            ciphertext = ProtectedData.Protect(
                plaintext,
                entropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(ciphertext);
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (entropy is not null) CryptographicOperations.ZeroMemory(entropy);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static byte[] UnprotectRecoverablePinBytes(
        string recoverablePinCiphertext,
        string normalizedUsername)
    {
        if (string.IsNullOrWhiteSpace(recoverablePinCiphertext) ||
            recoverablePinCiphertext.Length > 4096)
        {
            throw new InvalidDataException("Recoverable remote credential has an invalid size.");
        }

        byte[]? ciphertext = null;
        byte[]? entropy = null;
        byte[]? plaintext = null;
        var returnPlaintext = false;
        try
        {
            ciphertext = Convert.FromBase64String(recoverablePinCiphertext);
            entropy = DeriveRecoverablePinEntropy(normalizedUsername);
            plaintext = ProtectedData.Unprotect(
                ciphertext,
                entropy,
                DataProtectionScope.CurrentUser);
            if (plaintext.Length is < 4 or > 12 ||
                plaintext.Any(value => value is < (byte)'0' or > (byte)'9'))
            {
                throw new InvalidDataException("Recoverable remote credential has an invalid value.");
            }

            returnPlaintext = true;
            return plaintext;
        }
        finally
        {
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (entropy is not null) CryptographicOperations.ZeroMemory(entropy);
            if (!returnPlaintext && plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static byte[] DeriveRecoverablePinEntropy(string normalizedUsername)
    {
        byte[]? usernameBytes = null;
        byte[]? material = null;
        try
        {
            usernameBytes = Encoding.UTF8.GetBytes(normalizedUsername);
            material = GC.AllocateUninitializedArray<byte>(
                RecoverablePinEntropyDomain.Length + usernameBytes.Length);
            RecoverablePinEntropyDomain.CopyTo(material, 0);
            usernameBytes.CopyTo(material, RecoverablePinEntropyDomain.Length);
            return SHA256.HashData(material);
        }
        finally
        {
            if (usernameBytes is not null) CryptographicOperations.ZeroMemory(usernameBytes);
            if (material is not null) CryptographicOperations.ZeroMemory(material);
        }
    }

    private static bool CredentialBelongsToSubject(CredentialRecord? credential, string subject)
        => credential is not null &&
           (string.Equals(
                subject,
                RemoteControlOptions.PublicTunnelCredentialSubject,
                StringComparison.Ordinal)
               ? credential.Gmail is null
               : credential.Gmail is not null &&
                 string.Equals(credential.Gmail, subject, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<CredentialRecord> GetCredentials(VaultDocument document)
        => document.Credentials
           ?? throw new InvalidOperationException("遠端安全帳號清單尚未完成遷移。");

    private static CredentialRecord? FindCredential(VaultDocument document, string username)
        => GetCredentials(document).FirstOrDefault(credential =>
            string.Equals(credential.Username, username, StringComparison.Ordinal));

    private static IReadOnlyList<RememberedDeviceRecord> GetDevices(VaultDocument document)
        => document.Devices
           ?? throw new InvalidOperationException("記住的遠端裝置清單尚未完成遷移。");

    private static VaultDocument ReplaceCredential(
        VaultDocument document,
        CredentialRecord replacement)
        => document with
        {
            Credentials = GetCredentials(document)
                .Select(credential => string.Equals(
                    credential.Username,
                    replacement.Username,
                    StringComparison.Ordinal)
                    ? replacement
                    : credential)
                .ToArray()
        };

    private static VaultDocument ReplaceDevice(
        VaultDocument document,
        RememberedDeviceRecord replacement)
        => document with
        {
            Devices = GetDevices(document)
                .Select(device => device.DeviceId == replacement.DeviceId
                    ? replacement
                    : device)
                .ToArray()
        };

    private static VaultDocument RevokeDevices(
        VaultDocument document,
        Func<RememberedDeviceRecord, bool> predicate,
        DateTimeOffset now,
        string reason,
        out int count)
    {
        count = 0;
        var devices = new List<RememberedDeviceRecord>(GetDevices(document).Count);
        foreach (var device in GetDevices(document))
        {
            if (device.RevokedAtUtc is null && predicate(device))
            {
                devices.Add(RevokeDeviceRecord(device, now, reason));
                count++;
            }
            else
            {
                devices.Add(device);
            }
        }
        return count == 0 ? document : document with { Devices = devices };
    }

    private static RememberedDeviceRecord RevokeDeviceRecord(
        RememberedDeviceRecord device,
        DateTimeOffset now,
        string reason)
        => device with
        {
            RevokedAtUtc = now,
            RevocationReason = reason
        };

    private static bool IsDeviceExpired(RememberedDeviceRecord device, DateTimeOffset now)
        => device.IdleExpiresAtUtc <= now || device.AbsoluteExpiresAtUtc <= now;

    private static RemoteRememberedDeviceStatus GetDeviceStatus(
        RememberedDeviceRecord device,
        DateTimeOffset now)
        => device.RevokedAtUtc is not null
            ? RemoteRememberedDeviceStatus.Revoked
            : IsDeviceExpired(device, now)
                ? RemoteRememberedDeviceStatus.Expired
                : RemoteRememberedDeviceStatus.Active;

    private static RemoteRememberedDeviceInfo ToRememberedDeviceInfo(
        RememberedDeviceRecord device,
        DateTimeOffset now)
        => new(
            device.DeviceId,
            device.Username,
            device.Label,
            device.CreatedAtUtc,
            device.LastUsedAtUtc,
            device.IdleExpiresAtUtc,
            device.AbsoluteExpiresAtUtc,
            GetDeviceStatus(device, now),
            device.RevokedAtUtc,
            device.RevocationReason);

    private static RemoteRememberedDeviceRefreshResult CreateRememberedDeviceRefreshSuccess(
        VaultDocument document,
        RememberedDeviceRecord device,
        RemoteWebPermission permissions,
        DateTimeOffset now)
        => new(
            RemoteRememberedDeviceRefreshStatus.Success,
            CreateRememberedDeviceToken(document, device),
            ToRememberedDeviceInfo(device, now),
            device.Username,
            permissions);

    private static string CreateRememberedDeviceToken(
        VaultDocument document,
        RememberedDeviceRecord device)
    {
        var secret = DeriveRememberedDeviceSecret(
            document,
            device,
            device.TokenGeneration);
        try
        {
            return RememberedDeviceTokenCodec.Create(
                device.DeviceId,
                device.TokenGeneration,
                secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static bool RememberedDeviceSecretMatches(
        VaultDocument document,
        RememberedDeviceRecord device,
        ulong generation,
        ReadOnlySpan<byte> suppliedSecret)
    {
        var expected = DeriveRememberedDeviceSecret(document, device, generation);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, suppliedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static byte[] DeriveRememberedDeviceSecret(
        VaultDocument document,
        RememberedDeviceRecord device,
        ulong generation)
    {
        byte[]? masterKey = null;
        byte[]? salt = null;
        byte[]? deviceIdBytes = null;
        byte[]? material = null;
        try
        {
            masterKey = Convert.FromBase64String(
                document.DeviceTokenKey
                ?? throw new InvalidOperationException("記住裝置的主密鑰不存在。"));
            salt = Convert.FromBase64String(device.DeviceSalt);
            deviceIdBytes = device.DeviceId.ToByteArray();
            material = GC.AllocateUninitializedArray<byte>(
                RememberedDeviceSecretDomain.Length +
                deviceIdBytes.Length +
                sizeof(ulong) +
                salt.Length);
            var offset = 0;
            RememberedDeviceSecretDomain.CopyTo(material, offset);
            offset += RememberedDeviceSecretDomain.Length;
            deviceIdBytes.CopyTo(material, offset);
            offset += deviceIdBytes.Length;
            BinaryPrimitives.WriteUInt64BigEndian(
                material.AsSpan(offset, sizeof(ulong)),
                generation);
            offset += sizeof(ulong);
            salt.CopyTo(material, offset);
            return HMACSHA256.HashData(masterKey, material);
        }
        finally
        {
            if (masterKey is not null) CryptographicOperations.ZeroMemory(masterKey);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (deviceIdBytes is not null) CryptographicOperations.ZeroMemory(deviceIdBytes);
            if (material is not null) CryptographicOperations.ZeroMemory(material);
        }
    }

    private static string CreateDeviceMasterKey()
    {
        var key = RandomNumberGenerator.GetBytes(DeviceMasterKeyBytes);
        try
        {
            return Convert.ToBase64String(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string NormalizeDeviceLabel(string label)
    {
        var normalized = label?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > MaximumDeviceLabelCharacters ||
            normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"裝置名稱必須是 1–{MaximumDeviceLabelCharacters} 個可顯示字元。");
        }

        return normalized;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
        => left <= right ? left : right;

    private static string NormalizeExistingUsername(string username)
    {
        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalized))
        {
            throw new InvalidOperationException("遠端帳號格式無效。");
        }

        return normalized;
    }

    private static RemoteApprovedAccount ToApprovedAccount(CredentialRecord credential)
        => new(
            credential.Username,
            credential.Gmail,
            credential.EmailVerifiedAtUtc,
            credential.CreatedAtUtc,
            credential.Permissions,
            credential.RecoverablePinCiphertext is not null);

    private static void ValidatePermissions(RemoteWebPermission permissions)
    {
        if ((permissions & ~RemoteWebPermission.All) != 0)
        {
            throw new InvalidOperationException("遠端帳號權限包含未知項目，已拒絕儲存。");
        }
    }

    private sealed record VaultDocument(
        int SchemaVersion,
        SmtpRecord? Smtp,
        CredentialRecord? Credential,
        IReadOnlyList<CredentialRecord>? Credentials = null,
        string? DeviceTokenKey = null,
        IReadOnlyList<RememberedDeviceRecord>? Devices = null,
        string? CloudflareNamedTunnelToken = null,
        CloudflaredInstallationReceipt? CloudflaredInstallationReceipt = null)
    {
        public static VaultDocument CreateEmpty()
            => new(
                CurrentSchemaVersion,
                null,
                null,
                [],
                CreateDeviceMasterKey(),
                [],
                null,
                null);
    }

    private sealed record SmtpRecord(string SenderGmail, string AppPassword);

    private sealed record CredentialRecord(
        string Username,
        string? Gmail,
        string Salt,
        string Verifier,
        int Iterations,
        DateTimeOffset? EmailVerifiedAtUtc,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? LastAuthenticatedAtUtc,
        int FailedAttempts,
        int LockoutLevel,
        DateTimeOffset? LockedUntilUtc,
        RemoteWebPermission Permissions = RemoteWebPermission.None,
        string? RecoverablePinCiphertext = null)
    {
        public override string ToString()
            => $"CredentialRecord {{ Username = {Username}, Gmail = {Gmail}, " +
               $"HasRecoverablePin = {RecoverablePinCiphertext is not null} }}";
    }

    private sealed record RememberedDeviceRecord(
        Guid DeviceId,
        string Username,
        string Label,
        string DeviceSalt,
        ulong TokenGeneration,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastUsedAtUtc,
        DateTimeOffset IdleExpiresAtUtc,
        DateTimeOffset AbsoluteExpiresAtUtc,
        Guid? LastRefreshRequestId,
        DateTimeOffset? LastRotatedAtUtc,
        DateTimeOffset? RevokedAtUtc,
        string? RevocationReason)
    {
        public override string ToString()
            => $"RememberedDeviceRecord {{ DeviceId = {DeviceId}, Username = {Username}, " +
               $"Label = {Label}, Generation = {TokenGeneration}, Revoked = {RevokedAtUtc is not null} }}";
    }
}

internal readonly record struct ParsedRememberedDeviceToken(
    Guid DeviceId,
    ulong Generation,
    byte[] Secret);

internal static class RememberedDeviceTokenCodec
{
    private const string Prefix = "mrd1";
    private const int SecretBytes = 32;
    private const int MaximumTokenCharacters = 128;

    public static string Create(Guid deviceId, ulong generation, ReadOnlySpan<byte> secret)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("A device id is required.", nameof(deviceId));
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException("A 32-byte device secret is required.", nameof(secret));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}.{deviceId:N}.{generation}.{EncodeBase64Url(secret)}");
    }

    public static bool TryParse(string? token, out ParsedRememberedDeviceToken parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenCharacters)
        {
            return false;
        }

        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 4 ||
            !string.Equals(parts[0], Prefix, StringComparison.Ordinal) ||
            parts[1].Length != 32 ||
            !Guid.TryParseExact(parts[1], "N", out var deviceId) ||
            deviceId == Guid.Empty ||
            !string.Equals(parts[1], deviceId.ToString("N"), StringComparison.Ordinal) ||
            !ulong.TryParse(
                parts[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var generation) ||
            !string.Equals(
                parts[2],
                generation.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            !TryDecodeBase64Url(parts[3], out var secret))
        {
            return false;
        }

        if (secret.Length != SecretBytes ||
            !string.Equals(parts[3], EncodeBase64Url(secret), StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(secret);
            return false;
        }

        parsed = new ParsedRememberedDeviceToken(deviceId, generation, secret);
        return true;
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryDecodeBase64Url(string value, out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrEmpty(value) ||
            value.Any(character =>
                character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        var standard = value.Replace('-', '+').Replace('_', '/');
        standard += (standard.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => "!"
        };
        try
        {
            decoded = Convert.FromBase64String(standard);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Non-persistent fallback used only by dependency-light coordinator tests.</summary>
internal sealed class EphemeralRemoteSecurityStore : IRemoteSecurityStore
{
    private const int MaximumRememberedDevicesPerAccount = 8;
    private const int MaximumRememberedDevices = 64;
    private static readonly TimeSpan RememberedDeviceIdleLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan RememberedDeviceAbsoluteLifetime = TimeSpan.FromDays(365);
    private readonly object _deviceGate = new();
    private readonly Dictionary<string, EphemeralCredential> _credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, EphemeralRememberedDevice> _devices = [];
    private readonly byte[] _deviceTokenKey = RandomNumberGenerator.GetBytes(32);
    private GmailSmtpCredential? _smtp;
    private string? _cloudflareNamedTunnelToken;
    private CloudflaredInstallationReceipt? _cloudflaredInstallationReceipt;

    public bool IsAvailable => true;
    public string? AvailabilityError => null;
    public string? SmtpSenderGmail => _smtp?.SenderGmail;
    public bool HasCloudflareNamedTunnelToken => _cloudflareNamedTunnelToken is not null;
    public bool HasCloudflaredInstallationReceipt => _cloudflaredInstallationReceipt is not null;
    public RemoteApprovedAccount? ApprovedAccount => ApprovedAccounts.FirstOrDefault();
    public IReadOnlyList<RemoteApprovedAccount> ApprovedAccounts
        => _credentials.Values.Select(value => value.Account).ToArray();

    public GmailSmtpCredential GetSmtpCredential()
        => _smtp ?? throw new InvalidOperationException("請先儲存 Gmail SMTP 寄件設定。");

    public void SaveSmtpCredential(string senderGmail, string appPassword)
        => _smtp = new GmailSmtpCredential(senderGmail, appPassword);

    public void DeleteSmtpCredential()
        => _smtp = null;

    public CloudflareNamedTunnelCredential GetCloudflareNamedTunnelCredential()
        => _cloudflareNamedTunnelToken is { } token
            ? new CloudflareNamedTunnelCredential(token)
            : throw new InvalidOperationException("請先儲存 Cloudflare Named Tunnel Token。");

    public void SaveCloudflareNamedTunnelToken(string token)
    {
        if (!RemoteSecurityStore.TryNormalizeCloudflareNamedTunnelToken(token, out var normalized))
        {
            throw new InvalidOperationException("Cloudflare Named Tunnel Token 格式無效。");
        }

        _cloudflareNamedTunnelToken = normalized;
    }

    public void DeleteCloudflareNamedTunnelToken()
        => _cloudflareNamedTunnelToken = null;

    public CloudflaredInstallationReceipt GetCloudflaredInstallationReceipt()
        => _cloudflaredInstallationReceipt
           ?? throw new InvalidOperationException(
               "請先以 MCSV 安全下載 cloudflared.exe 並建立安裝收據。");

    public void SaveCloudflaredInstallationReceipt(CloudflaredInstallationReceipt receipt)
    {
        CloudflaredInstallationReceipt.ValidateAndThrow(receipt);
        _cloudflaredInstallationReceipt = receipt;
    }

    public void DeleteCloudflaredInstallationReceipt()
        => _cloudflaredInstallationReceipt = null;

    public void RegisterAccount(
        string? verifiedGmail,
        string username,
        string pin,
        RemoteWebPermission permissions = RemoteWebPermission.All)
    {
        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalized))
        {
            throw new InvalidOperationException("帳號格式無效。");
        }

        if (_credentials.ContainsKey(normalized))
        {
            throw new InvalidOperationException("此遠端帳號名稱已存在。");
        }

        var account = new RemoteApprovedAccount(
            normalized,
            verifiedGmail,
            verifiedGmail is null ? null : DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            permissions,
            HasRecoverablePin: true);
        _credentials.Add(normalized, new EphemeralCredential(account, pin));
    }

    public void UpdateAccountPermissions(string username, RemoteWebPermission permissions)
    {
        if ((permissions & ~RemoteWebPermission.All) != 0)
        {
            throw new InvalidOperationException("遠端帳號權限包含未知項目。");
        }

        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalized) ||
            !_credentials.TryGetValue(normalized, out var credential))
        {
            throw new InvalidOperationException("找不到指定的遠端帳號。");
        }

        _credentials[normalized] = credential with
        {
            Account = credential.Account with { Permissions = permissions }
        };
    }

    public void ResetAccountPin(string username, string newPin)
    {
        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalized) ||
            !_credentials.TryGetValue(normalized, out var credential))
        {
            throw new InvalidOperationException("找不到指定的遠端帳號。");
        }

        if (!RemoteCredentialRules.IsValidPin(newPin))
        {
            throw new InvalidOperationException("密碼格式無效。");
        }

        _credentials[normalized] = credential with
        {
            Account = credential.Account with { HasRecoverablePin = true },
            Pin = newPin
        };
        RevokeRememberedDevicesForAccount(normalized);
    }

    public void DeleteAccount(string username)
    {
        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalized) ||
            !_credentials.Remove(normalized))
        {
            throw new InvalidOperationException("找不到指定的遠端帳號。");
        }

        lock (_deviceGate)
        {
            foreach (var deviceId in _devices.Values
                         .Where(device => string.Equals(
                             device.Username,
                             normalized,
                             StringComparison.Ordinal))
                         .Select(device => device.DeviceId)
                         .ToArray())
            {
                _devices.Remove(deviceId);
            }
        }
    }

    public string? GetRecoverablePin(string username)
    {
        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalized) ||
            !_credentials.TryGetValue(normalized, out var credential))
        {
            throw new InvalidOperationException("找不到指定的遠端帳號。");
        }

        return credential.Pin;
    }

    public IssuedRemoteRememberedDevice IssueRememberedDevice(
        string login,
        string username,
        string label)
    {
        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalized) ||
            !_credentials.TryGetValue(normalized, out var credential) ||
            !SubjectMatchesAccount(credential.Account, login))
        {
            throw new InvalidOperationException("找不到屬於目前連線身分的遠端帳號。");
        }

        var normalizedLabel = label?.Trim() ?? string.Empty;
        if (normalizedLabel.Length is < 1 or > 64 || normalizedLabel.Any(char.IsControl))
        {
            throw new InvalidOperationException("裝置名稱必須是 1–64 個可顯示字元。");
        }

        lock (_deviceGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var deviceId in _devices.Values
                         .Where(device =>
                             string.Equals(device.Username, normalized, StringComparison.Ordinal) &&
                             GetEphemeralStatus(device, now) != RemoteRememberedDeviceStatus.Active)
                         .Select(device => device.DeviceId)
                         .ToArray())
            {
                _devices.Remove(deviceId);
            }

            if (_devices.Values.Count(device =>
                    string.Equals(device.Username, normalized, StringComparison.Ordinal)) >=
                MaximumRememberedDevicesPerAccount)
            {
                throw new InvalidOperationException(
                    $"每個遠端帳號最多可記住 {MaximumRememberedDevicesPerAccount} 台裝置。");
            }

            if (_devices.Count >= MaximumRememberedDevices)
            {
                foreach (var deviceId in _devices.Values
                             .Where(device => GetEphemeralStatus(device, now) !=
                                              RemoteRememberedDeviceStatus.Active)
                             .Select(device => device.DeviceId)
                             .ToArray())
                {
                    _devices.Remove(deviceId);
                }
            }
            if (_devices.Count >= MaximumRememberedDevices)
            {
                throw new InvalidOperationException(
                    $"最多可保存 {MaximumRememberedDevices} 台遠端裝置。");
            }

            var absolute = now.Add(RememberedDeviceAbsoluteLifetime);
            var device = new EphemeralRememberedDevice(
                Guid.NewGuid(),
                normalized,
                normalizedLabel,
                RandomNumberGenerator.GetBytes(32),
                0,
                now,
                now,
                MinEphemeral(now.Add(RememberedDeviceIdleLifetime), absolute),
                absolute,
                null,
                null,
                null,
                null);
            _devices.Add(device.DeviceId, device);
            return new IssuedRemoteRememberedDevice(
                CreateEphemeralToken(device),
                ToEphemeralInfo(device, now),
                credential.Account.Permissions);
        }
    }

    public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
        string login,
        string token,
        Guid requestId)
    {
        if (requestId == Guid.Empty ||
            !RememberedDeviceTokenCodec.TryParse(token, out var parsed))
        {
            return new RemoteRememberedDeviceRefreshResult(
                RemoteRememberedDeviceRefreshStatus.Invalid);
        }

        try
        {
            lock (_deviceGate)
            {
                if (!_devices.TryGetValue(parsed.DeviceId, out var device) ||
                    !EphemeralSecretMatches(device, parsed.Generation, parsed.Secret) ||
                    !_credentials.TryGetValue(device.Username, out var credential) ||
                    !SubjectMatchesAccount(credential.Account, login))
                {
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Invalid);
                }

                var now = DateTimeOffset.UtcNow;
                if (device.RevokedAtUtc is not null)
                {
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Revoked);
                }
                if (device.IdleExpiresAtUtc <= now || device.AbsoluteExpiresAtUtc <= now)
                {
                    _devices[device.DeviceId] = device with
                    {
                        RevokedAtUtc = now,
                        RevocationReason = "expired"
                    };
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.Expired);
                }

                var sameRequest = device.LastRefreshRequestId == requestId;
                if (parsed.Generation == device.TokenGeneration)
                {
                    if (!sameRequest)
                    {
                        if (device.TokenGeneration == ulong.MaxValue)
                        {
                            _devices[device.DeviceId] = device with
                            {
                                RevokedAtUtc = now,
                                RevocationReason = "generation-exhausted"
                            };
                            return new RemoteRememberedDeviceRefreshResult(
                                RemoteRememberedDeviceRefreshStatus.Revoked);
                        }

                        device = device with
                        {
                            TokenGeneration = device.TokenGeneration + 1,
                            LastRefreshRequestId = requestId,
                            LastRotatedAtUtc = now,
                            LastUsedAtUtc = now,
                            IdleExpiresAtUtc = MinEphemeral(
                                now.Add(RememberedDeviceIdleLifetime),
                                device.AbsoluteExpiresAtUtc)
                        };
                        _devices[device.DeviceId] = device;
                    }

                    return EphemeralRefreshSuccess(device, credential.Account.Permissions, now);
                }

                if (device.TokenGeneration > 0 &&
                    parsed.Generation == device.TokenGeneration - 1 &&
                    sameRequest)
                {
                    return EphemeralRefreshSuccess(device, credential.Account.Permissions, now);
                }

                if (parsed.Generation < device.TokenGeneration)
                {
                    _devices[device.DeviceId] = device with
                    {
                        RevokedAtUtc = now,
                        RevocationReason = "replay-detected"
                    };
                    return new RemoteRememberedDeviceRefreshResult(
                        RemoteRememberedDeviceRefreshStatus.ReplayDetected);
                }

                return new RemoteRememberedDeviceRefreshResult(
                    RemoteRememberedDeviceRefreshStatus.Invalid);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices()
    {
        lock (_deviceGate)
        {
            var now = DateTimeOffset.UtcNow;
            return _devices.Values
                .OrderByDescending(device => device.LastUsedAtUtc)
                .Select(device => ToEphemeralInfo(device, now))
                .ToArray();
        }
    }

    public bool RevokeRememberedDevice(string login, string token)
    {
        if (!RememberedDeviceTokenCodec.TryParse(token, out var parsed)) return false;
        try
        {
            lock (_deviceGate)
            {
                if (!_devices.TryGetValue(parsed.DeviceId, out var device) ||
                    !EphemeralSecretMatches(device, parsed.Generation, parsed.Secret) ||
                    !_credentials.TryGetValue(device.Username, out var credential) ||
                    !SubjectMatchesAccount(credential.Account, login) ||
                    device.RevokedAtUtc is not null)
                {
                    return false;
                }

                _devices[device.DeviceId] = device with
                {
                    RevokedAtUtc = DateTimeOffset.UtcNow,
                    RevocationReason = "device-signout"
                };
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    public bool RevokeRememberedDevice(Guid deviceId)
    {
        lock (_deviceGate)
        {
            if (!_devices.TryGetValue(deviceId, out var device) ||
                device.RevokedAtUtc is not null)
            {
                return false;
            }

            _devices[deviceId] = device with
            {
                RevokedAtUtc = DateTimeOffset.UtcNow,
                RevocationReason = "desktop-revoked"
            };
            return true;
        }
    }

    public int RevokeRememberedDevicesForAccount(string username)
    {
        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalized))
        {
            throw new InvalidOperationException("遠端帳號格式無效。");
        }

        lock (_deviceGate)
        {
            return RevokeEphemeralDevices(
                device => string.Equals(device.Username, normalized, StringComparison.Ordinal),
                "account-revoked");
        }
    }

    public int RevokeAllRememberedDevices()
    {
        lock (_deviceGate)
        {
            return RevokeEphemeralDevices(static _ => true, "all-devices-revoked");
        }
    }

    public bool HasCredentialForLogin(string tailscaleLogin)
        => _credentials.Values.Any(credential =>
            SubjectMatchesAccount(credential.Account, tailscaleLogin));

    public RemoteCredentialAuthenticationResult Authenticate(
        string tailscaleLogin,
        string username,
        string pin)
        => _credentials.TryGetValue(username, out var credential) &&
           SubjectMatchesAccount(credential.Account, tailscaleLogin) &&
           string.Equals(credential.Pin, pin, StringComparison.Ordinal)
            ? new RemoteCredentialAuthenticationResult(
                RemoteCredentialAuthenticationStatus.Success,
                credential.Account.Username,
                Permissions: credential.Account.Permissions)
            : new RemoteCredentialAuthenticationResult(
                RemoteCredentialAuthenticationStatus.InvalidCredentials);

    private static bool SubjectMatchesAccount(RemoteApprovedAccount account, string subject)
        => string.Equals(
               subject,
               RemoteControlOptions.QuickTunnelCredentialSubject,
               StringComparison.Ordinal)
            ? account.Gmail is null
            : account.Gmail is not null &&
              string.Equals(account.Gmail, subject, StringComparison.OrdinalIgnoreCase);

    private int RevokeEphemeralDevices(
        Func<EphemeralRememberedDevice, bool> predicate,
        string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var device in _devices.Values.ToArray())
        {
            if (device.RevokedAtUtc is not null || !predicate(device)) continue;
            _devices[device.DeviceId] = device with
            {
                RevokedAtUtc = now,
                RevocationReason = reason
            };
            count++;
        }

        return count;
    }

    private RemoteRememberedDeviceRefreshResult EphemeralRefreshSuccess(
        EphemeralRememberedDevice device,
        RemoteWebPermission permissions,
        DateTimeOffset now)
        => new(
            RemoteRememberedDeviceRefreshStatus.Success,
            CreateEphemeralToken(device),
            ToEphemeralInfo(device, now),
            device.Username,
            permissions);

    private string CreateEphemeralToken(EphemeralRememberedDevice device)
    {
        var secret = DeriveEphemeralSecret(device, device.TokenGeneration);
        try
        {
            return RememberedDeviceTokenCodec.Create(
                device.DeviceId,
                device.TokenGeneration,
                secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private bool EphemeralSecretMatches(
        EphemeralRememberedDevice device,
        ulong generation,
        ReadOnlySpan<byte> supplied)
    {
        var expected = DeriveEphemeralSecret(device, generation);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private byte[] DeriveEphemeralSecret(EphemeralRememberedDevice device, ulong generation)
    {
        var id = device.DeviceId.ToByteArray();
        var material = GC.AllocateUninitializedArray<byte>(id.Length + sizeof(ulong) + device.Salt.Length);
        try
        {
            id.CopyTo(material, 0);
            BinaryPrimitives.WriteUInt64BigEndian(material.AsSpan(id.Length, sizeof(ulong)), generation);
            device.Salt.CopyTo(material, id.Length + sizeof(ulong));
            return HMACSHA256.HashData(_deviceTokenKey, material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(id);
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static RemoteRememberedDeviceInfo ToEphemeralInfo(
        EphemeralRememberedDevice device,
        DateTimeOffset now)
        => new(
            device.DeviceId,
            device.Username,
            device.Label,
            device.CreatedAtUtc,
            device.LastUsedAtUtc,
            device.IdleExpiresAtUtc,
            device.AbsoluteExpiresAtUtc,
            GetEphemeralStatus(device, now),
            device.RevokedAtUtc,
            device.RevocationReason);

    private static RemoteRememberedDeviceStatus GetEphemeralStatus(
        EphemeralRememberedDevice device,
        DateTimeOffset now)
        => device.RevokedAtUtc is not null
            ? RemoteRememberedDeviceStatus.Revoked
            : device.IdleExpiresAtUtc <= now || device.AbsoluteExpiresAtUtc <= now
                ? RemoteRememberedDeviceStatus.Expired
                : RemoteRememberedDeviceStatus.Active;

    private static DateTimeOffset MinEphemeral(DateTimeOffset left, DateTimeOffset right)
        => left <= right ? left : right;

    private sealed record EphemeralCredential(RemoteApprovedAccount Account, string Pin)
    {
        public override string ToString()
            => $"EphemeralCredential {{ Username = {Account.Username} }}";
    }

    private sealed record EphemeralRememberedDevice(
        Guid DeviceId,
        string Username,
        string Label,
        byte[] Salt,
        ulong TokenGeneration,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastUsedAtUtc,
        DateTimeOffset IdleExpiresAtUtc,
        DateTimeOffset AbsoluteExpiresAtUtc,
        Guid? LastRefreshRequestId,
        DateTimeOffset? LastRotatedAtUtc,
        DateTimeOffset? RevokedAtUtc,
        string? RevocationReason);
}
