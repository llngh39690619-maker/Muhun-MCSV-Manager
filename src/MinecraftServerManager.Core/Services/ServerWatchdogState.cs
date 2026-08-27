using System.Collections.Concurrent;

namespace MinecraftServerManager.Core.Services;

public sealed record ServerWatchdogPolicy(
    TimeSpan StartupGrace,
    int ConsecutiveFailureThreshold)
{
    public static ServerWatchdogPolicy Default { get; } = new(TimeSpan.FromMinutes(3), 3);

    public void Validate()
    {
        if (StartupGrace < TimeSpan.Zero || StartupGrace > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(StartupGrace));
        }

        if (ConsecutiveFailureThreshold is < 2 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(ConsecutiveFailureThreshold));
        }
    }
}

public sealed record ServerWatchdogObservation(
    bool IsCurrentSession,
    bool IsInsideStartupGrace,
    bool IsHealthy,
    int ConsecutiveFailures,
    bool ShouldRestart,
    string? Error = null);

/// <summary>
/// Session-scoped watchdog state machine. Scheduling and process control remain with the caller;
/// this class only prevents stale sessions, single transient failures, and duplicate triggers.
/// </summary>
public sealed class ServerWatchdogState
{
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();

    public void StartSession(Guid instanceId, Guid sessionId, DateTimeOffset startedAtUtc)
        => _sessions.AddOrUpdate(
            instanceId,
            _ => new SessionState(sessionId, startedAtUtc),
            (_, _) => new SessionState(sessionId, startedAtUtc));

    public void EndSession(Guid instanceId, Guid sessionId)
    {
        if (_sessions.TryGetValue(instanceId, out var state) && state.SessionId == sessionId)
        {
            _sessions.TryRemove(new KeyValuePair<Guid, SessionState>(instanceId, state));
        }
    }

    public ServerWatchdogObservation Record(
        Guid instanceId,
        Guid sessionId,
        DateTimeOffset observedAtUtc,
        bool isHealthy,
        ServerWatchdogPolicy policy,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (!_sessions.TryGetValue(instanceId, out var state) || state.SessionId != sessionId)
        {
            return new ServerWatchdogObservation(false, false, isHealthy, 0, false, error);
        }

        lock (state.Sync)
        {
            if (state.SessionId != sessionId)
            {
                return new ServerWatchdogObservation(false, false, isHealthy, 0, false, error);
            }

            if (observedAtUtc - state.StartedAtUtc < policy.StartupGrace)
            {
                if (isHealthy) state.ConsecutiveFailures = 0;
                return new ServerWatchdogObservation(true, true, isHealthy, state.ConsecutiveFailures, false, error);
            }

            if (isHealthy)
            {
                state.ConsecutiveFailures = 0;
                state.HasTriggered = false;
                return new ServerWatchdogObservation(true, false, true, 0, false);
            }

            state.ConsecutiveFailures++;
            var shouldRestart = state.ConsecutiveFailures >= policy.ConsecutiveFailureThreshold
                && !state.HasTriggered;
            if (shouldRestart) state.HasTriggered = true;
            return new ServerWatchdogObservation(
                true,
                false,
                false,
                state.ConsecutiveFailures,
                shouldRestart,
                error);
        }
    }

    private sealed class SessionState(Guid sessionId, DateTimeOffset startedAtUtc)
    {
        public object Sync { get; } = new();
        public Guid SessionId { get; } = sessionId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public int ConsecutiveFailures { get; set; }
        public bool HasTriggered { get; set; }
    }
}

public sealed record CrashRestartDecision(
    bool ShouldRestart,
    TimeSpan Delay,
    int CrashesInWindow,
    string Message);

/// <summary>Bounds repeated automatic restarts while allowing a stable run to clear history.</summary>
public sealed class CrashRestartLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _crashes = [];

    public CrashRestartDecision RecordCrash(
        Guid instanceId,
        DateTimeOffset occurredAtUtc,
        TimeSpan sessionUptime,
        int maximumRestarts = 3,
        TimeSpan? window = null,
        TimeSpan? stableReset = null)
    {
        if (maximumRestarts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maximumRestarts));
        var effectiveWindow = window ?? TimeSpan.FromMinutes(10);
        var effectiveStableReset = stableReset ?? TimeSpan.FromMinutes(10);
        if (effectiveWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        if (effectiveStableReset < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stableReset));

        lock (_sync)
        {
            if (!_crashes.TryGetValue(instanceId, out var history))
            {
                history = new Queue<DateTimeOffset>();
                _crashes.Add(instanceId, history);
            }

            if (sessionUptime >= effectiveStableReset)
            {
                history.Clear();
            }

            while (history.TryPeek(out var old) && occurredAtUtc - old > effectiveWindow)
            {
                history.Dequeue();
            }

            history.Enqueue(occurredAtUtc);
            var count = history.Count;
            if (count > maximumRestarts)
            {
                return new CrashRestartDecision(
                    false,
                    TimeSpan.Zero,
                    count,
                    $"{effectiveWindow.TotalMinutes:0.#} 分鐘內已崩潰 {count} 次，已停止自動重啟以避免崩潰循環。");
            }

            var delay = count switch
            {
                1 => TimeSpan.FromSeconds(5),
                2 => TimeSpan.FromSeconds(15),
                _ => TimeSpan.FromSeconds(45)
            };
            return new CrashRestartDecision(
                true,
                delay,
                count,
                $"第 {count} 次非正常退出；將在 {delay.TotalSeconds:0} 秒後重啟。");
        }
    }

    public void Reset(Guid instanceId)
    {
        lock (_sync)
        {
            _crashes.Remove(instanceId);
        }
    }
}
