using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost;

public sealed class ProviderRpcProtocolException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public sealed class ProviderProcessCrashedException(
    string message,
    int? exitCode,
    string standardErrorTail,
    Exception? innerException = null) : IOException(message, innerException)
{
    public int? ExitCode { get; } = exitCode;
    public string StandardErrorTail { get; } = standardErrorTail;
}

public sealed class ProviderRpcTimeoutException(string message) : TimeoutException(message);

public static class ProviderRpcFrameCodec
{
    public static async ValueTask WriteAsync<T>(
        Stream destination,
        T value,
        int maximumFrameBytes = ProductProviderRpcProtocol.MaximumFrameBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ValidateMaximum(maximumFrameBytes);
        await using var bounded = new BoundedWriteStream(maximumFrameBytes);
        await JsonSerializer.SerializeAsync(
                bounded,
                value,
                RpcJsonOptions.Options,
                cancellationToken)
            .ConfigureAwait(false);
        var payload = bounded.ToArray();
        if (payload.Length is < 2 || payload.Length > maximumFrameBytes)
        {
            throw new ProviderRpcProtocolException("Provider RPC outbound frame is outside its size limit.");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await destination.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(
        Stream source,
        int maximumFrameBytes = ProductProviderRpcProtocol.MaximumFrameBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateMaximum(maximumFrameBytes);
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(source, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 2 || length > maximumFrameBytes)
        {
            throw new ProviderRpcProtocolException("Provider RPC inbound frame is outside its size limit.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(length);
        await ReadExactlyAsync(source, payload, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions { MaxDepth = RpcJsonOptions.MaximumDepth });
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(RpcJsonOptions.Options)
                   ?? throw new ProviderRpcProtocolException("Provider RPC frame contains JSON null.");
        }
        catch (JsonException error)
        {
            throw new ProviderRpcProtocolException("Provider RPC frame contains invalid or unknown JSON.", error);
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Provider RPC stream ended before a complete frame arrived.");
            }

            offset += read;
        }
    }

    private static void ValidateMaximum(int maximumFrameBytes)
    {
        if (maximumFrameBytes is < 256 or > ProductProviderRpcProtocol.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ProviderRpcProtocolException(
                        "Provider RPC frame contains duplicate JSON properties.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private sealed class BoundedWriteStream(int maximumBytes) : Stream
    {
        private readonly MemoryStream _buffer = new(Math.Min(maximumBytes, 16 * 1024));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _buffer.ToArray();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _buffer.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _buffer.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCapacity(buffer.Length);
            return _buffer.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0 || additionalBytes > maximumBytes - _buffer.Length)
            {
                throw new ProviderRpcProtocolException(
                    "Provider RPC outbound frame is outside its size limit.");
            }
        }
    }
}

public sealed class ProviderRpcSession : IAsyncDisposable
{
    public const int StandardErrorTailBytes = 64 * 1024;
    public const int MaximumBrokerRequestsPerInvocation = 32;
    private readonly IProviderProcess _process;
    private readonly ProviderRegistration? _registration;
    private readonly IProviderHttpBroker? _httpBroker;
    private readonly int _maximumFrameBytes;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly BoundedByteTail _standardErrorTail = new(StandardErrorTailBytes);
    private readonly Task _standardErrorPump;
    private int _terminated;
    private int _disposed;

    public ProviderRpcSession(
        IProviderProcess process,
        int maximumFrameBytes = ProductProviderRpcProtocol.MaximumFrameBytes,
        ProviderRegistration? registration = null,
        IProviderHttpBroker? httpBroker = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        if (maximumFrameBytes is < 256 or > ProductProviderRpcProtocol.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        }

        _maximumFrameBytes = maximumFrameBytes;
        _registration = registration;
        _httpBroker = httpBroker;
        _standardErrorPump = PumpStandardErrorAsync(_lifetime.Token);
    }

    public string StandardErrorTail => _standardErrorTail.GetUtf8Text();

    public async Task<ProductProviderRpcResponse> InvokeAsync(
        ProviderInvocationRequest invocation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (string.IsNullOrWhiteSpace(invocation.Operation) || invocation.Operation.Length > 128 ||
            invocation.Operation.Any(char.IsControl))
        {
            throw new ArgumentException("Provider operation is invalid.", nameof(invocation));
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token,
            _lifetime.Token);
        try
        {
            ThrowIfDisposed();
            EnsureProcessAlive();
            var request = new ProductProviderRpcRequest(
                ProductProviderRpcProtocol.CurrentVersion,
                ProductProviderRpcProtocol.RequestMessageType,
                requestId,
                invocation.Operation,
                invocation.NetworkTarget?.AbsoluteUri,
                invocation.Payload);
            await ProviderRpcFrameCodec.WriteAsync(
                    _process.StandardInput,
                    request,
                    _maximumFrameBytes,
                    operationSource.Token)
                .ConfigureAwait(false);

            var response = await ReadResponseAndServeBrokerAsync(
                    requestId,
                    invocation.NetworkTarget,
                    operationSource.Token)
                .ConfigureAwait(false);

            ValidateResponse(response, requestId);
            return response;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await CancelAndTerminateAsync(requestId).ConfigureAwait(false);
            throw new ProviderRpcTimeoutException("Provider RPC request exceeded its timeout.");
        }
        catch (OperationCanceledException)
        {
            await CancelAndTerminateAsync(requestId).ConfigureAwait(false);
            throw;
        }
        catch
        {
            Terminate();
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<ProductProviderRpcResponse> ReadResponseAndServeBrokerAsync(
        string requestId,
        Uri? invocationTarget,
        CancellationToken cancellationToken)
    {
        for (var brokerCount = 0; brokerCount <= MaximumBrokerRequestsPerInvocation; brokerCount++)
        {
            JsonElement frame;
            try
            {
                frame = await ProviderRpcFrameCodec.ReadAsync<JsonElement>(
                        _process.StandardOutput,
                        _maximumFrameBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException error)
            {
                throw Crash("Provider process exited before returning a complete response.", error);
            }

            if (frame.ValueKind != JsonValueKind.Object ||
                !frame.TryGetProperty("messageType", out var messageTypeValue) ||
                messageTypeValue.ValueKind != JsonValueKind.String)
            {
                throw new ProviderRpcProtocolException("Provider returned an unknown RPC frame.");
            }

            var messageType = messageTypeValue.GetString();
            if (messageType == ProductProviderRpcProtocol.ResponseMessageType)
            {
                try
                {
                    return frame.Deserialize<ProductProviderRpcResponse>(RpcJsonOptions.Options)
                           ?? throw new ProviderRpcProtocolException("Provider returned JSON null.");
                }
                catch (JsonException error)
                {
                    throw new ProviderRpcProtocolException(
                        "Provider returned an invalid response frame.",
                        error);
                }
            }

            if (messageType != ProductProviderRpcProtocol.BrokerHttpRequestMessageType ||
                brokerCount == MaximumBrokerRequestsPerInvocation ||
                _registration is null ||
                _httpBroker is null ||
                invocationTarget is null)
            {
                throw new ProviderRpcProtocolException(
                    "Provider returned an unknown or unavailable broker operation.");
            }

            ProductProviderBrokerHttpRequest brokerRequest;
            try
            {
                brokerRequest = frame.Deserialize<ProductProviderBrokerHttpRequest>(RpcJsonOptions.Options)
                                ?? throw new ProviderRpcProtocolException(
                                    "Provider returned a null broker request.");
            }
            catch (JsonException error)
            {
                throw new ProviderRpcProtocolException("Provider returned an invalid broker request.", error);
            }

            if (!string.Equals(brokerRequest.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new ProviderRpcProtocolException("Provider broker request id does not match its invocation.");
            }

            var brokerResponse = await _httpBroker.SendAsync(
                    _registration,
                    invocationTarget,
                    brokerRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            await ProviderRpcFrameCodec.WriteAsync(
                    _process.StandardInput,
                    brokerResponse,
                    _maximumFrameBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new ProviderRpcProtocolException("Provider exceeded its broker request limit.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        Terminate();
        await _requestGate.WaitAsync().ConfigureAwait(false);
        _requestGate.Release();
        try
        {
            await _standardErrorPump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when a live provider is being disposed.
        }

        await _process.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void ValidateResponse(ProductProviderRpcResponse response, string requestId)
    {
        if (response.ProtocolVersion != ProductProviderRpcProtocol.CurrentVersion ||
            !string.Equals(response.MessageType, ProductProviderRpcProtocol.ResponseMessageType, StringComparison.Ordinal) ||
            response.RequestId is null ||
            response.RequestId.Length is < 1 or > ProductProviderRpcProtocol.MaximumRequestIdLength ||
            !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new ProviderRpcProtocolException("Provider returned an unknown or mismatched RPC response.");
        }

        if (response.Status == ProductProviderRpcProtocol.SuccessStatus)
        {
            if (response.Error is not null)
            {
                throw new ProviderRpcProtocolException("Successful provider response cannot contain an error.");
            }

            return;
        }

        if (response.Status != ProductProviderRpcProtocol.ErrorStatus || response.Error is null ||
            response.Result is not null ||
            string.IsNullOrWhiteSpace(response.Error.Code) || response.Error.Code.Length > 96 ||
            string.IsNullOrWhiteSpace(response.Error.Message) || response.Error.Message.Length > 2048 ||
            !IsSafeErrorCode(response.Error.Code) ||
            response.Error.Message.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new ProviderRpcProtocolException("Provider returned an unknown response status or invalid error.");
        }
    }

    private static bool IsSafeErrorCode(string value) => value.All(character =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private async Task CancelAndTerminateAsync(string requestId)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await ProviderRpcFrameCodec.WriteAsync(
                    _process.StandardInput,
                    new ProductProviderRpcCancellation(
                        ProductProviderRpcProtocol.CurrentVersion,
                        ProductProviderRpcProtocol.CancelMessageType,
                        requestId),
                    _maximumFrameBytes,
                    cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is IOException or ObjectDisposedException or OperationCanceledException or ProviderRpcProtocolException)
        {
            // Cancellation delivery is best-effort; process termination is the isolation boundary.
        }
        finally
        {
            Terminate();
        }
    }

    private async Task PumpStandardErrorAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await _process.StandardError.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                _standardErrorTail.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception error) when (
            error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested && error is not OperationCanceledException)
            {
                _standardErrorTail.Append(Encoding.UTF8.GetBytes("[stderr unavailable]"));
            }
        }
    }

    private void EnsureProcessAlive()
    {
        if (_process.HasExited)
        {
            throw Crash("Provider process exited before accepting a request.");
        }
    }

    private ProviderProcessCrashedException Crash(string message, Exception? error = null) =>
        new(message, _process.ExitCode, StandardErrorTail, error);

    private void Terminate()
    {
        if (Interlocked.Exchange(ref _terminated, 1) == 0)
        {
            _process.Kill();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class BoundedByteTail(int capacity)
    {
        private readonly byte[] _buffer = new byte[capacity];
        private readonly object _sync = new();
        private int _next;
        private int _count;

        public void Append(ReadOnlySpan<byte> value)
        {
            lock (_sync)
            {
                if (value.Length >= _buffer.Length)
                {
                    value[^_buffer.Length..].CopyTo(_buffer);
                    _next = 0;
                    _count = _buffer.Length;
                    return;
                }

                foreach (var item in value)
                {
                    _buffer[_next] = item;
                    _next = (_next + 1) % _buffer.Length;
                    if (_count < _buffer.Length)
                    {
                        _count++;
                    }
                }
            }
        }

        public string GetUtf8Text()
        {
            lock (_sync)
            {
                if (_count == 0)
                {
                    return string.Empty;
                }

                var result = new byte[_count];
                var start = (_next - _count + _buffer.Length) % _buffer.Length;
                var first = Math.Min(_count, _buffer.Length - start);
                Array.Copy(_buffer, start, result, 0, first);
                if (first < _count)
                {
                    Array.Copy(_buffer, 0, result, first, _count - first);
                }

                var text = Encoding.UTF8.GetString(result);
                return new string(text
                    .Select(character => char.IsControl(character) || char.IsSurrogate(character)
                        ? ' '
                        : character)
                    .ToArray());
            }
        }
    }
}

internal static class RpcJsonOptions
{
    public const int MaximumDepth = 32;

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaximumDepth,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
