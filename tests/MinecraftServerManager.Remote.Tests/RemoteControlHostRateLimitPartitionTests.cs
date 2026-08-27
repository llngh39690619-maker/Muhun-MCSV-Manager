using System.Net;
using Microsoft.AspNetCore.Http;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteControlHostRateLimitPartitionTests
{
    [Theory]
    [InlineData("203.0.113.10", "public-quick-tunnel:203.0.113.10")]
    [InlineData("2001:0db8:0:0:0:0:0:10", "public-quick-tunnel:2001:db8::10")]
    [InlineData("::ffff:203.0.113.10", "public-quick-tunnel:203.0.113.10")]
    public void QuickTunnel_LoopbackProxyUsesCanonicalClientAddress(
        string headerValue,
        string expectedPartition)
    {
        var context = CreateContext(IPAddress.Loopback, headerValue);

        var partition = RemoteControlHost.GetLoginPartition(
            context,
            CreateQuickTunnelOptions(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(expectedPartition, partition);
    }

    [Fact]
    public void QuickTunnel_NonLoopbackPeerCannotForgeCloudflareClientAddress()
    {
        var context = CreateContext(
            IPAddress.Parse("192.0.2.40"),
            "203.0.113.10");

        var partition = RemoteControlHost.GetLoginPartition(
            context,
            CreateQuickTunnelOptions(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("public-quick-tunnel:unattributed", partition);
    }

    [Fact]
    public void NamedTunnel_UsesTheSamePublicLoopbackProxyTrustBoundary()
    {
        var loopback = CreateContext(IPAddress.Loopback, "203.0.113.44");
        var forged = CreateContext(IPAddress.Parse("192.0.2.44"), "203.0.113.44");
        var options = new RemoteControlOptions
        {
            PublicOrigin = new Uri("https://mcsv.example.com/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareNamedTunnel
        };

        Assert.Equal(
            "public-quick-tunnel:203.0.113.44",
            RemoteControlHost.GetLoginPartition(
                loopback,
                options,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        Assert.Equal(
            "public-quick-tunnel:unattributed",
            RemoteControlHost.GetLoginPartition(
                forged,
                options,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Funnel_NeverTrustsForgedIdentityOrCloudflareAddressHeaders()
    {
        var context = CreateContext(IPAddress.Loopback, "203.0.113.44");
        context.Request.Headers[RemoteControlOptions.TailscaleLoginHeaderName] = "owner@gmail.com";
        var options = new RemoteControlOptions
        {
            PublicOrigin = new Uri("https://manager-node.example.ts.net/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.TailscaleFunnel
        };

        Assert.Equal(
            "public-funnel",
            RemoteControlHost.GetLoginPartition(
                context,
                options,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void QuickTunnel_Ipv4MappedLoopbackProxyIsTrusted()
    {
        var context = CreateContext(
            IPAddress.Parse("::ffff:127.0.0.1"),
            "203.0.113.10");

        var partition = RemoteControlHost.GetLoginPartition(
            context,
            CreateQuickTunnelOptions(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("public-quick-tunnel:203.0.113.10", partition);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("fe80::1%12")]
    public void QuickTunnel_InvalidAddressUsesSharedFailSafePartition(string headerValue)
    {
        var context = CreateContext(IPAddress.IPv6Loopback, headerValue);

        var partition = RemoteControlHost.GetLoginPartition(
            context,
            CreateQuickTunnelOptions(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("public-quick-tunnel:unattributed", partition);
    }

    [Fact]
    public void QuickTunnel_MultipleClientAddressValuesUseSharedFailSafePartition()
    {
        var context = CreateContext(
            IPAddress.Loopback,
            "203.0.113.10",
            "198.51.100.20");

        var partition = RemoteControlHost.GetLoginPartition(
            context,
            CreateQuickTunnelOptions(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("public-quick-tunnel:unattributed", partition);
    }

    private static DefaultHttpContext CreateContext(
        IPAddress remoteAddress,
        params string[] headerValues)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        foreach (var value in headerValues)
        {
            context.Request.Headers.Append(
                RemoteControlOptions.CloudflareConnectingIpHeaderName,
                value);
        }

        return context;
    }

    private static RemoteControlOptions CreateQuickTunnelOptions()
        => new()
        {
            PublicOrigin = new Uri("https://quiet-lake-abc123.trycloudflare.com/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareQuickTunnel
        };
}
