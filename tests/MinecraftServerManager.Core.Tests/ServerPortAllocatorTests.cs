using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class ServerPortAllocatorTests
{
    [Fact]
    public async Task Capture_IncludesTcpListenerButExcludesConnectedClientPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient(AddressFamily.InterNetwork);
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, listenerPort);
        using var acceptedClient = await acceptTask;
        var connectedClientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var snapshot = SystemPortOccupancy.Capture();

        Assert.Contains(listenerPort, snapshot.TcpPorts);
        Assert.DoesNotContain(connectedClientPort, snapshot.TcpPorts);
    }

    [Fact]
    public void Capture_SourceContract_DoesNotEnumerateActiveTcpConnections()
    {
        var solutionDirectory = GetSolutionDirectory();
        var sourcePath = Path.Combine(
            solutionDirectory,
            "src",
            "MinecraftServerManager.Core",
            "Services",
            "ServerPortAllocator.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("GetActiveTcpListeners()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActiveTcpConnections()", source, StringComparison.Ordinal);
        Assert.Contains("GetActiveUdpListeners()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FindFirstAvailablePort_WithNoOccupancy_UsesPreferredPort()
    {
        var port = ServerPortAllocator.FindFirstAvailablePort();

        Assert.Equal(25565, port);
    }

    [Fact]
    public void FindFirstAvailablePort_CombinesTcpUdpAndManagerReservations()
    {
        var port = ServerPortAllocator.FindFirstAvailablePort(
            preferredPort: 25565,
            occupiedTcpPorts: [25565],
            occupiedUdpPorts: [25566],
            managerReservedPorts: [25567]);

        Assert.Equal(25568, port);
    }

    [Fact]
    public void FindFirstAvailablePort_ReusesReleasedLowerPortOnNextCall()
    {
        var first = ServerPortAllocator.FindFirstAvailablePort(
            occupiedTcpPorts: [25565, 25566]);
        var afterRelease = ServerPortAllocator.FindFirstAvailablePort(
            occupiedTcpPorts: [25566]);

        Assert.Equal(25567, first);
        Assert.Equal(25565, afterRelease);
    }

    [Fact]
    public void FindFirstAvailablePort_CanAllocatePort65535()
    {
        var port = ServerPortAllocator.FindFirstAvailablePort(
            preferredPort: 65534,
            occupiedTcpPorts: [65534]);

        Assert.Equal(65535, port);
    }

    [Fact]
    public void FindFirstAvailablePort_WhenRangeIsExhausted_ThrowsExplicitError()
    {
        var exception = Assert.Throws<NoAvailableServerPortException>(() =>
            ServerPortAllocator.FindFirstAvailablePort(
                preferredPort: 65534,
                occupiedTcpPorts: [65534],
                occupiedUdpPorts: [65535]));

        Assert.Equal(65534, exception.PreferredPort);
        Assert.Equal(65535, exception.MaximumPort);
        Assert.Contains("65534-65535", exception.Message);
    }

    [Fact]
    public void FindFirstAvailablePort_AcceptsOccupancySnapshot()
    {
        var snapshot = new PortOccupancySnapshot(
            new HashSet<int> { 25565 },
            new HashSet<int> { 25566 });

        var port = ServerPortAllocator.FindFirstAvailablePort(snapshot, [25567]);

        Assert.Equal(25568, port);
    }

    private static string GetSolutionDirectory([CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            ".."));
}
