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
    public void Negotiate_Api17Client_RemainsCompatibleWithoutApi18SettingsCapability()
    {
        var api17 = ProductApiProtocol.ServerPropertiesEditorVersion;

        var result = ProductApiProtocol.Negotiate(api17, api17);

        Assert.True(result.IsCompatible);
        Assert.Equal(api17, result.SelectedVersion);
        Assert.True(
            result.SelectedVersion!.Value.CompareTo(
                ProductApiProtocol.ServiceInstanceSettingsVersion) < 0);
    }

    [Fact]
    public void Negotiate_Api18Client_RemainsCompatibleWithoutApi19RuntimeStatus()
    {
        var api18 = ProductApiProtocol.ServiceInstanceSettingsVersion;

        var result = ProductApiProtocol.Negotiate(api18, api18);

        Assert.True(result.IsCompatible);
        Assert.Equal(api18, result.SelectedVersion);
        Assert.True(api18.CompareTo(ProductApiProtocol.RuntimeStatusVersion) < 0);
        Assert.Equal(new ProductApiVersion(1, 9), ProductApiProtocol.CurrentVersion);
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
