using MinecraftServerManager.Remote.Contracts;
using MinecraftServerManager.Contracts.Security;

namespace MinecraftServerManager.Remote;

/// <summary>
/// Linearizes credential session issue, mutation acceptance, and revocation so
/// no sign-in or privileged operation can cross a completed desktop revocation.
/// </summary>
public sealed class RemoteAuthCoordinator
{
    private readonly object _gate = new();
    private readonly RemoteSessionStore _sessions;
    private readonly IRemoteCredentialStore _credentials;
    private readonly IRemoteRememberedDeviceStore _rememberedDevices;
    private readonly IRemoteAuthorizationStore? _authorizationStore;
    private int _credentialVerificationBusy;

    public RemoteAuthCoordinator(
        RemoteSessionStore sessions,
        IRemoteCredentialStore credentials,
        IRemoteRememberedDeviceStore? rememberedDevices = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _rememberedDevices = rememberedDevices ?? DenyAllRememberedDeviceStore.Instance;
        _authorizationStore = credentials as IRemoteAuthorizationStore;
    }

    public RemoteMutationAuthorizationStatus TryEnrollRememberedDevice(
        string? sessionToken,
        string login,
        string? csrfToken,
        string label,
        out IssuedRemoteRememberedDevice issued)
    {
        lock (_gate)
        {
            if (!TryValidateSession(sessionToken, login, out var session, out _) ||
                !RemoteSessionStore.CsrfMatches(session, csrfToken) ||
                string.IsNullOrWhiteSpace(session.Username))
            {
                issued = default!;
                return RemoteMutationAuthorizationStatus.Unauthorized;
            }

            issued = _rememberedDevices.IssueRememberedDevice(
                session.Login,
                session.Username,
                label);
            return RemoteMutationAuthorizationStatus.Accepted;
        }
    }

    public RemoteRememberedDeviceRefreshResult TryRefreshRememberedDevice(
        string login,
        string? token,
        Guid requestId,
        out IssuedRemoteSession session)
    {
        session = default!;
        if (string.IsNullOrWhiteSpace(token) || requestId == Guid.Empty)
        {
            return new RemoteRememberedDeviceRefreshResult(
                RemoteRememberedDeviceRefreshStatus.Invalid);
        }

        var generation = _sessions.CaptureGeneration();
        var result = _rememberedDevices.RefreshRememberedDevice(login, token, requestId);
        if (result.Status != RemoteRememberedDeviceRefreshStatus.Success ||
            string.IsNullOrWhiteSpace(result.Username))
        {
            return result;
        }

        if (!TryGetFormalAuthorization(login, result.Username, out var authorization))
        {
            return new RemoteRememberedDeviceRefreshResult(
                RemoteRememberedDeviceRefreshStatus.Invalid);
        }

        lock (_gate)
        {
            _ = _sessions.TryIssueIfGenerationUnchanged(
                login,
                result.Username,
                result.Permissions,
                authorization,
                generation,
                out session);
        }

        return result;
    }

    public bool RevokeRememberedDevice(string login, string? token)
        => !string.IsNullOrWhiteSpace(token) &&
           _rememberedDevices.RevokeRememberedDevice(login, token);

    /// <summary>
    /// Revokes the persistent credential before invalidating sessions. If the durable revoke
    /// fails, no session state is changed so the same browser can safely retry sign-out. The
    /// final generation bump prevents a concurrent remembered-device refresh from issuing a
    /// replacement session after sign-out has completed.
    /// </summary>
    public bool SignOut(
        string login,
        string? sessionToken,
        string? rememberedDeviceToken)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(rememberedDeviceToken))
            {
                var revoked = _rememberedDevices.RevokeRememberedDevice(
                    login,
                    rememberedDeviceToken);
                _sessions.RevokeAll();
                return revoked;
            }

            return _sessions.Revoke(sessionToken);
        }
    }

    public bool HasCredentialForLogin(string login)
        => _credentials.HasCredentialForLogin(login);

    public RemoteCredentialAuthenticationResult TryLogin(
        string? username,
        string? pin,
        string login,
        out IssuedRemoteSession session)
    {
        session = default!;
        if (!RemoteCredentialRules.TryNormalizeUsername(username, out var normalizedUsername) ||
            !RemoteCredentialRules.IsValidPin(pin))
        {
            return new RemoteCredentialAuthenticationResult(
                RemoteCredentialAuthenticationStatus.InvalidCredentials);
        }

        // A PIN KDF is intentionally expensive. Never let concurrent login attempts
        // compete with Minecraft servers for every available CPU core.
        if (Interlocked.CompareExchange(ref _credentialVerificationBusy, 1, 0) != 0)
        {
            return new RemoteCredentialAuthenticationResult(
                RemoteCredentialAuthenticationStatus.LockedOut,
                LockedUntilUtc: DateTimeOffset.UtcNow.AddSeconds(2));
        }

        try
        {
            var generation = _sessions.CaptureGeneration();
            var result = _credentials.Authenticate(login, normalizedUsername, pin!);
            if (result.Status != RemoteCredentialAuthenticationStatus.Success ||
                string.IsNullOrWhiteSpace(result.Username))
            {
                return result;
            }

            if (!TryGetFormalAuthorization(login, result.Username, out var authorization))
            {
                return new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);
            }

            lock (_gate)
            {
                if (!_sessions.TryIssueIfGenerationUnchanged(
                        login,
                        result.Username,
                        result.Permissions,
                        authorization,
                        generation,
                        out session))
                {
                    return new RemoteCredentialAuthenticationResult(
                        RemoteCredentialAuthenticationStatus.InvalidCredentials);
                }
            }

            return result;
        }
        finally
        {
            Volatile.Write(ref _credentialVerificationBusy, 0);
        }
    }

    /// <summary>
    /// Revalidates a mutation and invokes its acceptance callback while revocation is
    /// excluded. The callback must synchronously register the operation before it returns.
    /// This gives desktop revocation a precise boundary: an operation is either accepted
    /// before revocation, or it cannot start after revocation has returned.
    /// </summary>
    public RemoteMutationAuthorizationStatus TryAcceptMutation<TAcceptance>(
        string? sessionToken,
        string login,
        string? csrfToken,
        RemoteWebPermission requiredPermission,
        Func<Guid, TAcceptance> accept,
        out TAcceptance accepted)
    {
        ArgumentNullException.ThrowIfNull(accept);

        lock (_gate)
        {
            if (_authorizationStore is not null)
            {
                // Product grants require a concrete permission code and target server.
                // The preview flag overload must never become a bypass around scoped RBAC.
                accepted = default!;
                return RemoteMutationAuthorizationStatus.Forbidden;
            }

            if (!_sessions.TryValidate(sessionToken, login, out var session) ||
                !RemoteSessionStore.CsrfMatches(session, csrfToken))
            {
                accepted = default!;
                return RemoteMutationAuthorizationStatus.Unauthorized;
            }

            if (requiredPermission == RemoteWebPermission.None
                || (requiredPermission & ~RemoteWebPermission.All) != 0
                || !session.Permissions.HasFlag(requiredPermission))
            {
                accepted = default!;
                return RemoteMutationAuthorizationStatus.Forbidden;
            }

            accepted = accept(session.SessionId);
            return RemoteMutationAuthorizationStatus.Accepted;
        }
    }

    /// <summary>
    /// Formal product authorization boundary. The opaque web server id is resolved by the API
    /// to the product's Guid identity, and the protected authorization store is consulted again
    /// inside the same lock that registers the backend mutation.
    /// </summary>
    public RemoteAuthorizationStatus TryAcceptMutation<TAcceptance>(
        string? sessionToken,
        string login,
        string? csrfToken,
        string permissionCode,
        Guid? targetServerId,
        Func<RemoteMutationAcceptanceContext, TAcceptance> accept,
        out TAcceptance accepted)
    {
        ArgumentNullException.ThrowIfNull(accept);

        lock (_gate)
        {
            var authorization = TryAuthorizeCore(
                sessionToken,
                login,
                permissionCode,
                targetServerId,
                out var session,
                out _);
            if (authorization != RemoteAuthorizationStatus.Granted ||
                !RemoteSessionStore.CsrfMatches(session!, csrfToken))
            {
                accepted = default!;
                return authorization == RemoteAuthorizationStatus.Granted
                    ? RemoteAuthorizationStatus.Unauthorized
                    : authorization;
            }

            accepted = accept(new RemoteMutationAcceptanceContext(
                session!.SessionId,
                session.Username!,
                permissionCode,
                targetServerId));
            return RemoteAuthorizationStatus.Granted;
        }
    }

    public RemoteAuthorizationStatus TryAuthorizeRequest(
        string? sessionToken,
        string login,
        string permissionCode,
        Guid? targetServerId,
        out ValidatedRemoteSession session,
        out RemoteAuthorizationSnapshot? currentAuthorization)
    {
        lock (_gate)
        {
            var status = TryAuthorizeCore(
                sessionToken,
                login,
                permissionCode,
                targetServerId,
                out var validated,
                out currentAuthorization);
            session = validated!;
            return status;
        }
    }

    public bool TryValidateSession(
        string? sessionToken,
        string login,
        out ValidatedRemoteSession session,
        out RemoteAuthorizationSnapshot? currentAuthorization)
    {
        lock (_gate)
        {
            session = default!;
            currentAuthorization = null;
            if (!_sessions.TryValidate(sessionToken, login, out var validated) ||
                string.IsNullOrWhiteSpace(validated.Username))
            {
                return false;
            }

            if (_authorizationStore is null)
            {
                if (validated.Authorization is not null)
                {
                    _sessions.Revoke(sessionToken);
                    return false;
                }

                session = validated;
                return true;
            }

            if (!TryGetFormalAuthorization(login, validated.Username, out var current) ||
                current is null ||
                validated.Authorization is null ||
                !string.Equals(
                    current.SecurityStamp,
                    validated.Authorization.SecurityStamp,
                    StringComparison.Ordinal))
            {
                _sessions.RevokeForUsername(validated.Username);
                return false;
            }

            session = validated with { Authorization = current };
            currentAuthorization = current;
            return true;
        }
    }

    public bool TryAcceptMutation<TAcceptance>(
        string? sessionToken,
        string login,
        string? csrfToken,
        Func<Guid, TAcceptance> accept,
        out TAcceptance accepted)
        => TryAcceptMutation(
               sessionToken,
               login,
               csrfToken,
               RemoteWebPermission.All,
               accept,
               out accepted) == RemoteMutationAuthorizationStatus.Accepted;

    public bool Revoke(string? sessionToken)
    {
        lock (_gate)
        {
            return _sessions.Revoke(sessionToken);
        }
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            _sessions.RevokeAll();
        }
    }

    private RemoteAuthorizationStatus TryAuthorizeCore(
        string? sessionToken,
        string login,
        string permissionCode,
        Guid? targetServerId,
        out ValidatedRemoteSession? session,
        out RemoteAuthorizationSnapshot? currentAuthorization)
    {
        session = null;
        currentAuthorization = null;
        if (!ProductPermissionCatalog.TryGet(permissionCode, out var descriptor))
        {
            return RemoteAuthorizationStatus.Forbidden;
        }

        if (!TryValidateSession(
                sessionToken,
                login,
                out var validated,
                out currentAuthorization))
        {
            return RemoteAuthorizationStatus.Unauthorized;
        }

        session = validated;
        if (currentAuthorization is null)
        {
            return RemoteLegacyPermissionMapping.IsGranted(validated.Permissions, permissionCode)
                ? RemoteAuthorizationStatus.Granted
                : RemoteAuthorizationStatus.Forbidden;
        }

        if ((descriptor.SupportsServerScope && (targetServerId is null || targetServerId == Guid.Empty)) ||
            (!descriptor.SupportsServerScope && targetServerId is not null))
        {
            return RemoteAuthorizationStatus.Forbidden;
        }

        return ProductAuthorization.Evaluate(
                currentAuthorization.Grants,
                permissionCode,
                targetServerId) == ProductAuthorizationDecision.Granted
            ? RemoteAuthorizationStatus.Granted
            : RemoteAuthorizationStatus.Forbidden;
    }

    private bool TryGetFormalAuthorization(
        string login,
        string username,
        out RemoteAuthorizationSnapshot? authorization)
    {
        authorization = null;
        if (_authorizationStore is null)
        {
            return true;
        }

        try
        {
            if (!_authorizationStore.TryGetAuthorization(login, username, out var candidate) ||
                !RemoteAuthorizationSnapshotValidator.TryCreateImmutable(candidate, out var immutable))
            {
                return false;
            }

            authorization = immutable;
            return true;
        }
        catch
        {
            // Authorization storage is part of the security boundary. Any read/parse/storage
            // failure is indistinguishable from revocation and therefore fails closed.
            return false;
        }
    }

    private sealed class DenyAllRememberedDeviceStore : IRemoteRememberedDeviceStore
    {
        public static DenyAllRememberedDeviceStore Instance { get; } = new();

        public IssuedRemoteRememberedDevice IssueRememberedDevice(
            string login,
            string username,
            string label)
            => throw new InvalidOperationException("Remembered-device login is unavailable.");

        public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
            string login,
            string token,
            Guid requestId)
            => new(RemoteRememberedDeviceRefreshStatus.Invalid);

        public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices() => [];

        public bool RevokeRememberedDevice(string login, string token) => false;

        public bool RevokeRememberedDevice(Guid deviceId) => false;

        public int RevokeRememberedDevicesForAccount(string username) => 0;

        public int RevokeAllRememberedDevices() => 0;
    }
}

public enum RemoteMutationAuthorizationStatus
{
    Accepted,
    Unauthorized,
    Forbidden
}
