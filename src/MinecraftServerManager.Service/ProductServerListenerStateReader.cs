using System.ComponentModel;
using System.Net.NetworkInformation;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Provides a short-lived, machine-level TCP-listener snapshot for lightweight status polling.
/// One operating-system query serves every server row in a poll interval. The result observes a
/// port, not PID ownership, so callers must present it as a readiness diagnostic.
/// </summary>
public sealed class ProductServerListenerStateReader
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMilliseconds(750);
    private readonly Func<PortOccupancySnapshot> _capture;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private IReadOnlySet<int> _tcpPorts = new HashSet<int>();
    private DateTimeOffset _expiresAtUtc;
    private bool _captureAvailable;

    public ProductServerListenerStateReader(TimeProvider timeProvider)
        : this(SystemPortOccupancy.CaptureTcp, timeProvider)
    {
    }

    internal ProductServerListenerStateReader(
        Func<PortOccupancySnapshot> capture,
        TimeProvider timeProvider)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool? TryIsListening(int port)
    {
        if (port is < 1 or > ServerPortAllocator.MaximumPort)
        {
            return null;
        }

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (now >= _expiresAtUtc)
            {
                try
                {
                    var snapshot = _capture();
                    _tcpPorts = snapshot.TcpPorts.ToHashSet();
                    _captureAvailable = true;
                }
                catch (Exception error) when (error is NetworkInformationException or
                                                    Win32Exception or
                                                    InvalidOperationException or
                                                    PlatformNotSupportedException)
                {
                    _tcpPorts = new HashSet<int>();
                    _captureAvailable = false;
                }

                _expiresAtUtc = now + CacheLifetime;
            }

            return _captureAvailable ? _tcpPorts.Contains(port) : null;
        }
    }
}
