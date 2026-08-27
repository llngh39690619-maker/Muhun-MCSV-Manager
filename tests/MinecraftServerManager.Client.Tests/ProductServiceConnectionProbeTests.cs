using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Client.Tests;

public sealed class ProductServiceConnectionProbeTests
{
    [Fact]
    public async Task Probe_ReportsConnectedOnlyForReadyCompatibleService()
    {
        var client = new StubClient
        {
            Handshake = Handshake(
                ready: true,
                ProductApiProtocol.MinimumSupportedVersion,
                ProductApiProtocol.CurrentVersion),
        };

        var result = await ProductServiceConnectionProbe.ProbeAsync(client);

        Assert.True(result.IsConnected);
        Assert.Equal(ProductServiceConnectionState.Connected, result.State);
        Assert.NotNull(result.Handshake);
    }

    [Fact]
    public async Task Probe_RejectsServiceThatIsNotReady()
    {
        var client = new StubClient
        {
            Handshake = Handshake(
                ready: false,
                ProductApiProtocol.MinimumSupportedVersion,
                ProductApiProtocol.CurrentVersion),
        };

        var result = await ProductServiceConnectionProbe.ProbeAsync(client);

        Assert.Equal(ProductServiceConnectionState.NotReady, result.State);
        Assert.Null(result.Handshake);
    }

    [Theory]
    [InlineData("service.connection_failed", ProductServiceConnectionState.Unavailable)]
    [InlineData("service.timeout", ProductServiceConnectionState.Unavailable)]
    [InlineData("service.access_denied", ProductServiceConnectionState.AccessDenied)]
    [InlineData("service.not_ready", ProductServiceConnectionState.NotReady)]
    [InlineData("protocol.schema_unsupported", ProductServiceConnectionState.Incompatible)]
    [InlineData("runtime.failure", ProductServiceConnectionState.Faulted)]
    public async Task Probe_ClassifiesStableClientCodesWithoutExposingMessage(
        string code,
        ProductServiceConnectionState expected)
    {
        var client = new StubClient
        {
            Error = new ProductServiceClientException(
                code,
                @"sensitive C:\ProgramData\Muhun\MCSV\servers\private"),
        };

        var result = await ProductServiceConnectionProbe.ProbeAsync(client);

        Assert.Equal(expected, result.State);
        Assert.DoesNotContain("ProgramData", result.Code, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Handshake);
    }

    [Fact]
    public async Task Probe_RejectsNonOverlappingApiRange()
    {
        var client = new StubClient
        {
            Handshake = Handshake(
                ready: true,
                new ProductApiVersion(2, 0),
                new ProductApiVersion(2, 1)),
        };

        var result = await ProductServiceConnectionProbe.ProbeAsync(client);

        Assert.Equal(ProductServiceConnectionState.Incompatible, result.State);
    }

    [Fact]
    public async Task Probe_RejectsInvertedServiceApiRange()
    {
        var client = new StubClient
        {
            Handshake = Handshake(
                ready: true,
                ProductApiProtocol.CurrentVersion,
                ProductApiProtocol.MinimumSupportedVersion),
        };

        var result = await ProductServiceConnectionProbe.ProbeAsync(client);

        Assert.Equal(ProductServiceConnectionState.Incompatible, result.State);
        Assert.Null(result.Handshake);
    }

    private static ProductLocalHandshakePayload Handshake(
        bool ready,
        ProductApiVersion minimum,
        ProductApiVersion current)
        => new(
            new ProductHandshakeResponse(
                "Muhun MCSV Manager",
                "1.0.0",
                current,
                minimum,
                ready),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

    private sealed class StubClient : IProductServiceClient
    {
        public ProductLocalHandshakePayload? Handshake { get; init; }

        public ProductServiceClientException? Error { get; init; }

        public Task<ProductLocalHandshakePayload> HandshakeAsync(
            CancellationToken cancellationToken = default)
            => Error is null
                ? Task.FromResult(Handshake!)
                : Task.FromException<ProductLocalHandshakePayload>(Error);

        public Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerStatus> GetStatusAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerStatus> RegisterAsync(
            ProductServerRegistration registration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveAsync(Guid serverId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerMutationResult> StartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerMutationResult> StopAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerMutationResult> RestartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductConsolePage> ReadConsoleAsync(
            Guid serverId,
            long afterCursor,
            int limit = 50,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerStatus> SendCommandAsync(
            Guid serverId,
            string command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
