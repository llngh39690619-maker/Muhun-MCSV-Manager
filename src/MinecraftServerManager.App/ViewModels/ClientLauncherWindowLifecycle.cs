namespace MinecraftServerManager.App.ViewModels;

internal enum ClientLauncherWindowTransition
{
    None,
    Minimize,
    Restore,
}

/// <summary>
/// Tracks only client sessions that actually launched and requested automatic launcher hiding.
/// Keeping this state separate from WPF makes overlapping sessions and shutdown races explicit.
/// </summary>
internal sealed class ClientLauncherWindowLifecycle
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _hiddenSessions = [];
    private bool _shutdownStarted;

    internal int HiddenSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _hiddenSessions.Count;
            }
        }
    }

    public ClientLauncherWindowTransition CompleteLaunch(
        Guid sessionId,
        bool launchSucceeded,
        bool hideLauncherAfterGameStarts)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A client session id is required.", nameof(sessionId));
        }

        lock (_gate)
        {
            if (_shutdownStarted || !launchSucceeded || !hideLauncherAfterGameStarts)
            {
                return ClientLauncherWindowTransition.None;
            }

            return _hiddenSessions.Add(sessionId)
                ? ClientLauncherWindowTransition.Minimize
                : ClientLauncherWindowTransition.None;
        }
    }

    public ClientLauncherWindowTransition CompleteSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A client session id is required.", nameof(sessionId));
        }

        lock (_gate)
        {
            if (!_hiddenSessions.Remove(sessionId))
            {
                return ClientLauncherWindowTransition.None;
            }

            return !_shutdownStarted && _hiddenSessions.Count == 0
                ? ClientLauncherWindowTransition.Restore
                : ClientLauncherWindowTransition.None;
        }
    }

    public void BeginShutdown()
    {
        lock (_gate)
        {
            _shutdownStarted = true;
            _hiddenSessions.Clear();
        }
    }
}
