using System.Collections.Concurrent;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Service;

/// <summary>
/// Maintains a bounded in-memory view of online players from the Core console event stream.
/// Parsing happens on the process callback but only performs bounded string matching and one
/// dictionary update; no dispatcher, disk, network, or database work is performed there.
/// </summary>
public sealed class ProductPlayerPresenceTracker(
    ServerProcessManager processManager,
    ProductServerRegistry registry,
    TimeProvider timeProvider,
    ProductKnownPlayerRegistryReader? knownPlayerReader = null) : IHostedService, IDisposable
{
    public const int MaximumTrackedPlayersPerServer = 4_096;
    private readonly ConcurrentDictionary<Guid, ServerPresence> _servers = new();
    private int _started;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            processManager.ConsoleLineReceived += OnConsoleLineReceived;
            processManager.StateChanged += OnStateChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        _servers.Clear();
        return Task.CompletedTask;
    }

    public IReadOnlyList<RemotePlayerDto> GetPlayers(Guid serverId)
        => _servers.TryGetValue(serverId, out var presence)
            ? presence.CaptureOnline()
            : [];

    public async Task<IReadOnlyList<ProductKnownPlayerRecord>> GetKnownPlayersAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tracked = _servers.TryGetValue(serverId, out var presence)
            ? presence.CaptureKnown()
            : [];
        if (knownPlayerReader is null)
        {
            return tracked;
        }

        var stored = await knownPlayerReader.ReadAsync(serverId, cancellationToken)
            .ConfigureAwait(false);
        if (stored.Count == 0)
        {
            return tracked;
        }

        var merged = stored.ToDictionary(player => player.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var live in tracked)
        {
            if (merged.TryGetValue(live.Name, out var persisted))
            {
                merged[live.Name] = live with
                {
                    Uuid = live.Uuid ?? persisted.Uuid,
                    Operator = live.Operator || persisted.Operator,
                    Whitelisted = live.Whitelisted || persisted.Whitelisted,
                    Banned = live.Banned || persisted.Banned,
                };
            }
            else if (merged.Count < MaximumTrackedPlayersPerServer || live.Online)
            {
                merged[live.Name] = live;
            }
        }

        return merged.Values
            .OrderByDescending(static player => player.Online)
            .ThenBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTrackedPlayersPerServer)
            .ToArray();
    }

    public void Dispose()
    {
        Unsubscribe();
        _servers.Clear();
    }

    private void OnConsoleLineReceived(object? sender, ConsoleLineReceivedEventArgs args)
    {
        if (!registry.TryGet(args.InstanceId, out var registration) ||
            !Enum.TryParse<CoreType>(registration.CoreType, ignoreCase: true, out var coreType) ||
            !PlayerPresenceEventParser.TryParse(args.Line.Text, coreType, out var change))
        {
            return;
        }

        var presence = _servers.GetOrAdd(args.InstanceId, _ => new ServerPresence());
        presence.Apply(args.SessionId, change, timeProvider.GetUtcNow());
    }

    private void OnStateChanged(object? sender, ServerStateChangedEventArgs args)
    {
        if (args.State == ServerState.Running)
        {
            _servers.GetOrAdd(args.InstanceId, _ => new ServerPresence()).BeginSession(args.SessionId);
        }
        else if (args.State is ServerState.Stopped or ServerState.Crashed or ServerState.Faulted)
        {
            _servers.TryRemove(args.InstanceId, out _);
        }
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        processManager.ConsoleLineReceived -= OnConsoleLineReceived;
        processManager.StateChanged -= OnStateChanged;
    }

    private sealed class ServerPresence
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, RemotePlayerDto> _players =
            new(StringComparer.OrdinalIgnoreCase);
        private Guid _sessionId;

        public void BeginSession(Guid sessionId)
        {
            lock (_gate)
            {
                if (_sessionId == sessionId)
                {
                    return;
                }

                _sessionId = sessionId;
                _players.Clear();
            }
        }

        public void Apply(Guid sessionId, PlayerPresenceChange change, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_sessionId != sessionId)
                {
                    _sessionId = sessionId;
                    _players.Clear();
                }

                if (change.IsOnline)
                {
                    if (_players.Count >= MaximumTrackedPlayersPerServer &&
                        !_players.ContainsKey(change.PlayerName))
                    {
                        return;
                    }

                    _players[change.PlayerName] = new RemotePlayerDto(
                        change.PlayerName,
                        Uuid: null,
                        Online: true,
                        Operator: false,
                        Banned: false,
                        LastSeenUtc: now);
                }
                else
                {
                    _players.Remove(change.PlayerName);
                }
            }
        }

        public IReadOnlyList<RemotePlayerDto> CaptureOnline()
        {
            lock (_gate)
            {
                return _players.Values
                    .Where(static player => player.Online)
                    .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumTrackedPlayersPerServer)
                    .ToArray();
            }
        }

        public IReadOnlyList<ProductKnownPlayerRecord> CaptureKnown()
        {
            lock (_gate)
            {
                return _players.Values
                    .OrderByDescending(static player => player.Online)
                    .ThenBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumTrackedPlayersPerServer)
                    .Select(static player => new ProductKnownPlayerRecord(
                        player.Name,
                        player.Uuid,
                        player.Online,
                        player.Operator,
                        Whitelisted: false,
                        Banned: player.Banned,
                        LastSeenUtc: player.LastSeenUtc))
                    .ToArray();
            }
        }

    }
}
