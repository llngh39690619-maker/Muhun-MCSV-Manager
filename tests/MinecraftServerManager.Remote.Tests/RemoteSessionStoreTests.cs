using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteSessionStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 21, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IssuedSession_IsBoundToLoginAndHasIndependentCsrfSecret()
    {
        var store = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));

        var issued = store.Issue("owner@gmail.com");

        Assert.Equal(43, issued.SessionToken.Length);
        Assert.Equal(43, issued.CsrfToken.Length);
        Assert.NotEqual(issued.SessionToken, issued.CsrfToken);
        Assert.True(store.TryValidate(issued.SessionToken, "OWNER@gmail.com", out var validated));
        Assert.True(RemoteSessionStore.CsrfMatches(validated, issued.CsrfToken));
        Assert.False(RemoteSessionStore.CsrfMatches(validated, new string('A', 43)));
        Assert.False(store.TryValidate(issued.SessionToken, "attacker@gmail.com", out _));
    }

    [Fact]
    public void ExpiredSession_IsRemoved()
    {
        var time = new MutableTimeProvider(Start);
        var store = new RemoteSessionStore(
            TestOptions.Create(sessionLifetime: TimeSpan.FromMinutes(15)),
            time);
        var issued = store.Issue("owner@gmail.com");

        time.Advance(TimeSpan.FromMinutes(15));

        Assert.False(store.TryValidate(issued.SessionToken, issued.Login, out _));
    }

    [Fact]
    public void CapacityIsBounded_OldestSessionIsEvicted()
    {
        var time = new MutableTimeProvider(Start);
        var store = new RemoteSessionStore(TestOptions.Create(maximumSessions: 2), time);
        var first = store.Issue("owner@gmail.com");
        time.Advance(TimeSpan.FromSeconds(1));
        var second = store.Issue("owner@gmail.com");
        time.Advance(TimeSpan.FromSeconds(1));
        var third = store.Issue("owner@gmail.com");

        Assert.False(store.TryValidate(first.SessionToken, first.Login, out _));
        Assert.True(store.TryValidate(second.SessionToken, second.Login, out _));
        Assert.True(store.TryValidate(third.SessionToken, third.Login, out _));
    }

    [Fact]
    public void RevokeAndRevokeAll_InvalidateSessions()
    {
        var store = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var first = store.Issue("owner@gmail.com");
        var second = store.Issue("owner@gmail.com");

        Assert.True(store.Revoke(first.SessionToken));
        Assert.False(store.TryValidate(first.SessionToken, first.Login, out _));
        store.RevokeAll();
        Assert.False(store.TryValidate(second.SessionToken, second.Login, out _));
    }

    [Fact]
    public void RevokeAllGeneration_PreventsSessionIssueAfterRevocationReturns()
    {
        var store = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var capturedBeforeInvitationConsumption = store.CaptureGeneration();

        // Deterministic ordering for the critical race: the invitation was
        // consumed, then desktop revocation completed, then Pair tried to issue.
        store.RevokeAll();

        Assert.False(store.TryIssueIfGenerationUnchanged(
            "owner@gmail.com",
            "account1",
            capturedBeforeInvitationConsumption,
            out _));
    }

    [Fact]
    public void UnchangedGeneration_AllowsSessionIssue()
    {
        var store = new RemoteSessionStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var generation = store.CaptureGeneration();

        Assert.True(store.TryIssueIfGenerationUnchanged(
            "owner@gmail.com",
            "account1",
            generation,
            out var issued));
        Assert.True(store.TryValidate(issued.SessionToken, issued.Login, out _));
        Assert.Equal("account1", issued.Username);
    }
}
