using System.Buffers.Binary;
using System.Text.Json;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost.Tests;

public sealed class ProviderRpcSessionTests
{
    [Fact]
    public async Task FrameCodec_RejectsOversizeOutboundValueWithoutWritingPartialFrame()
    {
        await using var destination = new MemoryStream();
        var value = new { text = new string('x', ProductProviderRpcProtocol.MaximumFrameBytes + 1) };

        await Assert.ThrowsAsync<ProviderRpcProtocolException>(async () =>
            await ProviderRpcFrameCodec.WriteAsync(destination, value));

        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task InvokeAsync_AcceptsVersionedMatchingResponse()
    {
        await using var process = FakeProviderProcess.Responding((requestId, _) =>
            Response(requestId, ProductProviderRpcProtocol.ResponseMessageType));
        await using var session = new ProviderRpcSession(process);

        var result = await session.InvokeAsync(Request(), TimeSpan.FromSeconds(1));

        Assert.Equal(ProductProviderRpcProtocol.SuccessStatus, result.Status);
        Assert.Equal(ProductProviderRpcProtocol.CurrentVersion, result.ProtocolVersion);
        Assert.NotEmpty(process.WrittenBytes);
    }

    [Fact]
    public async Task InvokeAsync_RejectsOversizeFrameBeforeAllocatingPayloadAndKillsProvider()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, ProductProviderRpcProtocol.MaximumFrameBytes + 1);
        await using var process = new FakeProviderProcess(
            new MemoryStream(prefix, writable: false),
            new MemoryStream());
        await using var session = new ProviderRpcSession(process);

        var error = await Assert.ThrowsAsync<ProviderRpcProtocolException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromSeconds(1)));

        Assert.Contains("size limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task InvokeAsync_TimesOutSendsCancellationAndTerminatesProvider()
    {
        await using var process = new FakeProviderProcess(new NeverCompletingStream(), new MemoryStream());
        await using var session = new ProviderRpcSession(process);

        await Assert.ThrowsAsync<ProviderRpcTimeoutException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromMilliseconds(30)));

        Assert.Equal(1, process.KillCount);
        Assert.Contains(
            ProductProviderRpcProtocol.CancelMessageType,
            System.Text.Encoding.UTF8.GetString(process.WrittenBytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_CallerCancellationTerminatesProvider()
    {
        await using var process = new FakeProviderProcess(new NeverCompletingStream(), new MemoryStream());
        await using var session = new ProviderRpcSession(process);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromSeconds(2), cancellation.Token));

        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task InvokeAsync_EndOfStdoutIsIsolatedAsProviderCrash()
    {
        await using var process = new FakeProviderProcess(new MemoryStream(), new MemoryStream("crash-tail"u8.ToArray()));
        await using var session = new ProviderRpcSession(process);

        var error = await Assert.ThrowsAsync<ProviderProcessCrashedException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromSeconds(1)));

        Assert.Contains("crash-tail", error.StandardErrorTail, StringComparison.Ordinal);
        Assert.Equal(1, process.KillCount);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("request")]
    [InlineData("")]
    public async Task InvokeAsync_RejectsUnknownResponseType(string responseType)
    {
        await using var process = FakeProviderProcess.Responding((requestId, _) =>
            Response(requestId, responseType));
        await using var session = new ProviderRpcSession(process);

        var error = await Assert.ThrowsAsync<ProviderRpcProtocolException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromSeconds(1)));

        Assert.Contains("unknown", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task InvokeAsync_RejectsMismatchedRequestId()
    {
        await using var process = FakeProviderProcess.Responding((_, _) =>
            Response(Guid.NewGuid().ToString("N"), ProductProviderRpcProtocol.ResponseMessageType));
        await using var session = new ProviderRpcSession(process);

        await Assert.ThrowsAsync<ProviderRpcProtocolException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromSeconds(1)));

        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task InvokeAsync_RejectsInvalidProviderErrorCode()
    {
        await using var process = FakeProviderProcess.Responding((requestId, _) =>
        {
            var response = new ProductProviderRpcResponse(
                ProductProviderRpcProtocol.CurrentVersion,
                ProductProviderRpcProtocol.ResponseMessageType,
                requestId,
                ProductProviderRpcProtocol.ErrorStatus,
                null,
                new ProductProviderRpcError("INVALID CODE", "provider failed"));
            return Frame(JsonSerializer.SerializeToUtf8Bytes(
                response,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        await using var session = new ProviderRpcSession(process);

        await Assert.ThrowsAsync<ProviderRpcProtocolException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromSeconds(1)));

        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsActiveRequestBeforeDisposingProcessStreams()
    {
        await using var process = new FakeProviderProcess(new NeverCompletingStream(), new MemoryStream());
        var session = new ProviderRpcSession(process);
        var invocation = session.InvokeAsync(Request(), TimeSpan.FromSeconds(5));
        await Task.Delay(20);

        await session.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task InvokeAsync_RejectsUnknownJsonResponseMember()
    {
        await using var process = FakeProviderProcess.Responding((requestId, _) =>
        {
            var json = $$"""
                         {"protocolVersion":1,"messageType":"response","requestId":"{{requestId}}","status":"success","result":{},"error":null,"surprise":true}
                         """;
            return Frame(System.Text.Encoding.UTF8.GetBytes(json));
        }, responseAlreadyFramed: true);
        await using var session = new ProviderRpcSession(process);

        await Assert.ThrowsAsync<ProviderRpcProtocolException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromSeconds(1)));

        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task InvokeAsync_RejectsDuplicateJsonResponseMember()
    {
        await using var process = FakeProviderProcess.Responding((requestId, _) =>
        {
            var json = $$"""
                         {"protocolVersion":1,"messageType":"response","requestId":"{{requestId}}","requestId":"{{requestId}}","status":"success","result":{},"error":null}
                         """;
            return Frame(System.Text.Encoding.UTF8.GetBytes(json));
        }, responseAlreadyFramed: true);
        await using var session = new ProviderRpcSession(process);

        var error = await Assert.ThrowsAsync<ProviderRpcProtocolException>(() =>
            session.InvokeAsync(Request(), TimeSpan.FromSeconds(1)));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task StandardErrorCapture_IsBoundedToTail()
    {
        var stderr = Enumerable.Repeat((byte)'x', ProviderRpcSession.StandardErrorTailBytes + 8192).ToArray();
        await using var process = FakeProviderProcess.Responding(
            (requestId, _) => Response(requestId, ProductProviderRpcProtocol.ResponseMessageType),
            standardError: stderr);
        await using var session = new ProviderRpcSession(process);

        await session.InvokeAsync(Request(), TimeSpan.FromSeconds(1));

        Assert.Equal(ProviderRpcSession.StandardErrorTailBytes, session.StandardErrorTail.Length);
        Assert.All(session.StandardErrorTail, character => Assert.Equal('x', character));
    }

    private static ProviderInvocationRequest Request() => new(
        ProductProviderOperations.HealthGet,
        JsonSerializer.SerializeToElement(new { probe = true }));

    private static byte[] Response(string requestId, string messageType)
    {
        var response = new ProductProviderRpcResponse(
            ProductProviderRpcProtocol.CurrentVersion,
            messageType,
            requestId,
            ProductProviderRpcProtocol.SuccessStatus,
            JsonSerializer.SerializeToElement(new { ready = true }),
            null);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Frame(payload);
    }

    private static byte[] Frame(byte[] payload)
    {
        var result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(result, payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private sealed class FakeProviderProcess : IProviderProcess
    {
        private readonly MemoryStream _input = new();
        private readonly Stream _output;
        private readonly Stream _error;

        public FakeProviderProcess(Stream output, Stream error)
        {
            _output = output;
            _error = error;
        }

        public Stream StandardInput => _input;
        public Stream StandardOutput => _output;
        public Stream StandardError => _error;
        public bool HasExited { get; set; }
        public int? ExitCode => HasExited ? 1 : null;
        public int KillCount { get; private set; }
        public byte[] WrittenBytes => _input.ToArray();

        public static FakeProviderProcess Responding(
            Func<string, string, byte[]> response,
            bool responseAlreadyFramed = true,
            byte[]? standardError = null)
        {
            FakeProviderProcess? process = null;
            var deferred = new DeferredReadStream(() =>
            {
                var input = process!._input.ToArray();
                var length = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(0, 4));
                using var request = JsonDocument.Parse(input.AsMemory(4, length));
                var requestId = request.RootElement.GetProperty("requestId").GetString()!;
                var operation = request.RootElement.GetProperty("operation").GetString()!;
                var bytes = response(requestId, operation);
                return responseAlreadyFramed ? bytes : Frame(bytes);
            });
            process = new FakeProviderProcess(
                deferred,
                new MemoryStream(standardError ?? [], writable: false));
            return process;
        }

        public void Kill()
        {
            KillCount++;
            HasExited = true;
        }

        public ValueTask DisposeAsync()
        {
            _input.Dispose();
            _output.Dispose();
            _error.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeferredReadStream(Func<byte[]> factory) : Stream
    {
        private byte[]? _value;
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _value ??= factory();
            var remaining = _value.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var read = Math.Min(count, remaining);
            Array.Copy(_value, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _value ??= factory();
            var remaining = _value.Length - _position;
            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var read = Math.Min(buffer.Length, remaining);
            _value.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
