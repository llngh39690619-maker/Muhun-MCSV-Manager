using System.Net;
using MinecraftServerManager.BuiltinProvider;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost.Tests;

public sealed class ProviderBrokerHttpMessageHandlerTests
{
    [Fact]
    public async Task BuiltinProviderHttpClient_UsesOnlyRpcBrokerFrames()
    {
        const string invocationId = "invocation-id";
        ProductProviderBrokerHttpRequest? observed = null;
        using var hostInput = new DeferredReadStream();
        using var providerOutput = new CallbackWriteStream(async bytes =>
        {
            await using var requestStream = new MemoryStream(bytes, writable: false);
            observed = await ProviderRpcFrameCodec.ReadAsync<ProductProviderBrokerHttpRequest>(
                requestStream);
            await using var responseStream = new MemoryStream();
            await ProviderRpcFrameCodec.WriteAsync(
                responseStream,
                new ProductProviderBrokerHttpResponse(
                    ProductProviderRpcProtocol.CurrentVersion,
                    ProductProviderRpcProtocol.BrokerHttpResponseMessageType,
                    observed.RequestId,
                    observed.BrokerRequestId,
                    (int)HttpStatusCode.OK,
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/json",
                    },
                    Convert.ToBase64String("brokered"u8),
                    Error: null));
            hostInput.Complete(responseStream.ToArray());
        });
        using var handler = new ProviderBrokerHttpMessageHandler(
            hostInput,
            providerOutput,
            invocationId);
        using var client = new HttpClient(handler, disposeHandler: false);

        var response = await client.GetAsync("https://api.modrinth.com/v2/search");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("brokered", body);
        Assert.NotNull(observed);
        Assert.Equal(invocationId, observed.RequestId);
        Assert.Equal("GET", observed.Method);
        Assert.Equal("https://api.modrinth.com/v2/search", observed.Uri);
    }

    private sealed class DeferredReadStream : Stream
    {
        private readonly TaskCompletionSource<byte[]> _source = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;

        public void Complete(byte[] bytes) => _source.TrySetResult(bytes);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var bytes = await _source.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var remaining = bytes.Length - _offset;
            if (remaining <= 0)
            {
                return 0;
            }

            var count = Math.Min(buffer.Length, remaining);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CallbackWriteStream(Func<byte[], Task> onFlush) : Stream
    {
        private readonly MemoryStream _buffer = new();
        private int _flushed;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _buffer.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _flushed, 1) == 0)
            {
                await onFlush(_buffer.ToArray()).ConfigureAwait(false);
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            _buffer.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
