using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class PlayerRosterFilterTests
{
    private static readonly TestPlayer[] Players =
    [
        new("Alex", true),
        new("HistoricalPlayer", false),
        new("Steve", true)
    ];

    [Fact]
    public void Apply_DefaultModeReturnsOnlyOnlinePlayers()
    {
        var visible = PlayerRosterFilter.Apply(Players, player => player.IsOnline);

        Assert.Equal(["Alex", "Steve"], visible.Select(player => player.Name));
    }

    [Fact]
    public void Apply_AllKnownModeIncludesOfflinePlayers()
    {
        var visible = PlayerRosterFilter.Apply(
            Players,
            player => player.IsOnline,
            PlayerRosterDisplayMode.AllKnown);

        Assert.Equal(["Alex", "HistoricalPlayer", "Steve"], visible.Select(player => player.Name));
    }

    [Fact]
    public void Apply_OnlineOnlyReflectsPresenceChangeImmediately()
    {
        var mutablePlayers = Players.ToList();
        mutablePlayers[0] = mutablePlayers[0] with { IsOnline = false };

        var visible = PlayerRosterFilter.Apply(mutablePlayers, player => player.IsOnline);

        Assert.Equal(["Steve"], visible.Select(player => player.Name));
    }

    private sealed record TestPlayer(string Name, bool IsOnline);
}
