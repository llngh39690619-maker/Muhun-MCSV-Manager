using System.Collections.Concurrent;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Infrastructure;

internal sealed record PlayerPresenceSnapshot(
    Guid InstanceId,
    Guid SessionId,
    long Version,
    IReadOnlyList<string> OnlinePlayers);

/// <summary>
/// Maintains a bounded, thread-safe authoritative online set for each current process session.
/// Console threads update this state directly, while WPF receives coalesced snapshots. Event
/// volume therefore never creates one dispatcher item per line and no leave event is discarded.
/// </summary>
internal sealed class PlayerPresenceDispatchBuffer
{
    private readonly int _maximumOnlinePlayers;
    private readonly ConcurrentDictionary<Guid, SessionRoster> _sessions = new();

    public PlayerPresenceDispatchBuffer(int maximumOnlinePlayers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOnlinePlayers, 1);
        _maximumOnlinePlayers = maximumOnlinePlayers;
    }

    public void StartSession(Guid instanceId, Guid sessionId)
    {
        _sessions.AddOrUpdate(
            instanceId,
            _ => new SessionRoster(sessionId, _maximumOnlinePlayers),
            (_, existing) => existing.SessionId == sessionId
                ? existing
                : new SessionRoster(sessionId, _maximumOnlinePlayers));
    }

    public void EndSession(Guid instanceId, Guid sessionId)
    {
        if (_sessions.TryGetValue(instanceId, out var existing)
            && existing.SessionId == sessionId)
        {
            _sessions.TryRemove(new KeyValuePair<Guid, SessionRoster>(instanceId, existing));
        }
    }

    public void RemoveInstance(Guid instanceId) => _sessions.TryRemove(instanceId, out _);

    public bool Apply(
        Guid instanceId,
        Guid sessionId,
        PlayerPresenceChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return _sessions.TryGetValue(instanceId, out var session)
            && session.SessionId == sessionId
            && session.Apply(change);
    }

    public PlayerPresenceSnapshot? Capture(Guid instanceId)
        => _sessions.TryGetValue(instanceId, out var session)
            ? session.Capture(instanceId)
            : null;

    public bool HasChangedSince(Guid instanceId, Guid sessionId, long version)
        => _sessions.TryGetValue(instanceId, out var session)
            && (session.SessionId != sessionId || session.Version != version);

    private sealed class SessionRoster
    {
        private readonly object _sync = new();
        private readonly int _capacity;
        private readonly Dictionary<string, string> _onlinePlayers = new(StringComparer.OrdinalIgnoreCase);
        private long _version;

        public SessionRoster(Guid sessionId, int capacity)
        {
            SessionId = sessionId;
            _capacity = capacity;
        }

        public Guid SessionId { get; }

        public long Version
        {
            get
            {
                lock (_sync)
                {
                    return _version;
                }
            }
        }

        public bool Apply(PlayerPresenceChange change)
        {
            var playerName = change.PlayerName.Trim();
            if (playerName.Length == 0) return false;
            lock (_sync)
            {
                bool changed;
                if (change.IsOnline)
                {
                    if (_onlinePlayers.TryGetValue(playerName, out var existingName))
                    {
                        changed = !string.Equals(existingName, playerName, StringComparison.Ordinal);
                        if (changed) _onlinePlayers[playerName] = playerName;
                    }
                    else if (_onlinePlayers.Count < _capacity)
                    {
                        _onlinePlayers.Add(playerName, playerName);
                        changed = true;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    changed = _onlinePlayers.Remove(playerName);
                }

                if (changed) _version++;
                return changed;
            }
        }

        public PlayerPresenceSnapshot Capture(Guid instanceId)
        {
            lock (_sync)
            {
                return new PlayerPresenceSnapshot(
                    instanceId,
                    SessionId,
                    _version,
                    _onlinePlayers.Values.ToArray());
            }
        }
    }
}
