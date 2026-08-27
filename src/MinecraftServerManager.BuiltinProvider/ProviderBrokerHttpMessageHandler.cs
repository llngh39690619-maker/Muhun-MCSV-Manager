using System.Net;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.ProviderHost;

namespace MinecraftServerManager.BuiltinProvider;

/// <summary>
/// Provider-side adapter for the trusted host HTTP broker. It never creates a socket; requests and
/// bounded responses travel only through the inherited provider RPC pipes.
/// </summary>
internal sealed class ProviderBrokerHttpMessageHandler(
    Stream hostInput,
    Stream hostOutput,
    string invocationRequestId) : HttpMessageHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
        {
            throw new HttpRequestException("Provider broker requires an absolute URI.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (body?.Length > ProductProviderRpcProtocol.MaximumBrokerHttpRequestBodyBytes)
            {
                throw new HttpRequestException("Provider broker request body is too large.");
            }

            var headers = request.Headers
                .Concat(request.Content is null
                    ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
                    : request.Content.Headers)
                .Take(ProductProviderRpcProtocol.MaximumBrokerHttpHeaders + 1)
                .ToDictionary(
                    pair => pair.Key,
                    pair => string.Join(", ", pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            if (headers.Count > ProductProviderRpcProtocol.MaximumBrokerHttpHeaders)
            {
                throw new HttpRequestException("Provider broker request has too many headers.");
            }

            var brokerId = Guid.NewGuid().ToString("N");
            var envelope = new ProductProviderBrokerHttpRequest(
                ProductProviderRpcProtocol.CurrentVersion,
                ProductProviderRpcProtocol.BrokerHttpRequestMessageType,
                invocationRequestId,
                brokerId,
                request.Method.Method.ToUpperInvariant(),
                request.RequestUri.AbsoluteUri,
                headers,
                body is null ? null : Convert.ToBase64String(body));
            await ProviderRpcFrameCodec.WriteAsync(
                    hostOutput,
                    envelope,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var response = await ProviderRpcFrameCodec.ReadAsync<ProductProviderBrokerHttpResponse>(
                    hostInput,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            ValidateResponse(response, brokerId);
            if (response.Error is not null)
            {
                throw new HttpRequestException(
                    response.Error.Message,
                    inner: null,
                    statusCode: null);
            }

            var bytes = response.BodyBase64 is null
                ? []
                : Convert.FromBase64String(response.BodyBase64);
            if (bytes.Length > ProductProviderRpcProtocol.MaximumBrokerHttpResponseBodyBytes)
            {
                throw new HttpRequestException("Provider broker response body is too large.");
            }

            var result = new HttpResponseMessage((HttpStatusCode)response.StatusCode)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(bytes),
            };
            foreach (var (name, value) in response.Headers)
            {
                if (!result.Headers.TryAddWithoutValidation(name, value))
                {
                    _ = result.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return result;
        }
        catch (FormatException error)
        {
            throw new HttpRequestException("Provider broker returned invalid binary data.", error);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ValidateResponse(ProductProviderBrokerHttpResponse response, string brokerId)
    {
        if (response.ProtocolVersion != ProductProviderRpcProtocol.CurrentVersion ||
            response.MessageType != ProductProviderRpcProtocol.BrokerHttpResponseMessageType ||
            response.RequestId != invocationRequestId ||
            response.BrokerRequestId != brokerId ||
            response.Headers is null ||
            response.Headers.Count > ProductProviderRpcProtocol.MaximumBrokerHttpHeaders ||
            response.StatusCode is < 0 or > 599 ||
            (response.Error is null && response.StatusCode < 100) ||
            (response.Error is not null && response.StatusCode != 0))
        {
            throw new HttpRequestException("Provider broker returned an invalid response envelope.");
        }
    }
}
