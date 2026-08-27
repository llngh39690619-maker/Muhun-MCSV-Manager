using System.Text.Json;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductApiProtocolTests
{
    [Fact]
    public void Negotiate_ExactVersion_IsCompatible()
    {
        var result = ProductApiProtocol.Negotiate(new ProductApiVersion(1, 0), new ProductApiVersion(1, 0));

        Assert.True(result.IsCompatible);
        Assert.Equal(new ProductApiVersion(1, 0), result.SelectedVersion);
    }

    [Fact]
    public void Negotiate_FutureMajor_IsRejected()
    {
        var result = ProductApiProtocol.Negotiate(new ProductApiVersion(2, 0), new ProductApiVersion(2, 2));

        Assert.False(result.IsCompatible);
        Assert.Equal(ProductApiNegotiationStatus.ClientTooNew, result.Status);
        Assert.Null(result.SelectedVersion);
    }

    [Fact]
    public void Negotiate_ClientRangeSpanningFutureMajor_SelectsCurrentVersion()
    {
        var result = ProductApiProtocol.Negotiate(new ProductApiVersion(1, 0), new ProductApiVersion(2, 0));

        Assert.True(result.IsCompatible);
        Assert.Equal(ProductApiProtocol.CurrentVersion, result.SelectedVersion);
    }

    [Fact]
    public void Negotiate_InvalidRange_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ProductApiProtocol.Negotiate(new ProductApiVersion(1, 2), new ProductApiVersion(1, 1)));
    }

    [Fact]
    public void Handshake_JsonRoundTrip_PreservesProtocolFields()
    {
        var expected = new ProductHandshakeResponse(
            "Muhun MCSV Manager",
            "1.0.0-alpha.1",
            ProductApiProtocol.CurrentVersion,
            ProductApiProtocol.MinimumSupportedVersion,
            Ready: true);

        var json = JsonSerializer.Serialize(expected);
        var actual = JsonSerializer.Deserialize<ProductHandshakeResponse>(json);

        Assert.Equal(expected, actual);
    }
}
