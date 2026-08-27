using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteInputValidatorTests
{
    [Fact]
    public void CanonicalUuidIdempotencyKey_IsAccepted()
    {
        var value = Guid.NewGuid().ToString("D");

        Assert.True(RemoteInputValidator.TryParseIdempotencyKey(value, out var parsed));
        Assert.Equal(Guid.Parse(value), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("11111111111111111111111111111111")]
    public void NonCanonicalIdempotencyKey_IsRejected(string? value)
    {
        Assert.False(RemoteInputValidator.TryParseIdempotencyKey(value, out _));
    }

    [Theory]
    [InlineData("server-01")]
    [InlineData("forge_26.2")]
    public void OpaqueServerIdentifiers_AreAccepted(string serverId)
    {
        Assert.True(RemoteInputValidator.TryValidateServerId(serverId, out _));
    }

    [Theory]
    [InlineData("../server")]
    [InlineData("C:\\servers\\one")]
    [InlineData("server/one")]
    [InlineData("server%2fone")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void PathsAndMalformedServerIdentifiers_AreRejected(string serverId)
    {
        Assert.False(RemoteInputValidator.TryValidateServerId(serverId, out _));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('F')]
    [InlineData('0')]
    public void OpaqueSha256BackupIdentifiers_AreAccepted(char value)
    {
        Assert.True(RemoteInputValidator.TryValidateBackupId(new string(value, 64), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../backup.zip")]
    [InlineData("C:\\backups\\backup.zip")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void PathsAndNonSha256BackupIdentifiers_AreRejected(string backupId)
    {
        Assert.False(RemoteInputValidator.TryValidateBackupId(backupId, out _));
    }

    [Theory]
    [InlineData("say hello")]
    [InlineData("whitelist list")]
    [InlineData("/list")]
    public void SingleLineMinecraftCommands_AreAccepted(string command)
    {
        Assert.True(RemoteInputValidator.TryValidateCommand(command, 512, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("say hello\rstop")]
    [InlineData("say hello\nstop")]
    [InlineData("say hello\0stop")]
    public void EmptyOrInjectedCommands_AreRejected(string command)
    {
        Assert.False(RemoteInputValidator.TryValidateCommand(command, 512, out _));
    }

    [Fact]
    public void OverlongCommand_IsRejected()
    {
        Assert.False(RemoteInputValidator.TryValidateCommand(new string('a', 513), 512, out _));
    }

    [Theory]
    [InlineData(RemotePlayerActionKind.Kick)]
    [InlineData(RemotePlayerActionKind.Ban)]
    [InlineData(RemotePlayerActionKind.Pardon)]
    [InlineData(RemotePlayerActionKind.Op)]
    [InlineData(RemotePlayerActionKind.Deop)]
    [InlineData(RemotePlayerActionKind.WhitelistAdd)]
    [InlineData(RemotePlayerActionKind.WhitelistRemove)]
    public void PlayerTargetedActions_RequireSafePlayerName(RemotePlayerActionKind action)
    {
        Assert.True(RemoteInputValidator.TryValidatePlayerAction(
            new RemotePlayerActionRequestDto("Steve_01", action, null),
            out _));
        Assert.False(RemoteInputValidator.TryValidatePlayerAction(
            new RemotePlayerActionRequestDto("Steve; stop", action, null),
            out _));
        Assert.False(RemoteInputValidator.TryValidatePlayerAction(
            new RemotePlayerActionRequestDto(null, action, null),
            out _));
    }

    [Theory]
    [InlineData(RemotePlayerActionKind.WhitelistOn)]
    [InlineData(RemotePlayerActionKind.WhitelistOff)]
    public void GlobalWhitelistActions_MustNotContainPlayerName(RemotePlayerActionKind action)
    {
        Assert.True(RemoteInputValidator.TryValidatePlayerAction(
            new RemotePlayerActionRequestDto(null, action, null),
            out _));
        Assert.False(RemoteInputValidator.TryValidatePlayerAction(
            new RemotePlayerActionRequestDto("Steve", action, null),
            out _));
    }

    [Fact]
    public void Reason_IsAcceptedOnlyForKickOrBan()
    {
        Assert.True(RemoteInputValidator.TryValidatePlayerAction(
            new RemotePlayerActionRequestDto("Steve", RemotePlayerActionKind.Ban, "testing"),
            out _));
        Assert.False(RemoteInputValidator.TryValidatePlayerAction(
            new RemotePlayerActionRequestDto("Steve", RemotePlayerActionKind.Op, "testing"),
            out _));
    }
}
