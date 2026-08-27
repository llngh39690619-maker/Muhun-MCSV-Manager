using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Selects and durably applies the lowest available Minecraft TCP port immediately before each
/// Service-owned launch. In-memory reservations close the gap between selection and the operating
/// system listener becoming visible, while session binding prevents a late exit from releasing a
/// newer launch's reservation.
/// </summary>
public sealed class ProductServerPortCoordinator
{
    private readonly ProductServerRegistry _registry;
    private readonly ProductDataLayout _layout;
    private readonly ServerPropertiesPortService _propertiesService;
    private readonly Func<PortOccupancySnapshot> _captureOccupancy;
    private readonly SemaphoreSlim _assignmentGate = new(1, 1);
    private readonly object _reservationSync = new();
    private readonly Dictionary<Guid, PortReservation> _reservations = [];

    public ProductServerPortCoordinator(
        ProductServerRegistry registry,
        ProductDataLayout layout,
        ServerPropertiesPortService propertiesService)
        : this(registry, layout, propertiesService, SystemPortOccupancy.Capture)
    {
    }

    internal ProductServerPortCoordinator(
        ProductServerRegistry registry,
        ProductDataLayout layout,
        ServerPropertiesPortService propertiesService,
        Func<PortOccupancySnapshot> captureOccupancy)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _propertiesService = propertiesService ?? throw new ArgumentNullException(nameof(propertiesService));
        _captureOccupancy = captureOccupancy ?? throw new ArgumentNullException(nameof(captureOccupancy));
    }

    /// <summary>
    /// Core process-manager preparation hook. The manager already holds the exclusive server
    /// directory lease while this method updates launch configuration.
    /// </summary>
    public async Task PrepareStartAsync(
        ServerInstance launchSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchSnapshot);
        if (launchSnapshot.Id == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(launchSnapshot));
        }

        await _assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PortReservation? reservation = null;
        try
        {
            // Core holds the directory lease for this complete callback. Revalidate the Service
            // ownership chain on every launch before reading or writing any server-owned file;
            // lexical containment alone cannot stop an intermediate junction redirect.
            var verifiedDirectory = SafePath.EnsureNoReparsePointsUnderRoot(
                _layout.Servers,
                launchSnapshot.DirectoryPath);
            var registration = GetMatchingRegistration(launchSnapshot);
            cancellationToken.ThrowIfCancellationRequested();

            var usesVelocityArguments = launchSnapshot.CoreType == CoreType.Velocity;
            if (launchSnapshot.CoreType is CoreType.Waterfall or CoreType.BungeeCord)
            {
                throw new NotSupportedException(
                    $"Automatic launch-port assignment for {launchSnapshot.CoreType} is disabled " +
                    "because its listener port is stored in YAML and a safe atomic YAML editor " +
                    "is not available.");
            }

            if (!usesVelocityArguments && !UsesServerPropertiesPort(launchSnapshot.CoreType))
            {
                throw new NotSupportedException(
                    $"Automatic launch-port assignment for {launchSnapshot.CoreType} is disabled " +
                    "because no verified port-configuration adapter is registered for that core.");
            }

            PortOccupancySnapshot occupancy;
            int[] reservedPorts;
            lock (_reservationSync)
            {
                if (_reservations.ContainsKey(launchSnapshot.Id))
                {
                    throw new InvalidOperationException(
                        $"Server '{launchSnapshot.Id}' already owns an active launch-port reservation.");
                }

                reservedPorts = _reservations.Values.Select(item => item.Port).ToArray();
            }

            occupancy = _captureOccupancy();
            var assignedPort = ServerPortAllocator.FindFirstAvailablePort(
                preferredPort: ServerPortAllocator.DefaultPreferredPort,
                occupiedTcpPorts: occupancy.TcpPorts,
                // Minecraft's primary server-port is TCP. Query and RCON are independent
                // settings, so a UDP-only listener must not force the primary port upward.
                occupiedUdpPorts: null,
                managerReservedPorts: reservedPorts);

            reservation = new PortReservation(assignedPort);
            lock (_reservationSync)
            {
                if (!_reservations.TryAdd(launchSnapshot.Id, reservation))
                {
                    throw new InvalidOperationException(
                        $"Server '{launchSnapshot.Id}' already owns an active launch-port reservation.");
                }
            }

            if (!usesVelocityArguments)
            {
                var propertiesPath = SafePath.EnsureWithinRoot(
                    verifiedDirectory,
                    Path.Combine(verifiedDirectory, "server.properties"),
                    allowRoot: false);
                if (File.Exists(propertiesPath))
                {
                    _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, propertiesPath);
                }

                var configuredPort = await _propertiesService.ReadServerPortAsync(
                        propertiesPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (configuredPort != assignedPort)
                {
                    await _propertiesService.SetServerPortAsync(
                            propertiesPath,
                            assignedPort,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var durableRegistration = await _registry.UpdateLaunchConfigurationAsync(
                    registration.Id,
                    assignedPort,
                    launchSnapshot.CoreType,
                    usesVelocityArguments,
                    cancellationToken)
                .ConfigureAwait(false);
            launchSnapshot.Port = assignedPort;
            if (usesVelocityArguments)
            {
                // Use the arguments normalized from the latest durable registration under its
                // registry gate. A settings write racing an automatic restart is therefore
                // preserved instead of being replaced by the exited session's old snapshot.
                launchSnapshot.ServerArguments = durableRegistration.ServerArguments.ToList();
            }
        }
        catch
        {
            if (reservation is not null)
            {
                RemoveReservationIfCurrent(launchSnapshot.Id, reservation, requireUnbound: true);
            }

            throw;
        }
        finally
        {
            _assignmentGate.Release();
        }
    }

    /// <summary>Core process-manager best-effort cleanup hook for an uncommitted prepared start.</summary>
    public void PreparedStartAborted(Guid instanceId)
    {
        lock (_reservationSync)
        {
            if (_reservations.TryGetValue(instanceId, out var reservation) &&
                reservation.SessionId is null)
            {
                _reservations.Remove(instanceId);
            }
        }
    }

    /// <summary>Binds and releases reservations using the exact Core process-session generation.</summary>
    public void ObserveStateChanged(object? sender, ServerStateChangedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        lock (_reservationSync)
        {
            if (!_reservations.TryGetValue(eventArgs.InstanceId, out var reservation))
            {
                return;
            }

            if (eventArgs.State is ServerState.Starting or ServerState.Running)
            {
                if (reservation.SessionId is null)
                {
                    reservation.SessionId = eventArgs.SessionId;
                }

                return;
            }

            if ((eventArgs.State is ServerState.Stopped or ServerState.Crashed or ServerState.Faulted) &&
                reservation.SessionId == eventArgs.SessionId)
            {
                _reservations.Remove(eventArgs.InstanceId);
            }
        }
    }

    internal bool TryGetReservation(Guid instanceId, out int port, out Guid? sessionId)
    {
        lock (_reservationSync)
        {
            if (_reservations.TryGetValue(instanceId, out var reservation))
            {
                port = reservation.Port;
                sessionId = reservation.SessionId;
                return true;
            }
        }

        port = 0;
        sessionId = null;
        return false;
    }

    private ProductServerRegistration GetMatchingRegistration(ServerInstance launchSnapshot)
    {
        if (!_registry.TryGet(launchSnapshot.Id, out var registration))
        {
            throw new KeyNotFoundException($"Server '{launchSnapshot.Id}' is not registered.");
        }

        var registeredDirectory = ProductServerRegistrationValidator.ResolveOwnedPath(
            _layout.Servers,
            registration.ServerDirectory,
            allowRoot: false);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetFullPath(registeredDirectory),
                Path.GetFullPath(launchSnapshot.DirectoryPath),
                comparison))
        {
            throw new InvalidOperationException(
                "The prepared launch directory no longer matches the Service registration.");
        }

        return registration;
    }

    private static bool UsesServerPropertiesPort(CoreType coreType)
        => coreType is CoreType.Vanilla
            or CoreType.Paper
            or CoreType.Purpur
            or CoreType.Folia
            or CoreType.Spigot
            or CoreType.CraftBukkit
            or CoreType.Fabric
            or CoreType.Forge
            or CoreType.NeoForge
            or CoreType.Mohist
            or CoreType.Arclight
            or CoreType.CatServer
            or CoreType.Akarin;

    private void RemoveReservationIfCurrent(
        Guid instanceId,
        PortReservation expected,
        bool requireUnbound)
    {
        lock (_reservationSync)
        {
            if (_reservations.TryGetValue(instanceId, out var current) &&
                ReferenceEquals(current, expected) &&
                (!requireUnbound || current.SessionId is null))
            {
                _reservations.Remove(instanceId);
            }
        }
    }

    private sealed class PortReservation(int port)
    {
        public int Port { get; } = port;

        public Guid? SessionId { get; set; }
    }
}
