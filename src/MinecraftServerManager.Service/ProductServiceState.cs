namespace MinecraftServerManager.Service;

public sealed class ProductServiceState(TimeProvider timeProvider)
{
    private readonly object _identityGate = new();
    private Guid _installationId;
    private int _foundationReady;
    private int _ipcReady;

    public Guid InstallationId
    {
        get
        {
            lock (_identityGate)
            {
                return _installationId;
            }
        }
    }

    public DateTimeOffset StartedAtUtc { get; } = timeProvider.GetUtcNow();

    public bool IsReady =>
        Volatile.Read(ref _foundationReady) != 0 &&
        Volatile.Read(ref _ipcReady) != 0;

    public void Initialize(Guid installationId)
    {
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("Installation id must not be empty.", nameof(installationId));
        }

        lock (_identityGate)
        {
            if (_installationId != Guid.Empty && _installationId != installationId)
            {
                throw new InvalidOperationException("Service installation identity was already initialized.");
            }

            _installationId = installationId;
        }
    }

    /// <summary>
    /// Compatibility helper for focused domain tests that do not host the transport layer.
    /// Production startup marks the foundation and IPC listener independently.
    /// </summary>
    public void MarkReady()
    {
        Volatile.Write(ref _foundationReady, 1);
        Volatile.Write(ref _ipcReady, 1);
    }

    public void MarkFoundationReady() => Volatile.Write(ref _foundationReady, 1);

    public void MarkFoundationNotReady() => Volatile.Write(ref _foundationReady, 0);

    public void MarkIpcReady() => Volatile.Write(ref _ipcReady, 1);

    public void MarkIpcNotReady() => Volatile.Write(ref _ipcReady, 0);

    public void MarkNotReady()
    {
        Volatile.Write(ref _ipcReady, 0);
        Volatile.Write(ref _foundationReady, 0);
    }
}
