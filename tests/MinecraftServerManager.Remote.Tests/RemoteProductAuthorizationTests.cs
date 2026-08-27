using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteProductAuthorizationTests
{
    private static readonly Guid ServerA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ServerB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void PreviewFlags_MapToProductCodesAtOneServerScopeOnly()
    {
        var grants = RemoteWebPermissionProductMapper.MapToServerScope(
            RemoteWebPermission.All,
            ServerA);

        Assert.Equal(
            [
                ProductPermissionCodes.ServerStart,
                ProductPermissionCodes.ServerStop,
                ProductPermissionCodes.ServerRestart,
                ProductPermissionCodes.ConsoleWrite,
                ProductPermissionCodes.PlayerManage,
                ProductPermissionCodes.BackupCreate
            ],
            grants.Select(grant => grant.PermissionCode));
        Assert.All(grants, grant =>
        {
            Assert.Equal(ProductPermissionScopeKind.Server, grant.Scope.Kind);
            Assert.Equal(ServerA, grant.Scope.ServerId);
        });
    }

    [Fact]
    public void ScopedProductGrant_AuthorizesOnlyMatchingServerAndPermission()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create());
        var credentials = new MutableFormalCredentialStore(
            "stamp-1",
            [Grant(ProductPermissionCodes.ServerStart, ServerA)]);
        var coordinator = new RemoteAuthCoordinator(sessions, credentials);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            coordinator.TryLogin(
                "account1",
                "12345678",
                "owner@gmail.com",
                out var issued).Status);

        var accepted = coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            ProductPermissionCodes.ServerStart,
            ServerA,
            context => context.ServerId,
            out var acceptedServer);
        var wrongServer = coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            ProductPermissionCodes.ServerStart,
            ServerB,
            _ => true,
            out _);
        var wrongPermission = coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            ProductPermissionCodes.ServerStop,
            ServerA,
            _ => true,
            out _);

        Assert.Equal(RemoteAuthorizationStatus.Granted, accepted);
        Assert.Equal(ServerA, acceptedServer);
        Assert.Equal(RemoteAuthorizationStatus.Forbidden, wrongServer);
        Assert.Equal(RemoteAuthorizationStatus.Forbidden, wrongPermission);
    }

    [Fact]
    public void PermissionReductionWithSameStamp_IsAppliedOnNextRequest()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create());
        var credentials = new MutableFormalCredentialStore(
            "stamp-1",
            [Grant(ProductPermissionCodes.ServerStart, ServerA)]);
        var coordinator = new RemoteAuthCoordinator(sessions, credentials);
        _ = coordinator.TryLogin(
            "account1",
            "12345678",
            "owner@gmail.com",
            out var issued);
        credentials.SetAuthorization(
            "stamp-1",
            [Grant(ProductPermissionCodes.ServerStop, ServerA)]);

        Assert.Equal(
            RemoteAuthorizationStatus.Forbidden,
            coordinator.TryAuthorizeRequest(
                issued.SessionToken,
                issued.Login,
                ProductPermissionCodes.ServerStart,
                ServerA,
                out _,
                out _));
        Assert.Equal(
            RemoteAuthorizationStatus.Granted,
            coordinator.TryAuthorizeRequest(
                issued.SessionToken,
                issued.Login,
                ProductPermissionCodes.ServerStop,
                ServerA,
                out _,
                out _));
    }

    [Fact]
    public void GlobalUpdateManageGrant_AuthorizesOnlyGlobalUpdateBoundary()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create());
        var credentials = new MutableFormalCredentialStore(
            "stamp-1",
            [new ProductPermissionGrant(
                ProductPermissionCodes.UpdateManage,
                ProductPermissionScope.Global)]);
        var coordinator = new RemoteAuthCoordinator(sessions, credentials);
        _ = coordinator.TryLogin(
            "account1",
            "12345678",
            "owner@gmail.com",
            out var issued);

        Assert.Equal(
            RemoteAuthorizationStatus.Granted,
            coordinator.TryAuthorizeRequest(
                issued.SessionToken,
                issued.Login,
                ProductPermissionCodes.UpdateManage,
                targetServerId: null,
                out _,
                out _));
        Assert.Equal(
            RemoteAuthorizationStatus.Forbidden,
            coordinator.TryAuthorizeRequest(
                issued.SessionToken,
                issued.Login,
                ProductPermissionCodes.UpdateManage,
                ServerA,
                out _,
                out _));
    }

    [Fact]
    public void SecurityStampChange_RevokesExistingSessionBeforeCallback()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create());
        var credentials = new MutableFormalCredentialStore(
            "stamp-1",
            [Grant(ProductPermissionCodes.ServerRestart, ServerA)]);
        var coordinator = new RemoteAuthCoordinator(sessions, credentials);
        _ = coordinator.TryLogin(
            "account1",
            "12345678",
            "owner@gmail.com",
            out var issued);
        _ = coordinator.TryLogin(
            "account1",
            "12345678",
            "owner@gmail.com",
            out var secondSession);
        credentials.SetAuthorization(
            "stamp-2",
            [Grant(ProductPermissionCodes.ServerRestart, ServerA)]);

        var callbackInvoked = false;
        var status = coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            ProductPermissionCodes.ServerRestart,
            ServerA,
            _ => callbackInvoked = true,
            out _);

        Assert.Equal(RemoteAuthorizationStatus.Unauthorized, status);
        Assert.False(callbackInvoked);
        Assert.False(sessions.TryValidate(issued.SessionToken, issued.Login, out _));
        Assert.False(sessions.TryValidate(secondSession.SessionToken, secondSession.Login, out _));
    }

    [Fact]
    public void InvalidFormalSnapshot_FailsLoginClosed()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create());
        var credentials = new MutableFormalCredentialStore(
            "stamp-1",
            [new ProductPermissionGrant(
                "unknown.permission",
                ProductPermissionScope.ForServer(ServerA))]);
        var coordinator = new RemoteAuthCoordinator(sessions, credentials);

        var result = coordinator.TryLogin(
            "account1",
            "12345678",
            "owner@gmail.com",
            out _);

        Assert.Equal(RemoteCredentialAuthenticationStatus.InvalidCredentials, result.Status);
    }

    [Fact]
    public void AuditContract_RejectsSecretsAndAcceptsBoundedMutationMetadata()
    {
        var valid = new RemoteSecurityAuditEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            RemoteSecurityAuditAction.ServerMutation,
            RemoteSecurityAuditOutcome.Accepted,
            "account1",
            ProductPermissionCodes.ServerStart,
            ServerA,
            "authorization_accepted",
            Guid.NewGuid());
        var unsafeReason = valid with { ReasonCode = "contains secret" };

        Assert.True(RemoteSecurityAuditEventValidator.IsValid(valid));
        Assert.False(RemoteSecurityAuditEventValidator.IsValid(unsafeReason));
    }

    private static ProductPermissionGrant Grant(string permissionCode, Guid serverId)
        => new(permissionCode, ProductPermissionScope.ForServer(serverId));

    private sealed class MutableFormalCredentialStore
        : IRemoteCredentialStore, IRemoteAuthorizationStore
    {
        private readonly object _gate = new();
        private RemoteAuthorizationSnapshot _authorization;

        public MutableFormalCredentialStore(
            string securityStamp,
            IReadOnlyList<ProductPermissionGrant> grants)
        {
            _authorization = new RemoteAuthorizationSnapshot(securityStamp, grants);
        }

        public bool HasCredentialForLogin(string tailscaleLogin)
            => string.Equals(tailscaleLogin, "owner@gmail.com", StringComparison.OrdinalIgnoreCase);

        public RemoteCredentialAuthenticationResult Authenticate(
            string tailscaleLogin,
            string username,
            string pin)
            => HasCredentialForLogin(tailscaleLogin) &&
               username == "account1" &&
               pin == "12345678"
                ? new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.Success,
                    "account1")
                : new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);

        public bool TryGetAuthorization(
            string credentialSubject,
            string username,
            out RemoteAuthorizationSnapshot snapshot)
        {
            lock (_gate)
            {
                snapshot = _authorization;
                return HasCredentialForLogin(credentialSubject) && username == "account1";
            }
        }

        public void SetAuthorization(
            string securityStamp,
            IReadOnlyList<ProductPermissionGrant> grants)
        {
            lock (_gate)
            {
                _authorization = new RemoteAuthorizationSnapshot(securityStamp, grants);
            }
        }
    }
}
