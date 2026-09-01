using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductSemanticVersionTests
{
    [Theory]
    [InlineData("1.2.9-beta.10", "1.2.9-beta.9")]
    [InlineData("1.2.9", "1.2.9-beta.99")]
    [InlineData("1.3.0-beta.1", "1.2.99")]
    [InlineData("2.0.0", "1.999999999999999999999999.999999999999999999999999")]
    public void Compare_RecognizesStrictUpgrade(string target, string active)
        => Assert.True(ProductSemanticVersion.Compare(target, active) > 0);

    [Theory]
    [InlineData("1.2.9-beta.9", "1.2.9-beta.10")]
    [InlineData("1.2.9-beta.99", "1.2.9")]
    [InlineData("1.2.8", "1.2.9-beta.1")]
    public void Compare_RecognizesDowngrade(string target, string active)
        => Assert.True(ProductSemanticVersion.Compare(target, active) < 0);

    [Fact]
    public void Compare_RecognizesSameVersion()
        => Assert.Equal(0, ProductSemanticVersion.Compare("1.2.9-beta.1", "1.2.9-beta.1"));
}
