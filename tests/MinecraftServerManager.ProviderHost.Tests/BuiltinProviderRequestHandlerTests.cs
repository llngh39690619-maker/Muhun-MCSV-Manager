using System.Text.Json;
using MinecraftServerManager.BuiltinProvider;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost.Tests;

public sealed class BuiltinProviderRequestHandlerTests
{
    [Fact]
    public async Task Health_ReturnsBoundedFirstPartyIdentity()
    {
        var request = Request(
            ProductProviderOperations.HealthGet,
            JsonSerializer.SerializeToElement(new { }),
            networkTarget: null);

        var response = await BuiltinProviderRequestHandler.HandleAsync(
            request,
            CancellationToken.None);

        Assert.Equal(ProductProviderRpcProtocol.SuccessStatus, response.Status);
        Assert.Equal(BuiltinProviderRequestHandler.ProviderId, response.Result?.GetProperty("providerId").GetString());
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task Catalogue_RejectsSourceAndExactNetworkHostMismatchWithoutNetworkCall()
    {
        var payload = JsonSerializer.SerializeToElement(
            new ProductProviderModpackSearchRequest(
                ProductModpackCatalogSources.Modrinth,
                Query: "sky",
                Limit: 8));
        var request = Request(
            ProductProviderOperations.ModpackCatalogSearch,
            payload,
            "https://api.feed-the-beast.com/");

        var response = await BuiltinProviderRequestHandler.HandleAsync(
            request,
            CancellationToken.None);

        Assert.Equal(ProductProviderRpcProtocol.ErrorStatus, response.Status);
        Assert.Equal("provider.request_invalid", response.Error?.Code);
        Assert.Null(response.Result);
    }

    [Fact]
    public async Task UnknownPayloadProperties_AreRejectedFailClosed()
    {
        using var payload = JsonDocument.Parse(
            """
            {"source":"modrinth","query":"sky","limit":8,"unexpected":true}
            """);
        var request = Request(
            ProductProviderOperations.ModpackCatalogSearch,
            payload.RootElement.Clone(),
            "https://api.modrinth.com/");

        var response = await BuiltinProviderRequestHandler.HandleAsync(
            request,
            CancellationToken.None);

        Assert.Equal(ProductProviderRpcProtocol.ErrorStatus, response.Status);
        Assert.Equal("provider.request_invalid", response.Error?.Code);
    }

    private static ProductProviderRpcRequest Request(
        string operation,
        JsonElement payload,
        string? networkTarget)
        => new(
            ProductProviderRpcProtocol.CurrentVersion,
            ProductProviderRpcProtocol.RequestMessageType,
            Guid.NewGuid().ToString("N"),
            operation,
            networkTarget,
            payload);
}
