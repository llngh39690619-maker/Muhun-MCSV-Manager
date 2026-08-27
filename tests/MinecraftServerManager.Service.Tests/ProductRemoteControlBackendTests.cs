using MinecraftServerManager.Remote.Contracts;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductRemoteControlBackendTests
{
    [Theory]
    [InlineData(RemotePlayerActionKind.Kick, "Steve", null, "kick Steve")]
    [InlineData(RemotePlayerActionKind.Kick, "Steve", "maintenance", "kick Steve maintenance")]
    [InlineData(RemotePlayerActionKind.Ban, "Alex", "abuse", "ban Alex abuse")]
    [InlineData(RemotePlayerActionKind.Pardon, "Alex", null, "pardon Alex")]
    [InlineData(RemotePlayerActionKind.Op, "Alex", null, "op Alex")]
    [InlineData(RemotePlayerActionKind.Deop, "Alex", null, "deop Alex")]
    [InlineData(RemotePlayerActionKind.WhitelistAdd, "Alex", null, "whitelist add Alex")]
    [InlineData(RemotePlayerActionKind.WhitelistRemove, "Alex", null, "whitelist remove Alex")]
    [InlineData(RemotePlayerActionKind.WhitelistOn, null, null, "whitelist on")]
    [InlineData(RemotePlayerActionKind.WhitelistOff, null, null, "whitelist off")]
    public void ValidPlayerAction_MapsToOneExactMinecraftCommand(
        RemotePlayerActionKind action,
        string? player,
        string? reason,
        string expected)
    {
        var request = new RemotePlayerActionRequestDto(player, action, reason);

        Assert.Equal(expected, ProductRemoteControlBackend.CreatePlayerCommand(request));
    }
}
