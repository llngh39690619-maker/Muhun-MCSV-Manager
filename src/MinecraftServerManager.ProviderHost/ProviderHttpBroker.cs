using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost;

public interface IProviderHttpBroker
{
    Task<ProductProviderBrokerHttpResponse> SendAsync(
        ProviderRegistration registration,
        Uri invocationTarget,
        ProductProviderBrokerHttpRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The only network path available to an AppContainer provider. Production construction owns a
/// no-proxy/no-redirect client whose connect callback rejects non-public DNS answers. The
/// injectable invoker exists for deterministic policy tests; Service DI always uses the default.
/// </summary>
public sealed class ProviderHttpBroker(HttpMessageInvoker? testInvoker = null) : IProviderHttpBroker
{
    public const int MaximumUriLength = 4096;
    public static readonly TimeSpan MaximumRequestTime = TimeSpan.FromSeconds(35);
    private const string ProductUserAgent = "MuhunMCSVProviderBroker/1.0";

    private static readonly HashSet<string> AllowedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Authorization",
        "Content-Type",
        "If-Modified-Since",
        "If-None-Match",
        "User-Agent",
        "X-Api-Key",
    };

    private static readonly HashSet<string> ReturnedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "ETag",
        "Last-Modified",
        "Retry-After",
    };

    public async Task<ProductProviderBrokerHttpResponse> SendAsync(
        ProviderRegistration registration,
        Uri invocationTarget,
        ProductProviderBrokerHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(invocationTarget);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            ValidateEnvelope(request);
            var target = ValidateTarget(registration, invocationTarget, request.Uri);
            var body = DecodeBody(request.BodyBase64);
            using var message = CreateMessage(request, target, body);
            using var timeout = new CancellationTokenSource(MaximumRequestTime);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            using var ownedInvoker = testInvoker is null ? CreateProductionInvoker() : null;
            var invoker = testInvoker ?? ownedInvoker!;
            using var response = await invoker.SendAsync(message, operation.Token).ConfigureAwait(false);
            var responseBody = await ReadBoundedAsync(response.Content, operation.Token).ConfigureAwait(false);
            return Success(request, response, responseBody);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is
            InvalidDataException or
            FormatException or
            HttpRequestException or
            IOException or
            OperationCanceledException or
            SocketException)
        {
            var policyDenied = error is InvalidDataException or FormatException;
            return Failure(
                request,
                policyDenied ? "provider.broker_denied" : "provider.broker_unavailable",
                policyDenied
                    ? "The provider HTTP request was rejected by host policy."
                    : "The provider HTTP request could not be completed.",
                retryable: !policyDenied);
        }
    }

    private static void ValidateEnvelope(ProductProviderBrokerHttpRequest request)
    {
        if (request.ProtocolVersion != ProductProviderRpcProtocol.CurrentVersion ||
            request.MessageType != ProductProviderRpcProtocol.BrokerHttpRequestMessageType ||
            !IsSafeId(request.RequestId) ||
            !IsSafeId(request.BrokerRequestId) ||
            request.Headers is null ||
            request.Headers.Count > ProductProviderRpcProtocol.MaximumBrokerHttpHeaders)
        {
            throw new InvalidDataException("Provider broker request envelope is invalid.");
        }

        if (request.Method is not ("GET" or "POST" or "HEAD") ||
            request.Headers.Any(pair =>
                !AllowedRequestHeaders.Contains(pair.Key) ||
                !IsSafeHeader(pair.Key, pair.Value)))
        {
            throw new InvalidDataException("Provider broker method or headers are not allowed.");
        }

        if (request.Method is "GET" or "HEAD" && request.BodyBase64 is not null)
        {
            throw new InvalidDataException("Provider broker GET/HEAD requests cannot carry a body.");
        }
    }

    private static Uri ValidateTarget(
        ProviderRegistration registration,
        Uri invocationTarget,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUriLength ||
            !Uri.TryCreate(value, UriKind.Absolute, out var target))
        {
            throw new InvalidDataException("Provider broker target is invalid.");
        }

        ValidateExactHttps(target);
        ValidateExactHttps(invocationTarget);
        if (!target.IdnHost.Equals(invocationTarget.IdnHost, StringComparison.Ordinal) ||
            !registration.Manifest.Permissions.Contains(ProductProviderPermissions.Http, StringComparer.Ordinal) ||
            !registration.Manifest.NetworkHosts.Contains(target.IdnHost, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Provider broker target is outside the signed exact-host scope.");
        }

        return target;
    }

    private static void ValidateExactHttps(Uri target)
    {
        if (!target.IsAbsoluteUri ||
            !target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (!target.IsDefaultPort && target.Port != 443) ||
            target.HostNameType != UriHostNameType.Dns ||
            IPAddress.TryParse(target.Host, out _) ||
            !string.IsNullOrEmpty(target.UserInfo) ||
            !string.IsNullOrEmpty(target.Fragment))
        {
            throw new InvalidDataException("Provider broker requires an exact HTTPS DNS host on port 443.");
        }
    }

    private static byte[]? DecodeBody(string? bodyBase64)
    {
        if (bodyBase64 is null)
        {
            return null;
        }

        if (bodyBase64.Length > ((ProductProviderRpcProtocol.MaximumBrokerHttpRequestBodyBytes + 2) / 3 * 4))
        {
            throw new InvalidDataException("Provider broker request body is too large.");
        }

        var body = Convert.FromBase64String(bodyBase64);
        if (body.Length > ProductProviderRpcProtocol.MaximumBrokerHttpRequestBodyBytes)
        {
            throw new InvalidDataException("Provider broker request body is too large.");
        }

        return body;
    }

    private static HttpRequestMessage CreateMessage(
        ProductProviderBrokerHttpRequest request,
        Uri target,
        byte[]? body)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
        if (body is not null)
        {
            message.Content = new ByteArrayContent(body);
        }

        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Content is null || !MediaTypeHeaderValue.TryParse(value, out var contentType))
                {
                    throw new InvalidDataException("Provider broker content type is invalid.");
                }

                message.Content.Headers.ContentType = contentType;
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                throw new InvalidDataException("Provider broker header is invalid.");
            }
        }

        message.Headers.UserAgent.Clear();
        message.Headers.UserAgent.ParseAdd(ProductUserAgent);
        return message;
    }

    private static HttpMessageInvoker CreateProductionInvoker()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 2,
            PooledConnectionLifetime = TimeSpan.Zero,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = ConnectPublicAddressAsync,
        };
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    private static async ValueTask<Stream> ConnectPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var publicAddresses = addresses.Where(IsPublicAddress).ToArray();
        if (publicAddresses.Length == 0)
        {
            throw new HttpRequestException("Provider broker DNS did not return a public address.");
        }

        Exception? lastError = null;
        foreach (var address in publicAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                        new IPEndPoint(address, context.DnsEndPoint.Port),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                lastError = error;
            }
        }

        throw new HttpRequestException("Provider broker could not connect to a public endpoint.", lastError);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPublicAddress(address.MapToIPv4());
            }

            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) != 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        return octets[0] != 0 &&
               octets[0] != 10 &&
               octets[0] != 127 &&
               !(octets[0] == 100 && octets[1] is >= 64 and <= 127) &&
               !(octets[0] == 169 && octets[1] == 254) &&
               !(octets[0] == 172 && octets[1] is >= 16 and <= 31) &&
               !(octets[0] == 192 && octets[1] == 0 && octets[2] == 0) &&
               !(octets[0] == 192 && octets[1] == 0 && octets[2] == 2) &&
               !(octets[0] == 192 && octets[1] == 168) &&
               !(octets[0] == 198 && octets[1] is 18 or 19) &&
               !(octets[0] == 198 && octets[1] == 51 && octets[2] == 100) &&
               !(octets[0] == 203 && octets[1] == 0 && octets[2] == 113) &&
               octets[0] < 224;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > ProductProviderRpcProtocol.MaximumBrokerHttpResponseBodyBytes)
        {
            throw new InvalidDataException("Provider broker response body is too large.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > ProductProviderRpcProtocol.MaximumBrokerHttpResponseBodyBytes)
            {
                throw new InvalidDataException("Provider broker response body is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ProductProviderBrokerHttpResponse Success(
        ProductProviderBrokerHttpRequest request,
        HttpResponseMessage response,
        byte[] body)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Where(pair => ReturnedResponseHeaders.Contains(pair.Key))
            .Take(ProductProviderRpcProtocol.MaximumBrokerHttpHeaders)
            .ToDictionary(
                pair => pair.Key,
                pair => string.Join(", ", pair.Value).Truncate(1024),
                StringComparer.OrdinalIgnoreCase);
        return new ProductProviderBrokerHttpResponse(
            ProductProviderRpcProtocol.CurrentVersion,
            ProductProviderRpcProtocol.BrokerHttpResponseMessageType,
            request.RequestId,
            request.BrokerRequestId,
            (int)response.StatusCode,
            headers,
            Convert.ToBase64String(body),
            Error: null);
    }

    private static ProductProviderBrokerHttpResponse Failure(
        ProductProviderBrokerHttpRequest request,
        string code,
        string message,
        bool retryable)
        => new(
            ProductProviderRpcProtocol.CurrentVersion,
            ProductProviderRpcProtocol.BrokerHttpResponseMessageType,
            IsSafeId(request.RequestId) ? request.RequestId : "invalid",
            IsSafeId(request.BrokerRequestId) ? request.BrokerRequestId : "invalid",
            StatusCode: 0,
            new Dictionary<string, string>(),
            BodyBase64: null,
            new ProductProviderRpcError(code, message, retryable));

    private static bool IsSafeId(string? value)
        => value is { Length: >= 1 and <= ProductProviderRpcProtocol.MaximumRequestIdLength } &&
           value.All(character => !char.IsControl(character) && !char.IsSurrogate(character));

    private static bool IsSafeHeader(string name, string value)
        => name is { Length: >= 1 and <= 64 } &&
           value is { Length: <= 4096 } &&
           name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
           value.All(character => character is not ('\r' or '\n' or '\0') && !char.IsSurrogate(character));
}

internal static class ProviderBrokerStringExtensions
{
    public static string Truncate(this string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];
}
