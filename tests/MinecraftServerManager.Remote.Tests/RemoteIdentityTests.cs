using Microsoft.AspNetCore.Http;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteIdentityTests
{
    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(["owner@gmail.com"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ExactAllowlistedHeader_IsAccepted()
    {
        var headers = new HeaderDictionary
        {
            [RemoteControlOptions.TailscaleLoginHeaderName] = "OWNER@gmail.com"
        };

        Assert.True(RemoteIdentity.TryGetAllowedLogin(headers, Allowed, out var login));
        Assert.Equal("OWNER@gmail.com", login);
    }

    [Theory]
    [InlineData("owner@gmail.com.attacker.example")]
    [InlineData("prefix-owner@gmail.com")]
    [InlineData("owner@example.com")]
    [InlineData(" owner@gmail.com")]
    public void SimilarOrMalformedLogin_IsRejected(string candidate)
    {
        var headers = new HeaderDictionary
        {
            [RemoteControlOptions.TailscaleLoginHeaderName] = candidate
        };

        Assert.False(RemoteIdentity.TryGetAllowedLogin(headers, Allowed, out _));
    }

    [Fact]
    public void MultipleIdentityHeaders_AreRejected()
    {
        var headers = new HeaderDictionary
        {
            [RemoteControlOptions.TailscaleLoginHeaderName] = new[]
            {
                "owner@gmail.com",
                "other@gmail.com"
            }
        };

        Assert.False(RemoteIdentity.TryGetAllowedLogin(headers, Allowed, out _));
    }
}
