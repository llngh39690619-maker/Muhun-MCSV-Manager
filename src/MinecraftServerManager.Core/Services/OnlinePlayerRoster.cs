namespace MinecraftServerManager.Core.Services;

/// <summary>
/// Tracks the authoritative online set for one running server session. Registry-file refreshes
/// are intentionally kept separate so a stale disk snapshot cannot resurrect a player who left.
/// </summary>
public sealed class OnlinePlayerRoster
{
    private readonly Dictionary<string, string> _players = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _players.Count;

    public bool Contains(string playerName)
        => !string.IsNullOrWhiteSpace(playerName) && _players.ContainsKey(playerName.Trim());

    public IReadOnlyList<string> Snapshot()
        => _players.Values.ToArray();

    public bool SetPresence(string playerName, bool isOnline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        var normalizedName = playerName.Trim();
        return isOnline
            ? _players.TryAdd(normalizedName, normalizedName)
            : _players.Remove(normalizedName);
    }

    public void Replace(IEnumerable<string> onlineNames)
    {
        ArgumentNullException.ThrowIfNull(onlineNames);
        _players.Clear();
        foreach (var name in onlineNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var normalizedName = name.Trim();
            _players.TryAdd(normalizedName, normalizedName);
        }
    }
}
