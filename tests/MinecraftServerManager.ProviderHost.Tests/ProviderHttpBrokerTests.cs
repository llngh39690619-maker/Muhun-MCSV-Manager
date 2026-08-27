using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost.Tests;

public sealed class ProviderHttpBrokerTests
{
    [Fact]
    public async Task ExactManifestAndInvocationHost_IsBrokeredWithBoundedResponse()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("trusted"u8.ToArray()),
        });
        var broker = new ProviderHttpBroker(new HttpMessageInvoker(handler));
        var request = Request("https://api.example.com/v1/catalog");

        var response = await broker.SendAsync(
            Registration(),
            new Uri("https://api.example.com/"),
            request,
            CancellationToken.None);

        Assert.Null(response.Error);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("trusted", Encoding.UTF8.GetString(Convert.FromBase64String(response.BodyBase64!)));
        Assert.Equal(new Uri("https://api.example.com/v1/catalog"), handler.LastTarget);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("https://other.example.com/v1")]
    [InlineData("https://sub.api.example.com/v1")]
    [InlineData("http://api.example.com/v1")]
    [InlineData("https://api.example.com:8443/v1")]
    [InlineData("https://127.0.0.1/v1")]
    public async Task UnlistedOrUnsafeTarget_IsDeniedBeforeNetwork(string target)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var broker = new ProviderHttpBroker(new HttpMessageInvoker(handler));

        var response = await broker.SendAsync(
            Registration(),
            new Uri("https://api.example.com/"),
            Request(target),
            CancellationToken.None);

        Assert.Equal("provider.broker_denied", response.Error?.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task OversizeResponse_IsRejectedWithoutCrossingRpcBoundary()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[
                ProductProviderRpcProtocol.MaximumBrokerHttpResponseBodyBytes + 1]),
        });
        var broker = new ProviderHttpBroker(new HttpMessageInvoker(handler));

        var response = await broker.SendAsync(
            Registration(),
            new Uri("https://api.example.com/"),
            Request("https://api.example.com/large"),
            CancellationToken.None);

        Assert.Equal("provider.broker_denied", response.Error?.Code);
        Assert.Null(response.BodyBase64);
    }

    private static ProductProviderBrokerHttpRequest Request(string target) => new(
        ProductProviderRpcProtocol.CurrentVersion,
        ProductProviderRpcProtocol.BrokerHttpRequestMessageType,
        "invocation",
        "broker",
        "GET",
        target,
        new Dictionary<string, string> { ["Accept"] = "application/json" },
        BodyBase64: null);

    private static ProviderRegistration Registration()
    {
        var manifest = new ProductProviderManifest(
            ProductProviderManifestValidator.CurrentSchemaVersion,
            "example.catalog",
            "Example",
            "1.0.0",
            ProductApiProtocol.CurrentVersion,
            "provider.exe",
            [ProductProviderCapabilities.ModpackCatalog],
            [ProductProviderPermissions.Http],
            ["api.example.com"],
            new Dictionary<string, string>
            {
                ["provider.exe"] = Convert.ToHexString(SHA256.HashData([1])).ToLowerInvariant(),
            });
        var now = DateTimeOffset.UtcNow;
        return new ProviderRegistration(
            manifest,
            "muhun.test",
            new string('a', 64),
            "packages/example.catalog/1.0.0",
            true,
            ProviderHealthStatus.Stopped,
            now,
            now,
            0,
            null);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? LastTarget { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastTarget = request.RequestUri;
            return Task.FromResult(response(request));
        }
    }
}
