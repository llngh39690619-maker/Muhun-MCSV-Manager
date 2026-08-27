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
    TimeProvider timeProvider) : IHostedService, IDisposable
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
            ? presence.Capture()
            : [];

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

        public IReadOnlyList<RemotePlayerDto> Capture()
        {
            lock (_gate)
            {
                return _players.Values
                    .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumTrackedPlayersPerServer)
                    .ToArray();
            }
        }
    }
}
