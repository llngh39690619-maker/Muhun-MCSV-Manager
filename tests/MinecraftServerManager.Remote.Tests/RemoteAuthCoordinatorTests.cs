using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteAuthCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 21, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoginCrossingCompletedRevocation_CannotIssueSession()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var credentials = new BlockingCredentialStore();
        var coordinator = new RemoteAuthCoordinator(sessions, credentials);
        IssuedRemoteSession? issued = null;

        var login = Task.Run(() =>
        {
            var result = coordinator.TryLogin(
                "account1",
                "12345678",
                "owner@gmail.com",
                out var candidate);
            if (result.Status == RemoteCredentialAuthenticationStatus.Success) issued = candidate;
        });

        await credentials.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.RevokeAll();
        credentials.Release.TrySetResult();
        await login.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(issued);
    }

    [Fact]
    public void CorrectCredential_IssuesSessionBoundToUsernameAndGmail()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var coordinator = new RemoteAuthCoordinator(sessions, new ValidCredentialStore());

        var result = coordinator.TryLogin(
            "ACCOUNT1",
            "12345678",
            "owner@gmail.com",
            out var issued);

        Assert.Equal(RemoteCredentialAuthenticationStatus.Success, result.Status);
        Assert.Equal("account1", issued.Username);
        Assert.True(sessions.TryValidate(issued.SessionToken, issued.Login, out var validated));
        Assert.Equal("account1", validated.Username);
    }

    [Fact]
    public void WrongCredential_DoesNotIssueSession()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var coordinator = new RemoteAuthCoordinator(sessions, new ValidCredentialStore());

        var result = coordinator.TryLogin(
            "account1",
            "00000000",
            "owner@gmail.com",
            out _);

        Assert.Equal(RemoteCredentialAuthenticationStatus.InvalidCredentials, result.Status);
    }

    [Fact]
    public async Task AcceptedMutationIsLinearizedBeforeRevokeAll_AndLaterAcceptanceIsRejected()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var coordinator = new RemoteAuthCoordinator(sessions, new ValidCredentialStore());
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            coordinator.TryLogin(
                "account1",
                "12345678",
                "owner@gmail.com",
                out var issued).Status);

        var acceptanceEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAcceptance = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptance = Task.Run(() => coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            sessionId =>
            {
                acceptanceEntered.TrySetResult();
                releaseAcceptance.Task.GetAwaiter().GetResult();
                return sessionId;
            },
            out _));

        await acceptanceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revokeAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var revoke = Task.Run(() =>
        {
            revokeAttempted.TrySetResult();
            coordinator.RevokeAll();
        });
        await revokeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(revoke.IsCompleted);

        releaseAcceptance.TrySetResult();
        Assert.True(await acceptance.WaitAsync(TimeSpan.FromSeconds(5)));
        await revoke.WaitAsync(TimeSpan.FromSeconds(5));

        var callbackInvoked = false;
        Assert.False(coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            _ => callbackInvoked = true,
            out _));
        Assert.False(callbackInvoked);
    }

    [Fact]
    public void RevokeAllBeforeMutationAcceptance_NeverInvokesAcceptanceCallback()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var coordinator = new RemoteAuthCoordinator(sessions, new ValidCredentialStore());
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            coordinator.TryLogin(
                "account1",
                "12345678",
                "owner@gmail.com",
                out var issued).Status);
        coordinator.RevokeAll();

        var callbackInvoked = false;
        Assert.False(coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            _ => callbackInvoked = true,
            out _));
        Assert.False(callbackInvoked);
    }

    [Fact]
    public void SessionPermissionSnapshot_AuthorizesOnlyTheRequiredMutation()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var coordinator = new RemoteAuthCoordinator(
            sessions,
            new PermissionCredentialStore(RemoteWebPermission.StartServer));
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            coordinator.TryLogin(
                "account1",
                "12345678",
                "owner@gmail.com",
                out var issued).Status);

        var forbiddenCallback = false;
        var forbidden = coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            RemoteWebPermission.StopServer,
            _ => forbiddenCallback = true,
            out _);
        var allowedCallback = false;
        var allowed = coordinator.TryAcceptMutation(
            issued.SessionToken,
            issued.Login,
            issued.CsrfToken,
            RemoteWebPermission.StartServer,
            _ => allowedCallback = true,
            out _);

        Assert.Equal(RemoteMutationAuthorizationStatus.Forbidden, forbidden);
        Assert.False(forbiddenCallback);
        Assert.Equal(RemoteMutationAuthorizationStatus.Accepted, allowed);
        Assert.True(allowedCallback);
    }

    [Fact]
    public void SignOut_DeviceStoreFailureLeavesSessionValidSoTheSameRequestCanBeRetried()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var devices = new FailOnceRememberedDeviceStore();
        var coordinator = new RemoteAuthCoordinator(sessions, new ValidCredentialStore(), devices);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            coordinator.TryLogin(
                "account1",
                "12345678",
                "owner@gmail.com",
                out var issued).Status);

        Assert.Throws<IOException>(() => coordinator.SignOut(
            issued.Login,
            issued.SessionToken,
            "remembered-token"));
        Assert.True(sessions.TryValidate(issued.SessionToken, issued.Login, out _));

        Assert.True(coordinator.SignOut(
            issued.Login,
            issued.SessionToken,
            "remembered-token"));
        Assert.False(sessions.TryValidate(issued.SessionToken, issued.Login, out _));
        Assert.Equal(2, devices.RevokeAttempts);
    }

    [Fact]
    public async Task SignOut_RacingRememberedRefreshCannotLeaveAReplacementSession()
    {
        var sessions = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var devices = new BlockingRememberedDeviceStore();
        var coordinator = new RemoteAuthCoordinator(sessions, new ValidCredentialStore(), devices);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            coordinator.TryLogin(
                "account1",
                "12345678",
                "owner@gmail.com",
                out var original).Status);

        IssuedRemoteSession? replacementSession = null;
        var refresh = Task.Run(() =>
        {
            var result = coordinator.TryRefreshRememberedDevice(
                original.Login,
                "remembered-token",
                Guid.NewGuid(),
                out var candidate);
            if (result.Status == RemoteRememberedDeviceRefreshStatus.Success)
            {
                replacementSession = candidate;
            }
        });
        await devices.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var signOut = Task.Run(() => coordinator.SignOut(
            original.Login,
            original.SessionToken,
            "remembered-token"));
        await devices.RevokeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        devices.ReleaseRefresh.TrySetResult();
        await devices.RefreshReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        devices.ReleaseRevoke.TrySetResult();

        Assert.True(await signOut.WaitAsync(TimeSpan.FromSeconds(5)));
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(replacementSession);
        Assert.False(sessions.TryValidate(original.SessionToken, original.Login, out _));
    }

    private class ValidCredentialStore : IRemoteCredentialStore
    {
        public bool HasCredentialForLogin(string tailscaleLogin)
            => string.Equals(tailscaleLogin, "owner@gmail.com", StringComparison.OrdinalIgnoreCase);

        public virtual RemoteCredentialAuthenticationResult Authenticate(
            string tailscaleLogin,
            string username,
            string pin)
            => HasCredentialForLogin(tailscaleLogin) && username == "account1" && pin == "12345678"
                ? new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.Success,
                    "account1")
                : new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);
    }

    private sealed class BlockingCredentialStore : ValidCredentialStore
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override RemoteCredentialAuthenticationResult Authenticate(
            string tailscaleLogin,
            string username,
            string pin)
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return base.Authenticate(tailscaleLogin, username, pin);
        }
    }

    private sealed class PermissionCredentialStore(RemoteWebPermission permissions)
        : ValidCredentialStore
    {
        public override RemoteCredentialAuthenticationResult Authenticate(
            string tailscaleLogin,
            string username,
            string pin)
            => HasCredentialForLogin(tailscaleLogin) && username == "account1" && pin == "12345678"
                ? new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.Success,
                    "account1",
                    Permissions: permissions)
                : new RemoteCredentialAuthenticationResult(
                    RemoteCredentialAuthenticationStatus.InvalidCredentials);
    }

    private sealed class FailOnceRememberedDeviceStore : IRemoteRememberedDeviceStore
    {
        public int RevokeAttempts { get; private set; }

        public bool RevokeRememberedDevice(string login, string token)
        {
            RevokeAttempts++;
            if (RevokeAttempts == 1) throw new IOException("simulated durable write failure");
            return true;
        }

        public IssuedRemoteRememberedDevice IssueRememberedDevice(string login, string username, string label)
            => throw new NotSupportedException();

        public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
            string login,
            string token,
            Guid requestId)
            => new(RemoteRememberedDeviceRefreshStatus.Invalid);

        public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices() => [];
        public bool RevokeRememberedDevice(Guid deviceId) => false;
        public int RevokeRememberedDevicesForAccount(string username) => 0;
        public int RevokeAllRememberedDevices() => 0;
    }

    private sealed class BlockingRememberedDeviceStore : IRemoteRememberedDeviceStore
    {
        public TaskCompletionSource RefreshEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RefreshReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RevokeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRevoke { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RemoteRememberedDeviceRefreshResult RefreshRememberedDevice(
            string login,
            string token,
            Guid requestId)
        {
            RefreshEntered.TrySetResult();
            ReleaseRefresh.Task.GetAwaiter().GetResult();
            RefreshReturned.TrySetResult();
            var now = Start;
            return new RemoteRememberedDeviceRefreshResult(
                RemoteRememberedDeviceRefreshStatus.Success,
                "rotated-token",
                new RemoteRememberedDeviceInfo(
                    Guid.NewGuid(),
                    "account1",
                    "Test iPhone",
                    now,
                    now,
                    now.AddDays(90),
                    now.AddDays(365),
                    RemoteRememberedDeviceStatus.Active),
                "account1",
                RemoteWebPermission.All);
        }

        public bool RevokeRememberedDevice(string login, string token)
        {
            RevokeEntered.TrySetResult();
            ReleaseRevoke.Task.GetAwaiter().GetResult();
            return true;
        }

        public IssuedRemoteRememberedDevice IssueRememberedDevice(string login, string username, string label)
            => throw new NotSupportedException();

        public IReadOnlyList<RemoteRememberedDeviceInfo> GetRememberedDevices() => [];
        public bool RevokeRememberedDevice(Guid deviceId) => false;
        public int RevokeRememberedDevicesForAccount(string username) => 0;
        public int RevokeAllRememberedDevices() => 0;
    }
}
