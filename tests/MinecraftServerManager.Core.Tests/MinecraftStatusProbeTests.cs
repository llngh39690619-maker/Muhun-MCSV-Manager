using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class MinecraftStatusProbeTests
{
    [Fact]
    public async Task ProbeAsync_CompletesStatusAndPongWithoutConsoleCommand()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var handshake = await ReadPacketAsync(stream);
            Assert.NotEmpty(handshake);
            Assert.Equal([0], await ReadPacketAsync(stream));

            const string status = "{\"version\":{\"name\":\"Paper 1.21.1\",\"protocol\":767},"
                                  + "\"players\":{\"max\":20,\"online\":3},\"description\":\"ok\"}";
            using var response = new MemoryStream();
            WriteVarInt(response, 0);
            WriteString(response, status);
            await WritePacketAsync(stream, response.ToArray());

            var ping = await ReadPacketAsync(stream);
            Assert.Equal(9, ping.Length);
            Assert.Equal(1, ping[0]);
            await WritePacketAsync(stream, ping);
        });

        var result = await new MinecraftStatusProbe().ProbeAsync(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(3));

        Assert.True(result.IsHealthy, result.Error);
        Assert.Equal("Paper 1.21.1", result.VersionName);
        Assert.Equal(767, result.ProtocolVersion);
        Assert.Equal(3, result.OnlinePlayers);
        Assert.Equal(20, result.MaximumPlayers);
        await server;
    }

    [Fact]
    public async Task ProbeAsync_TimeoutReturnsUnhealthyInsteadOfThrowing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        var result = await new MinecraftStatusProbe().ProbeAsync(
            "127.0.0.1",
            port,
            TimeSpan.FromMilliseconds(150));

        using var client = await accepted;
        Assert.False(result.IsHealthy);
        Assert.Contains("逾時", result.Error);
    }

    [Fact]
    public async Task ProbeAsync_RejectsMismatchedPong()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadPacketAsync(stream);
            _ = await ReadPacketAsync(stream);
            using var response = new MemoryStream();
            WriteVarInt(response, 0);
            WriteString(response, "{\"version\":{\"name\":\"test\",\"protocol\":1},\"players\":{\"max\":0,\"online\":0}}");
            await WritePacketAsync(stream, response.ToArray());
            _ = await ReadPacketAsync(stream);
            var wrongPong = new byte[9];
            wrongPong[0] = 1;
            BinaryPrimitives.WriteInt64BigEndian(wrongPong.AsSpan(1), 42);
            await WritePacketAsync(stream, wrongPong);
        });

        var result = await new MinecraftStatusProbe().ProbeAsync(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(3));

        Assert.False(result.IsHealthy);
        Assert.Contains("match", result.Error, StringComparison.OrdinalIgnoreCase);
        await server;
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream)
    {
        var length = await ReadVarIntAsync(stream);
        var data = new byte[length];
        await stream.ReadExactlyAsync(data);
        return data;
    }

    private static async Task<int> ReadVarIntAsync(Stream stream)
    {
        var result = 0;
        for (var index = 0; index < 5; index++)
        {
            var one = new byte[1];
            await stream.ReadExactlyAsync(one);
            result |= (one[0] & 0x7F) << (index * 7);
            if ((one[0] & 0x80) == 0) return result;
        }

        throw new InvalidDataException();
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray());
        await stream.FlushAsync();
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = unchecked((uint)value);
        do
        {
            var current = (byte)(remaining & 0x7F);
            remaining >>= 7;
            if (remaining != 0) current |= 0x80;
            stream.WriteByte(current);
        }
        while (remaining != 0);
    }
}
