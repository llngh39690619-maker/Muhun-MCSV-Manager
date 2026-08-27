using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MinecraftServerManager.Core.Services;

public sealed record MinecraftStatusProbeResult(
    bool IsHealthy,
    TimeSpan Latency,
    string? VersionName = null,
    int? ProtocolVersion = null,
    int? OnlinePlayers = null,
    int? MaximumPlayers = null,
    string? Error = null);

public interface IMinecraftStatusProbe
{
    Task<MinecraftStatusProbeResult> ProbeAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs a normal Minecraft server-list status request and ping. It does not log in, send a
/// console command, or require Query/RCON. The protocol is supported by Minecraft 1.7 and newer.
/// </summary>
public sealed class MinecraftStatusProbe : IMinecraftStatusProbe
{
    private const int MaximumPacketBytes = 1024 * 1024;
    private const int MaximumStringBytes = 1024 * 1024;

    public async Task<MinecraftStatusProbeResult> ProbeAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var token = timeoutCancellation.Token;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient
            {
                NoDelay = true
            };
            await client.ConnectAsync(host.Trim(), port, token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0);
            // -1 deliberately requests status without claiming a specific client protocol.
            WriteVarInt(handshake, -1);
            WriteString(handshake, host.Trim());
            Span<byte> portBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
            handshake.Write(portBytes);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), token).ConfigureAwait(false);

            await WritePacketAsync(stream, [0], token).ConfigureAwait(false);
            var statusPacket = await ReadPacketAsync(stream, token).ConfigureAwait(false);
            var offset = 0;
            if (ReadVarInt(statusPacket, ref offset) != 0)
            {
                throw new InvalidDataException("Minecraft status response used an unexpected packet ID.");
            }

            var json = ReadString(statusPacket, ref offset);
            string? versionName = null;
            int? protocolVersion = null;
            int? onlinePlayers = null;
            int? maximumPlayers = null;
            using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }))
            {
                var root = document.RootElement;
                if (root.TryGetProperty("version", out var version))
                {
                    if (version.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String)
                    {
                        versionName = name.GetString();
                    }

                    if (version.TryGetProperty("protocol", out var protocol)
                        && protocol.TryGetInt32(out var parsedProtocol))
                    {
                        protocolVersion = parsedProtocol;
                    }
                }

                if (root.TryGetProperty("players", out var players))
                {
                    if (players.TryGetProperty("online", out var online)
                        && online.TryGetInt32(out var parsedOnline))
                    {
                        onlinePlayers = parsedOnline;
                    }

                    if (players.TryGetProperty("max", out var maximum)
                        && maximum.TryGetInt32(out var parsedMaximum))
                    {
                        maximumPlayers = parsedMaximum;
                    }
                }
            }

            var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ping = new byte[9];
            ping[0] = 1;
            BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(1), nonce);
            await WritePacketAsync(stream, ping, token).ConfigureAwait(false);
            var pong = await ReadPacketAsync(stream, token).ConfigureAwait(false);
            offset = 0;
            if (ReadVarInt(pong, ref offset) != 1 || pong.Length - offset != sizeof(long))
            {
                throw new InvalidDataException("Minecraft ping response was malformed.");
            }

            var returnedNonce = BinaryPrimitives.ReadInt64BigEndian(pong.AsSpan(offset));
            if (returnedNonce != nonce)
            {
                throw new InvalidDataException("Minecraft ping response did not match the request.");
            }

            stopwatch.Stop();
            return new MinecraftStatusProbeResult(
                true,
                stopwatch.Elapsed,
                versionName,
                protocolVersion,
                onlinePlayers,
                maximumPlayers);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new MinecraftStatusProbeResult(false, stopwatch.Elapsed, Error: "健康檢查逾時。");
        }
        catch (Exception error) when (error is SocketException
                                           or IOException
                                           or InvalidDataException
                                           or JsonException)
        {
            stopwatch.Stop();
            return new MinecraftStatusProbeResult(false, stopwatch.Elapsed, Error: error.Message);
        }
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        using var packet = new MemoryStream(payload.Length + 5);
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.GetBuffer().AsMemory(0, checked((int)packet.Length)), cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length is < 1 or > MaximumPacketBytes)
        {
            throw new InvalidDataException($"Minecraft packet length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = 0;
        for (var index = 0; index < 5; index++)
        {
            var buffer = new byte[1];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            var current = buffer[0];
            result |= (current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("Minecraft VarInt exceeds five bytes.");
    }

    private static int ReadVarInt(ReadOnlySpan<byte> data, ref int offset)
    {
        var result = 0;
        for (var index = 0; index < 5; index++)
        {
            if (offset >= data.Length)
            {
                throw new InvalidDataException("Minecraft packet ended inside a VarInt.");
            }

            var current = data[offset++];
            result |= (current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("Minecraft VarInt exceeds five bytes.");
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = unchecked((uint)value);
        do
        {
            var current = (byte)(remaining & 0x7F);
            remaining >>= 7;
            if (remaining != 0)
            {
                current |= 0x80;
            }

            stream.WriteByte(current);
        }
        while (remaining != 0);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = ReadVarInt(data, ref offset);
        if (length < 0 || length > MaximumStringBytes || length > data.Length - offset)
        {
            throw new InvalidDataException("Minecraft status string length is invalid.");
        }

        var value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return value;
    }
}
