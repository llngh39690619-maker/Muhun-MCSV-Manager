using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteCredentialRulesTests
{
    [Theory]
    [InlineData("a12345", "a12345")]
    [InlineData("Account123", "account123")]
    [InlineData("a123456789", "a123456789")]
    public void Username_AcceptsAsciiLettersAndDigits(string value, string expected)
    {
        Assert.True(RemoteCredentialRules.TryNormalizeUsername(value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1abcde")]
    [InlineData("abc_de")]
    [InlineData("abc-de")]
    [InlineData("ａｂｃ１２３")]
    [InlineData("abcdef ")]
    public void Username_RejectsAmbiguousOrUnsupportedShapes(string value)
        => Assert.False(RemoteCredentialRules.TryNormalizeUsername(value, out _));

    [Theory]
    [InlineData("0000")]
    [InlineData("01234567")]
    [InlineData("123456789012")]
    public void Pin_PreservesLeadingZeroAndAcceptsFourToTwelveAsciiDigits(string value)
        => Assert.True(RemoteCredentialRules.IsValidPin(value));

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData("１２３４")]
    [InlineData("1234 ")]
    [InlineData("12a4")]
    public void Pin_RejectsInvalidShape(string value)
        => Assert.False(RemoteCredentialRules.IsValidPin(value));
}
