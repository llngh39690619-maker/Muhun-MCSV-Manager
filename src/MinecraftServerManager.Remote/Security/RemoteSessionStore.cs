using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace MinecraftServerManager.Remote;

public sealed record IssuedRemoteSession(
    string SessionToken,
    string CsrfToken,
    string Login,
    DateTimeOffset ExpiresAtUtc,
    string? Username = null,
    RemoteWebPermission Permissions = RemoteWebPermission.All,
    RemoteAuthorizationSnapshot? Authorization = null);

public sealed record ValidatedRemoteSession(
    Guid SessionId,
    string Login,
    string CsrfToken,
    DateTimeOffset ExpiresAtUtc,
    string? Username = null,
    RemoteWebPermission Permissions = RemoteWebPermission.All,
    RemoteAuthorizationSnapshot? Authorization = null);

public sealed class RemoteSessionStore
{
    private const int SecretBytes = 32;
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _sessionLifetime;
    private readonly int _maximumSessions;
    private readonly TimeProvider _timeProvider;
    private long _generation;

    public RemoteSessionStore(RemoteControlOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        RemoteControlOptionsValidator.ValidateAndThrow(options);
        _sessionLifetime = options.SessionLifetime;
        _maximumSessions = options.MaximumSessions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IssuedRemoteSession Issue(
        string login,
        string? username = null,
        RemoteWebPermission permissions = RemoteWebPermission.All,
        RemoteAuthorizationSnapshot? authorization = null)
    {
        lock (_gate)
        {
            return IssueCore(login, username, permissions, authorization);
        }
    }

    public long CaptureGeneration()
    {
        lock (_gate)
        {
            return _generation;
        }
    }

    public bool TryIssueIfGenerationUnchanged(
        string login,
        string? username,
        RemoteWebPermission permissions,
        RemoteAuthorizationSnapshot? authorization,
        long expectedGeneration,
        out IssuedRemoteSession session)
    {
        lock (_gate)
        {
            if (_generation != expectedGeneration)
            {
                session = default!;
                return false;
            }

            session = IssueCore(login, username, permissions, authorization);
            return true;
        }
    }

    public bool TryIssueIfGenerationUnchanged(
        string login,
        string? username,
        RemoteWebPermission permissions,
        long expectedGeneration,
        out IssuedRemoteSession session)
        => TryIssueIfGenerationUnchanged(
            login,
            username,
            permissions,
            authorization: null,
            expectedGeneration,
            out session);

    public bool TryIssueIfGenerationUnchanged(
        string login,
        string? username,
        long expectedGeneration,
        out IssuedRemoteSession session)
        => TryIssueIfGenerationUnchanged(
            login,
            username,
            RemoteWebPermission.All,
            authorization: null,
            expectedGeneration,
            out session);

    public bool TryValidate(string? sessionToken, string login, out ValidatedRemoteSession session)
    {
        session = default!;
        if (!IsSecretShapeValid(sessionToken) || !IsValidLoginSubject(login))
        {
            return false;
        }

        var key = HashForLookup(sessionToken!);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var stored))
            {
                return false;
            }

            if (stored.ExpiresAtUtc <= now)
            {
                _sessions.Remove(key);
                return false;
            }

            if (!string.Equals(stored.Login, login, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            session = new ValidatedRemoteSession(
                stored.SessionId,
                stored.Login,
                stored.CsrfToken,
                stored.ExpiresAtUtc,
                stored.Username,
                stored.Permissions,
                stored.Authorization);
            return true;
        }
    }

    public bool Revoke(string? sessionToken)
    {
        if (!IsSecretShapeValid(sessionToken))
        {
            return false;
        }

        lock (_gate)
        {
            return _sessions.Remove(HashForLookup(sessionToken!));
        }
    }

    public int RevokeForUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return 0;
        }

        lock (_gate)
        {
            var keys = _sessions
                .Where(pair => string.Equals(
                    pair.Value.Username,
                    username,
                    StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keys)
            {
                _sessions.Remove(key);
            }

            return keys.Length;
        }
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            unchecked
            {
                _generation++;
            }

            _sessions.Clear();
        }
    }

    public static bool CsrfMatches(ValidatedRemoteSession session, string? suppliedToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsSecretShapeValid(suppliedToken))
        {
            return false;
        }

        var expectedBytes = WebEncoders.Base64UrlDecode(session.CsrfToken);
        var suppliedBytes = WebEncoders.Base64UrlDecode(suppliedToken!);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(suppliedBytes);
        }
    }

    private static string CreateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            return WebEncoders.Base64UrlEncode(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private IssuedRemoteSession IssueCore(
        string login,
        string? username,
        RemoteWebPermission permissions,
        RemoteAuthorizationSnapshot? authorization)
    {
        if (!IsValidLoginSubject(login))
        {
            throw new ArgumentException("An approved credential subject is required.", nameof(login));
        }

        if ((permissions & ~RemoteWebPermission.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }

        if (authorization is not null &&
            !RemoteAuthorizationSnapshotValidator.TryCreateImmutable(authorization, out authorization))
        {
            throw new ArgumentException("Authorization snapshot is invalid.", nameof(authorization));
        }

        var sessionToken = CreateSecret();
        var csrfToken = CreateSecret();
        var now = _timeProvider.GetUtcNow();
        var expiresAtUtc = now.Add(_sessionLifetime);
        var key = HashForLookup(sessionToken);

        RemoveExpired(now);
        while (_sessions.Count >= _maximumSessions)
        {
            var oldestKey = _sessions.MinBy(pair => pair.Value.IssuedAtUtc).Key;
            _sessions.Remove(oldestKey);
        }

        _sessions.Add(
            key,
            new StoredSession(
                Guid.NewGuid(),
                login,
                username,
                csrfToken,
                now,
                expiresAtUtc,
                permissions,
                authorization));
        return new IssuedRemoteSession(
            sessionToken,
            csrfToken,
            login,
            expiresAtUtc,
            username,
            permissions,
            authorization);
    }

    private static bool IsValidLoginSubject(string? login)
        => string.Equals(
               login,
               RemoteControlOptions.QuickTunnelCredentialSubject,
               StringComparison.Ordinal)
           || RemoteIdentity.IsCanonicalGmailLogin(login);

    private static bool IsSecretShapeValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 43)
        {
            return false;
        }

        try
        {
            var decoded = WebEncoders.Base64UrlDecode(value);
            try
            {
                return decoded.Length == SecretBytes;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string HashForLookup(string token)
    {
        var bytes = WebEncoders.Base64UrlDecode(token);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _sessions.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _sessions.Remove(key);
        }
    }

    private sealed record StoredSession(
        Guid SessionId,
        string Login,
        string? Username,
        string CsrfToken,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        RemoteWebPermission Permissions,
        RemoteAuthorizationSnapshot? Authorization);
}
