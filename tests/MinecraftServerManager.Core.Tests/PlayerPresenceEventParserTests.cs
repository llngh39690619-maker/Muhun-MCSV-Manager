using System.Diagnostics;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class PlayerPresenceEventParserTests
{
    [Theory]
    [InlineData("[12:34:56] [Server thread/INFO]: Alex joined the game", "Alex", true)]
    [InlineData("[12:34:56 INFO]: Test_User left the game", "Test_User", false)]
    [InlineData("[12:34:56] [Server thread/INFO]: Steve lost connection: Timed out", "Steve", false)]
    [InlineData("[15Aug2026 12:34:56.789] [Server thread/INFO] [net.minecraft.server.MinecraftServer/]: Aero123 joined the game", "Aero123", true)]
    [InlineData("[15Aug2026 12:34:56.789] [Server thread/INFO] [net.minecraft.server.network.ServerGamePacketListenerImpl/]: Aero123 lost connection: Disconnected", "Aero123", false)]
    [InlineData("\u001b[32m[12:34:56 INFO]: Color_User joined the game\u001b[0m", "Color_User", true)]
    [InlineData("[12:34:56] [Server thread/INFO] [net.minecraft.server.dedicated.DedicatedServer]: Forge112 joined the game", "Forge112", true)]
    [InlineData("[12:34:56] [Server thread/INFO] [net.minecraft.server.management.PlayerList]: Forge112 left the game", "Forge112", false)]
    [InlineData("[12:34:56] [Server thread/INFO] [net.minecraft.network.NetHandlerPlayServer]: Forge112 lost connection: Disconnected", "Forge112", false)]
    [InlineData("[12:34:56] [Server thread/INFO] [minecraft/DedicatedServer]: ForgeModern joined the game", "ForgeModern", true)]
    [InlineData("[15Aug2026 12:34:56.789] [Server thread/INFO] [minecraft/PlayerList]: ForgeModern left the game", "ForgeModern", false)]
    [InlineData("[15:30:31] [Server thread/INFO]: Emperor_Yandi[/127.0.0.1:52253] logged in with entity id 1376 at ([world]-74.5, 76.0, 247.5)", "Emperor_Yandi", true)]
    [InlineData("[15:30:31 INFO]: IPv6_User[/0:0:0:0:0:0:0:1:52253] logged in with entity id -42 at ([world]0.0, 64.0, 0.0)", "IPv6_User", true)]
    [InlineData("\u001b[32m[15:30:31] [Server thread/INFO]: ColorLogin[/127.0.0.1:52253] logged in with entity id 99 at ([world]1.0, 2.0, 3.0)\u001b[0m", "ColorLogin", true)]
    [InlineData("[158月2026 09:17:43.495] [Server thread/INFO] [net.minecraft.server.players.PlayerList/]: Jin_Shou[/[::1]:64897] logged in with entity id 919 at (2737.64, 185.99, -3358.60)", "Jin_Shou", true)]
    [InlineData("[158月2026 09:17:44.774] [Server thread/INFO] [net.minecraft.server.MinecraftServer/]: Jin_Shou joined the game", "Jin_Shou", true)]
    [InlineData("[158月2026 12:07:24.131] [Server thread/INFO] [net.minecraft.server.network.ServerGamePacketListenerImpl/]: Jin_Shou lost connection: Disconnected", "Jin_Shou", false)]
    [InlineData("[158月2026 12:07:24.131] [Server thread/INFO] [net.minecraft.server.MinecraftServer/]: Jin_Shou left the game", "Jin_Shou", false)]
    [InlineData("[18:53:30.359] [Server thread/INFO] NoColon joined the game", "NoColon", true)]
    [InlineData("2013-06-21 12:34:56 [INFO] LegacyUser joined the game", "LegacyUser", true)]
    [InlineData("[12:34:56] [Region Scheduler Thread #2/INFO] [minecraft/MinecraftServer]: FoliaUser joined the game", "FoliaUser", true)]
    [InlineData("[12:34:56 INFO]: New_Name (formerly known as Old_Name) joined the game", "New_Name", true)]
    public void TryParse_AcceptsStandardPassivePresenceEvents(
        string text,
        string expectedPlayer,
        bool expectedOnline)
    {
        var parsed = PlayerPresenceEventParser.TryParse(text, out var change);

        Assert.True(parsed);
        Assert.Equal(expectedPlayer, change.PlayerName);
        Assert.Equal(expectedOnline, change.IsOnline);
    }

    [Theory]
    [InlineData("[12:34:56] [Server thread/INFO]: <Eve> Steve joined the game")]
    [InlineData("[15Aug2026 12:34:56.789] [Server thread/INFO] [example.plugin.FakePlugin/]: Steve joined the game")]
    [InlineData("[12:34:56] [Server thread/INFO]: There are 1 of a max of 20 players online: Steve")]
    [InlineData("player name joined the game")]
    [InlineData("PlayerNameThatIsTooLong joined the game")]
    [InlineData("Steve joined the game extra text")]
    [InlineData("Steve lost connection:")]
    [InlineData("\u001b[31m[12:34:56] [Server thread/INFO] [example.plugin.FakePlugin]: Steve joined the game\u001b[0m")]
    [InlineData("[12:34:56] [Server thread/INFO] [minecraft/FakePlugin]: Steve joined the game")]
    [InlineData("[15:30:31] [User Authenticator #1/INFO]: UUID of player Steve is 01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("Steve[/127.0.0.1:52253] logged in with entity id 1376 at ([world]0.0, 64.0, 0.0)")]
    [InlineData("[15:30:31] [Server thread/INFO]: [ExamplePlugin] Steve[/127.0.0.1:52253] logged in with entity id 1376 at ([world]0.0, 64.0, 0.0)")]
    [InlineData("[15:30:31] [Server thread/INFO]: Steve[/127.0.0.1:52253] logged in with entity id nope at ([world]0.0, 64.0, 0.0)")]
    [InlineData("[15:30:31] [Server thread/INFO]: Steve[/127.0.0.1:52253] logged in with entity id 1376 at ([world]0.0, 64.0, 0.0) forged tail")]
    [InlineData("Steve joined the game")]
    [InlineData("[158月2026 09:17:44.774] [Server thread/INFO] [example.plugin.FakePlugin/]: Steve joined the game")]
    [InlineData("[158月2026 09:17:44.774] [Async Chat Thread/INFO] [net.minecraft.server.MinecraftServer/]: Steve joined the game")]
    [InlineData("[158月2026 09:17:44.774] [Server thread/WARN] [net.minecraft.server.MinecraftServer/]: Steve joined the game")]
    public void TryParse_RejectsNonStandardOrAmbiguousLines(string text)
    {
        Assert.False(PlayerPresenceEventParser.TryParse(text, out _));
    }

    [Theory]
    [InlineData(CoreType.Velocity, "[17:39:56 INFO]: [connected player] VelocityUser (/127.0.0.1:3108) has connected", "VelocityUser", true)]
    [InlineData(CoreType.Velocity, "[17:39:57 INFO]: [connected player] VelocityUser (/127.0.0.1:3108) has disconnected: Server closed", "VelocityUser", false)]
    [InlineData(CoreType.Velocity, "[18:53:10.511] [Netty epoll Worker #7/INFO] (com.velocitypowered.proxy.connection.client.AuthSessionHandler) [connected player] ModernProxy (/127.0.0.1:3108) has connected", "ModernProxy", true)]
    [InlineData(CoreType.BungeeCord, "17:15:51 [INFO] [BungeeUser] <-> InitialHandler has connected", "BungeeUser", true)]
    [InlineData(CoreType.Waterfall, "17:16:02 [INFO] [BungeeUser] -> UpstreamBridge has disconnected", "BungeeUser", false)]
    public void TryParse_AcceptsCoreSpecificProxyEvents(
        CoreType coreType,
        string text,
        string expectedPlayer,
        bool expectedOnline)
    {
        var parsed = PlayerPresenceEventParser.TryParse(text, coreType, out var change);

        Assert.True(parsed);
        Assert.Equal(expectedPlayer, change.PlayerName);
        Assert.Equal(expectedOnline, change.IsOnline);
    }

    [Theory]
    [InlineData(CoreType.Vanilla)]
    [InlineData(CoreType.Paper)]
    [InlineData(CoreType.Purpur)]
    [InlineData(CoreType.Folia)]
    [InlineData(CoreType.Spigot)]
    [InlineData(CoreType.CraftBukkit)]
    [InlineData(CoreType.Fabric)]
    [InlineData(CoreType.Forge)]
    [InlineData(CoreType.NeoForge)]
    [InlineData(CoreType.CustomJar)]
    [InlineData(CoreType.Mohist)]
    [InlineData(CoreType.Arclight)]
    [InlineData(CoreType.CatServer)]
    [InlineData(CoreType.Akarin)]
    public void TryParse_UsesSharedMinecraftEventsAcrossServerCoreFamilies(CoreType coreType)
    {
        var parsed = PlayerPresenceEventParser.TryParse(
            "[12:34:56] [Server thread/INFO]: CrossCore_User joined the game",
            coreType,
            out var change);

        Assert.True(parsed);
        Assert.Equal("CrossCore_User", change.PlayerName);
        Assert.True(change.IsOnline);
    }

    [Theory]
    [InlineData(CoreType.Velocity, "[17:39:56 INFO]: [server connection] VelocityUser -> lobby has disconnected")]
    [InlineData(CoreType.Velocity, "[17:39:56 INFO]: [connected player] VelocityUser (/127.0.0.1:3108): disconnected while connecting to lobby")]
    [InlineData(CoreType.BungeeCord, "17:15:51 [INFO] [/127.0.0.1:56107] <-> InitialHandler has connected")]
    [InlineData(CoreType.BungeeCord, "17:15:51 [INFO] [BungeeUser] <-> ServerConnector [Lobby] has disconnected")]
    [InlineData(CoreType.Paper, "[17:39:56 INFO]: [connected player] VelocityUser (/127.0.0.1:3108) has connected")]
    [InlineData(CoreType.Velocity, "[17:39:56 INFO]: VelocityUser joined the game")]
    public void TryParse_RejectsWrongOrNonAuthoritativeCoreEvents(CoreType coreType, string text)
    {
        Assert.False(PlayerPresenceEventParser.TryParse(text, coreType, out _));
    }

    [Fact]
    public void TryParse_RejectsNeoForgePreLoginGameProfileDisconnect()
    {
        const string preLogin =
            "[158月2026 09:14:03.848] [Server thread/INFO] "
            + "[net.minecraft.server.network.ServerConfigurationPacketListenerImpl/]: "
            + "com.mojang.authlib.GameProfile@[id=01234567-89ab-cdef-0123-456789abcdef,"
            + "name=Jin_Shou,properties={}] lost connection: Incompatible client!";

        Assert.False(PlayerPresenceEventParser.TryParse(preLogin, CoreType.NeoForge, out _));
    }

    [Theory]
    [InlineData(CoreType.Paper, "[12:34:56 INFO]: Flexible_User joined\t the  game", "Flexible_User", true)]
    [InlineData(CoreType.Velocity, "[17:39:56 INFO]: [connected\t player] Proxy_User (/127.0.0.1:3108) has connected", "Proxy_User", true)]
    public void TryParse_FastGatePreservesFlexibleWhitespaceAcceptance(
        CoreType coreType,
        string text,
        string expectedPlayer,
        bool expectedOnline)
    {
        var parsed = PlayerPresenceEventParser.TryParse(text, coreType, out var change);

        Assert.True(parsed);
        Assert.Equal(expectedPlayer, change.PlayerName);
        Assert.Equal(expectedOnline, change.IsOnline);
    }

    [Fact]
    public void TryParse_TenThousandOrdinaryLinesStayOnBoundedNegativeFastPath()
    {
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < 10_000; index++)
        {
            Assert.False(PlayerPresenceEventParser.TryParse(
                $"[12:34:56] [Server thread/INFO]: Preparing spawn area: {index % 100}%",
                CoreType.Paper,
                out _));
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Ordinary-line fast path took {stopwatch.Elapsed}.");
    }
}
