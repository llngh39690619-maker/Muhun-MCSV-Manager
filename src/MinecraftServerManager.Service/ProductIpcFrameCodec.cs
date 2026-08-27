using System.Buffers.Binary;
using System.Text.Json;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Service;

public static class ProductIpcFrameCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
    };

    public static async Task<ProductIpcRequest?> ReadRequestAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var lengthBuffer = new byte[sizeof(int)];
        await ReadExactlyAsync(input, lengthBuffer, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length is < 2 or > ProductIpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("IPC frame length is outside the allowed range.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(input, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ProductIpcRequest>(payload, SerializerOptions);
    }

    public static async Task WriteResponseAsync(
        Stream output,
        ProductIpcResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(response);
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions);
        if (payload.Length > ProductIpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("IPC response exceeds the maximum frame size.");
        }

        var lengthBuffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payload.Length);
        await output.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("IPC peer closed before sending a complete frame.");
            }

            total += read;
        }
    }
}
