using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteRequestSecurityTests
{
    private static readonly Uri PublicOrigin = new("https://mcsv-test.example.ts.net");

    [Fact]
    public void ExactOriginAndHost_AreAccepted()
    {
        Assert.True(RemoteRequestSecurity.HasExactMutationOrigin(
            "https://mcsv-test.example.ts.net",
            "mcsv-test.example.ts.net",
            PublicOrigin));
    }

    [Theory]
    [InlineData("https://evil.example", "mcsv-test.example.ts.net")]
    [InlineData("https://mcsv-test.example.ts.net.evil.example", "mcsv-test.example.ts.net")]
    [InlineData("https://mcsv-test.example.ts.net", "localhost:42871")]
    [InlineData("https://mcsv-test.example.ts.net", "mcsv-test.example.ts.net:443")]
    [InlineData(null, "mcsv-test.example.ts.net")]
    [InlineData("https://mcsv-test.example.ts.net", null)]
    public void NonExactOriginOrHost_IsRejected(string? origin, string? host)
    {
        Assert.False(RemoteRequestSecurity.HasExactMutationOrigin(origin, host, PublicOrigin));
    }

    [Fact]
    public void ExplicitPublicPort_MustAppearInBothOriginAndHost()
    {
        var origin = new Uri("https://mcsv-test.example.ts.net:8443");

        Assert.True(RemoteRequestSecurity.HasExactMutationOrigin(
            "https://mcsv-test.example.ts.net:8443",
            "mcsv-test.example.ts.net:8443",
            origin));
        Assert.False(RemoteRequestSecurity.HasExactMutationOrigin(
            "https://mcsv-test.example.ts.net",
            "mcsv-test.example.ts.net",
            origin));
    }
}
