using System.Buffers.Binary;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductIpcFoundationTests
{
    [Fact]
    public void ReadyState_ReturnsAuthenticatedLocalIdentity()
    {
        var state = ReadyState();
        var request = ValidRequest();

        var response = new ProductIpcMessageProcessor(state).Process(request);

        Assert.True(response.Success);
        Assert.Null(response.Error);
        Assert.Equal(state.InstallationId, response.Handshake?.InstallationId);
        Assert.Equal(ProductApiProtocol.CurrentVersion, response.Handshake?.Protocol.ApiVersion);
    }

    [Fact]
    public void NotReadyState_FailsClosed()
    {
        var state = new ProductServiceState(TimeProvider.System);

        var response = new ProductIpcMessageProcessor(state).Process(ValidRequest());

        Assert.False(response.Success);
        Assert.Equal("service.not_ready", response.Error?.Code);
    }

    [Theory]
    [InlineData(ProductIpcProtocol.ServerStartMethod, true)]
    [InlineData(ProductIpcProtocol.ServerStartMethod, false)]
    [InlineData(ProductIpcProtocol.ServerRestartMethod, true)]
    [InlineData(ProductIpcProtocol.ServerRestartMethod, false)]
    public void MinecraftEulaConfirmation_RequiresNegotiatedApi16(
        string method,
        bool acceptMinecraftEula)
    {
        var request = ValidRequest() with
        {
            Method = method,
            ServerId = Guid.NewGuid(),
            ClientMaximumApiVersion = new ProductApiVersion(1, 5),
            AcceptMinecraftEula = acceptMinecraftEula,
        };

        var response = new ProductIpcMessageProcessor(ReadyState()).Process(request);

        Assert.False(response.Success);
        Assert.Equal("protocol.field_version_unsupported", response.Error?.Code);
    }

    [Fact]
    public async Task OversizedFrame_IsRejectedBeforeAllocation()
    {
        var frame = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, ProductIpcProtocol.MaximumFrameBytes + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProductIpcFrameCodec.ReadRequestAsync(new MemoryStream(frame), CancellationToken.None));
    }

    [Fact]
    public async Task ResponseFrame_RoundTripsWithBoundedLengthPrefix()
    {
        var response = new ProductIpcMessageProcessor(ReadyState()).Process(ValidRequest());
        await using var output = new MemoryStream();

        await ProductIpcFrameCodec.WriteResponseAsync(output, response, CancellationToken.None);

        var frame = output.ToArray();
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, sizeof(int)));
        Assert.InRange(length, 2, ProductIpcProtocol.MaximumFrameBytes);
        var actual = JsonSerializer.Deserialize<ProductIpcResponse>(frame.AsSpan(sizeof(int), length),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(response, actual);
    }

    private static ProductServiceState ReadyState()
    {
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());
        state.MarkReady();
        return state;
    }

    private static ProductIpcRequest ValidRequest() => new(
        ProductIpcProtocol.CurrentSchemaVersion,
        Guid.NewGuid(),
        ProductIpcProtocol.HandshakeMethod,
        ProductApiProtocol.MinimumSupportedVersion,
        ProductApiProtocol.CurrentVersion);
}
