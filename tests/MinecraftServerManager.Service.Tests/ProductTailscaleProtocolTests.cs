using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductTailscaleProtocolTests
{
    private const string DnsName = "muhun-box.tail123.ts.net";
    private const string Target = "http://127.0.0.1:42871";

    [Fact]
    public void NodeStatus_RequiresRunningBackendStrictTsNetDnsAndCertificateDomain()
    {
        var status = ProductTailscaleProtocol.ParseNodeStatus(
            $$"""
            {
              "BackendState": "Running",
              "Self": { "DNSName": "{{DnsName}}." },
              "CertDomains": ["{{DnsName}}"]
            }
            """);

        Assert.True(status.IsConnected);
        Assert.Equal(DnsName, status.DnsName);
        Assert.Equal($"https://{DnsName}/", status.PublicOrigin?.ToString());
        Assert.Null(status.ErrorCode);
    }

    [Theory]
    [InlineData("{\"BackendState\":\"Running\",\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"box.tail.ts.net\"},\"CertDomains\":[\"box.tail.ts.net\"]}")]
    [InlineData("{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"attacker.example\"},\"CertDomains\":[\"attacker.example\"]}")]
    [InlineData("{\"BackendState\":\"Stopped\",\"Self\":{\"DNSName\":\"box.tail.ts.net\"},\"CertDomains\":[\"box.tail.ts.net\"]}")]
    [InlineData("{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"box.tail.ts.net\"},\"CertDomains\":null}")]
    public void NodeStatus_RejectsAmbiguousOrUnusableInput(string json)
    {
        var status = ProductTailscaleProtocol.ParseNodeStatus(json);

        Assert.Null(status.PublicOrigin);
        Assert.NotNull(status.ErrorCode);
    }

    [Fact]
    public void FunnelStatus_AcceptsOneExactForeground443Target()
    {
        var status = ProductTailscaleProtocol.ParseFunnelStatus(
            ExactForegroundJson(),
            DnsName,
            Target);

        Assert.Equal(ProductFunnelRouteDisposition.ExactTarget, status.Disposition);
        Assert.Null(status.ErrorCode);
    }

    [Fact]
    public void FunnelStatus_RecognizesStrictlyEmptyConfiguration()
    {
        var status = ProductTailscaleProtocol.ParseFunnelStatus(
            "{\"Foreground\":{},\"Services\":{}}",
            DnsName,
            Target);

        Assert.Equal(ProductFunnelRouteDisposition.Absent, status.Disposition);
    }

    [Fact]
    public void FunnelStatus_RefusesDifferentOrMultiple443Routes()
    {
        var different = ProductTailscaleProtocol.ParseFunnelStatus(
            ExactForegroundJson().Replace(Target, "http://127.0.0.1:49999", StringComparison.Ordinal),
            DnsName,
            Target);
        var multiple = ProductTailscaleProtocol.ParseFunnelStatus(
            $$"""
            {
              "TCP": { "443": { "HTTPS": true } },
              "Web": { "{{DnsName}}:443": { "Handlers": { "/": { "Proxy": "{{Target}}" } } } },
              "AllowFunnel": { "{{DnsName}}:443": true },
              "Foreground": { "other": {
                "TCP": { "443": { "HTTPS": true } },
                "Web": { "{{DnsName}}:443": { "Handlers": { "/": { "Proxy": "{{Target}}" } } } },
                "AllowFunnel": { "{{DnsName}}:443": true }
              } }
            }
            """,
            DnsName,
            Target);

        Assert.Equal(ProductFunnelRouteDisposition.Conflict, different.Disposition);
        Assert.Equal(ProductFunnelRouteDisposition.Conflict, multiple.Disposition);
    }

    [Fact]
    public void FunnelStatus_FailsClosedOnUnknownSchemaOrUnsafeTarget()
    {
        var futureSchema = ProductTailscaleProtocol.ParseFunnelStatus(
            "{\"FutureRoutes\":{}}",
            DnsName,
            Target);
        var unsafeTarget = ProductTailscaleProtocol.ParseFunnelStatus(
            "{}",
            DnsName,
            "http://0.0.0.0:42871");

        Assert.Equal(ProductFunnelRouteDisposition.Indeterminate, futureSchema.Disposition);
        Assert.Equal(ProductFunnelRouteDisposition.Indeterminate, unsafeTarget.Disposition);
    }

    [Fact]
    public void FunnelStatus_DoesNotClaimPersistentOrIndirectConfigurationAsOwnedForeground()
    {
        var persistent = ProductTailscaleProtocol.ParseFunnelStatus(
            $$"""
            {
              "TCP": { "443": { "HTTPS": true } },
              "Web": { "{{DnsName}}:443": { "Handlers": { "/": { "Proxy": "{{Target}}" } } } },
              "AllowFunnel": { "{{DnsName}}:443": true }
            }
            """,
            DnsName,
            Target);
        var indirect = ProductTailscaleProtocol.ParseFunnelStatus(
            "{\"Foreground\":{\"session\":{\"UnexpectedWrapper\":{}}}}",
            DnsName,
            Target);

        Assert.Equal(ProductFunnelRouteDisposition.Conflict, persistent.Disposition);
        Assert.Equal(ProductFunnelRouteDisposition.Indeterminate, indirect.Disposition);
    }

    [Fact]
    public void FunnelStatus_AllowsValidatedUnrelatedServicePortButRejectsService443()
    {
        var unrelated = ProductTailscaleProtocol.ParseFunnelStatus(
            $$"""
            { "Services": { "svc:other": {
              "TCP": { "8443": { "HTTPS": true } },
              "Web": { "{{DnsName}}:8443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:49999" } } } }
            } } }
            """,
            DnsName,
            Target);
        var port443 = ProductTailscaleProtocol.ParseFunnelStatus(
            $$"""
            { "Services": { "svc:other": {
              "TCP": { "443": { "HTTPS": true } },
              "Web": { "{{DnsName}}:443": { "Handlers": { "/": { "Proxy": "{{Target}}" } } } },
              "AllowFunnel": { "{{DnsName}}:443": true }
            } } }
            """,
            DnsName,
            Target);

        Assert.Equal(ProductFunnelRouteDisposition.Absent, unrelated.Disposition);
        Assert.Equal(ProductFunnelRouteDisposition.Conflict, port443.Disposition);
    }

    private static string ExactForegroundJson() =>
        $$"""
        {
          "Foreground": {
            "session-1": {
              "TCP": { "443": { "HTTPS": true } },
              "Web": { "{{DnsName}}:443": { "Handlers": { "/": { "Proxy": "{{Target}}" } } } },
              "AllowFunnel": { "{{DnsName}}:443": true }
            }
          },
          "Services": {}
        }
        """;
}
