using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class OnlinePlayerRosterTests
{
    [Fact]
    public void SetPresence_JoinAddsAndLeaveRemovesPlayer()
    {
        var roster = new OnlinePlayerRoster();

        Assert.True(roster.SetPresence("Alex", true));
        Assert.True(roster.Contains("alex"));
        Assert.True(roster.SetPresence("ALEX", false));
        Assert.Empty(roster.Snapshot());
    }

    [Fact]
    public void SetPresence_UnknownLeaveDoesNotCreateHistoricalEntry()
    {
        var roster = new OnlinePlayerRoster();

        Assert.False(roster.SetPresence("HistoricalPlayer", false));

        Assert.Equal(0, roster.Count);
    }

    [Fact]
    public void Replace_ClearsPlayersFromPreviousServerSession()
    {
        var roster = new OnlinePlayerRoster();
        roster.Replace(["Alex", "Steve"]);

        roster.Replace([]);

        Assert.Empty(roster.Snapshot());
    }

    [Fact]
    public void SetPresence_DuplicateLoginAndLogoutEventsRemainIdempotent()
    {
        var roster = new OnlinePlayerRoster();

        Assert.True(roster.SetPresence("Alex", true));
        Assert.False(roster.SetPresence("alex", true));
        Assert.Single(roster.Snapshot());

        Assert.True(roster.SetPresence("ALEX", false));
        Assert.False(roster.SetPresence("Alex", false));
        Assert.Empty(roster.Snapshot());
    }
}
