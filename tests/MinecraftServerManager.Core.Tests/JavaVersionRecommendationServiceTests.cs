using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class JavaVersionRecommendationServiceTests
{
    private readonly JavaVersionRecommendationService _service = new();

    [Theory]
    [InlineData("1.11.2", 8)]
    [InlineData("1.12.2", 11)]
    [InlineData("1.16.5", 16)]
    [InlineData("1.19.4", 17)]
    [InlineData("1.21.4", 21)]
    [InlineData("26.1", 25)]
    public void GetRecommendation_PaperRulesCoverSupportedJavaVersions(
        string minecraftVersion,
        int expectedJava)
    {
        var result = _service.GetRecommendation(minecraftVersion, CoreType.Paper);

        Assert.Equal(expectedJava, result.MajorVersion);
        Assert.False(result.RequiresUserConfirmation);
    }

    [Theory]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    public void GetRecommendation_VanillaUsesGameRuntimeRules(
        string minecraftVersion,
        int expectedJava)
    {
        var result = _service.GetRecommendation(minecraftVersion, CoreType.Vanilla);

        Assert.Equal(expectedJava, result.MajorVersion);
    }

    [Theory]
    [InlineData(CoreType.Mohist, "1.20.1", 17)]
    [InlineData(CoreType.Arclight, "1.21.1", 21)]
    [InlineData(CoreType.CatServer, "1.18.2", 17)]
    [InlineData(CoreType.Akarin, "1.12.2", 8)]
    public void GetRecommendation_HybridCoresUseMinecraftRulesWithoutUnknownConfirmation(
        CoreType coreType,
        string minecraftVersion,
        int expectedJava)
    {
        var result = _service.GetRecommendation(minecraftVersion, coreType);

        Assert.Equal(expectedJava, result.MajorVersion);
        Assert.False(result.RequiresUserConfirmation);
    }

    [Fact]
    public void GetRecommendation_UnknownVersionUsesConservativeConfirmedFallback()
    {
        var result = _service.GetRecommendation(null, CoreType.CustomJar);

        Assert.Equal(17, result.MajorVersion);
        Assert.True(result.RequiresUserConfirmation);
        Assert.False(result.IsOverride);
    }

    [Fact]
    public void GetRecommendation_ExplicitOverrideAlwaysWins()
    {
        var result = _service.GetRecommendation("1.21.4", CoreType.Paper, 11);

        Assert.Equal(11, result.MajorVersion);
        Assert.True(result.IsOverride);
        Assert.False(result.RequiresUserConfirmation);
    }

    [Fact]
    public void SupportedMajorVersions_ListsEveryManagedRuntimeGeneration()
    {
        Assert.Equal([8, 11, 16, 17, 21, 25], JavaVersionRecommendationService.SupportedMajorVersions);
    }
}
