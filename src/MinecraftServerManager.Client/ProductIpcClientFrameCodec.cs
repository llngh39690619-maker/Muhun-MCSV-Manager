using System.Buffers.Binary;
using System.Text.Json;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Client;

public static class ProductIpcClientFrameCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
    };

    public static async Task WriteRequestAsync(
        Stream output,
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(request);
        var validation = ProductIpcRequestValidator.Validate(request);
        if (validation is not null)
        {
            throw new ArgumentException(validation.Message, nameof(request));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
        if (payload.Length > ProductIpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("IPC request exceeds the maximum frame size.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProductIpcResponse> ReadResponseAsync(
        Stream input,
        Guid expectedRequestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(input, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > ProductIpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("IPC response frame length is outside the allowed range.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(length);
        await ReadExactlyAsync(input, payload, cancellationToken).ConfigureAwait(false);
        ProductIpcResponse response;
        try
        {
            response = JsonSerializer.Deserialize<ProductIpcResponse>(payload, SerializerOptions)
                ?? throw new InvalidDataException("IPC response is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("IPC response JSON is invalid.", exception);
        }

        if (response.SchemaVersion != ProductIpcProtocol.CurrentSchemaVersion ||
            response.RequestId != expectedRequestId ||
            response.Success == (response.Error is not null) ||
            (!response.Success && response.Error is null))
        {
            throw new InvalidDataException("IPC response envelope is inconsistent.");
        }

        return response;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("IPC peer closed before sending a complete frame.");
            }

            offset += read;
        }
    }
}
