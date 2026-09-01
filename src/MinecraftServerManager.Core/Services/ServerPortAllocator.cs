using System.Net.NetworkInformation;

namespace MinecraftServerManager.Core.Services;

/// <summary>The local TCP and UDP ports reported as occupied by the operating system.</summary>
public sealed record PortOccupancySnapshot(
    IReadOnlySet<int> TcpPorts,
    IReadOnlySet<int> UdpPorts);

/// <summary>Reads the current local port usage without retaining allocation state.</summary>
public static class SystemPortOccupancy
{
    /// <summary>
    /// Captures only TCP listeners for Minecraft's primary server port.  Querying UDP listeners
    /// can be materially slower on Windows and cannot affect the primary TCP assignment.
    /// </summary>
    public static PortOccupancySnapshot CaptureTcp()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var tcpPorts = properties.GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port)
            .ToHashSet();
        return new PortOccupancySnapshot(tcpPorts, new HashSet<int>());
    }

    public static PortOccupancySnapshot Capture()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var tcpPorts = properties.GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port)
            .ToHashSet();
        var udpPorts = properties.GetActiveUdpListeners()
            .Select(endpoint => endpoint.Port)
            .ToHashSet();

        return new PortOccupancySnapshot(tcpPorts, udpPorts);
    }
}

/// <summary>
/// Selects a server port from a point-in-time occupancy snapshot. The selector is deliberately
/// stateless, so a lower-numbered port is reused as soon as it is absent from a later snapshot.
/// </summary>
public static class ServerPortAllocator
{
    public const int DefaultPreferredPort = 25565;

    public const int MaximumPort = 65535;

    public static int FindFirstAvailablePort(
        int preferredPort = DefaultPreferredPort,
        IEnumerable<int>? occupiedTcpPorts = null,
        IEnumerable<int>? occupiedUdpPorts = null,
        IEnumerable<int>? managerReservedPorts = null,
        int maximumPort = MaximumPort)
    {
        ValidateRange(preferredPort, maximumPort);

        var unavailablePorts = new HashSet<int>();
        AddPorts(unavailablePorts, occupiedTcpPorts);
        AddPorts(unavailablePorts, occupiedUdpPorts);
        AddPorts(unavailablePorts, managerReservedPorts);

        for (var candidate = preferredPort; candidate <= maximumPort; candidate++)
        {
            if (!unavailablePorts.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new NoAvailableServerPortException(preferredPort, maximumPort);
    }

    public static int FindFirstAvailablePort(
        PortOccupancySnapshot occupancy,
        IEnumerable<int>? managerReservedPorts = null,
        int preferredPort = DefaultPreferredPort,
        int maximumPort = MaximumPort)
    {
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(occupancy.TcpPorts);
        ArgumentNullException.ThrowIfNull(occupancy.UdpPorts);

        return FindFirstAvailablePort(
            preferredPort,
            occupancy.TcpPorts,
            occupancy.UdpPorts,
            managerReservedPorts,
            maximumPort);
    }

    private static void AddPorts(HashSet<int> destination, IEnumerable<int>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var port in source)
        {
            destination.Add(port);
        }
    }

    private static void ValidateRange(int preferredPort, int maximumPort)
    {
        if (preferredPort is < 1 or > MaximumPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferredPort),
                preferredPort,
                $"The preferred port must be between 1 and {MaximumPort}.");
        }

        if (maximumPort is < 1 or > MaximumPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPort),
                maximumPort,
                $"The maximum port must be between 1 and {MaximumPort}.");
        }

        if (maximumPort < preferredPort)
        {
            throw new ArgumentException(
                "The maximum port cannot be lower than the preferred port.",
                nameof(maximumPort));
        }
    }
}

public sealed class NoAvailableServerPortException : InvalidOperationException
{
    public NoAvailableServerPortException(int preferredPort, int maximumPort)
        : base($"No available server port exists in the range {preferredPort}-{maximumPort}.")
    {
        PreferredPort = preferredPort;
        MaximumPort = maximumPort;
    }

    public int PreferredPort { get; }

    public int MaximumPort { get; }
}
