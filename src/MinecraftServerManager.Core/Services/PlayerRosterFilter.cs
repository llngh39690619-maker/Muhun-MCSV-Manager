namespace MinecraftServerManager.Core.Services;

public enum PlayerRosterDisplayMode
{
    OnlineOnly,
    AllKnown
}

/// <summary>
/// Produces the player list shown by the GUI. The default mode intentionally excludes
/// historical, offline user-cache entries so a busy server does not grow an unbounded list.
/// </summary>
public static class PlayerRosterFilter
{
    public static IReadOnlyList<T> Apply<T>(
        IEnumerable<T> players,
        Func<T, bool> isOnline,
        PlayerRosterDisplayMode displayMode = PlayerRosterDisplayMode.OnlineOnly)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(isOnline);
        if (!Enum.IsDefined(displayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(displayMode));
        }

        return displayMode == PlayerRosterDisplayMode.AllKnown
            ? players.ToArray()
            : players.Where(isOnline).ToArray();
    }
}
