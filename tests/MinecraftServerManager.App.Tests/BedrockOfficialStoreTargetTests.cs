using System.Diagnostics;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class BedrockOfficialStoreTargetTests
{
    [Theory]
    [InlineData(
        MinecraftBedrockChannel.Stable,
        2,
        "ms-windows-store://pdp/?ProductId=9NBLGGH2JHXJ")]
    [InlineData(
        MinecraftBedrockChannel.Preview,
        3,
        "ms-windows-store://pdp/?ProductId=9P5X4QVLC2XR")]
    public void StoreChannel_MapsToOneFixedOfficialProduct(
        MinecraftBedrockChannel channel,
        int expectedTargetValue,
        string expectedUri)
    {
        Assert.Equal(
            (BedrockOfficialHandoffTarget)expectedTargetValue,
            BedrockOfficialHandoffService.GetStoreTarget(channel));

        var startInfo = BedrockOfficialHandoffService.CreateStoreStartInfo(channel);

        AssertSafeFixedShellHandoff(startInfo, expectedUri);
    }

    [Fact]
    public void Launcher_MapsToItsFixedOfficialStoreProduct()
    {
        Assert.Equal(
            "ms-windows-store://pdp/?ProductId=9PGW18NPBZV5",
            BedrockOfficialHandoffService.MinecraftLauncherStoreUri.AbsoluteUri);
        Assert.Same(
            BedrockOfficialHandoffService.MinecraftLauncherStoreUri,
            BedrockOfficialHandoffService.MicrosoftStoreUri);
        AssertSafeFixedShellHandoff(
            BedrockOfficialHandoffService.CreateStartInfo(
                BedrockOfficialHandoffTarget.MicrosoftStore),
            "ms-windows-store://pdp/?ProductId=9PGW18NPBZV5");
    }

    [Fact]
    public void TryOpenStore_UsesOnlyChannelAndNeverPlacesDisplayNameInUri()
    {
        var shortcut = new BedrockClientShortcut
        {
            DisplayName = "My name ?ProductId=attacker&url=https://example.invalid/",
            Channel = MinecraftBedrockChannel.Preview,
        };
        ProcessStartInfo? attempted = null;
        var service = new BedrockOfficialHandoffService(startInfo =>
        {
            attempted = startInfo;
            return true;
        });

        Assert.True(service.TryOpenStore(shortcut.Channel));

        var startInfo = Assert.IsType<ProcessStartInfo>(attempted);
        AssertSafeFixedShellHandoff(
            startInfo,
            "ms-windows-store://pdp/?ProductId=9P5X4QVLC2XR");
        Assert.DoesNotContain(shortcut.DisplayName, startInfo.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", startInfo.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreChannel_RejectsValuesOutsideClosedEnumBeforeShellExecution()
    {
        var started = false;
        var service = new BedrockOfficialHandoffService(_ =>
        {
            started = true;
            return true;
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.TryOpenStore((MinecraftBedrockChannel)int.MaxValue));
        Assert.False(started);
    }

    [Fact]
    public void AllShellTargetsAreTheCompleteFixedAllowlist()
    {
        Assert.Equal(
            new[]
            {
                BedrockOfficialHandoffTarget.Minecraft,
                BedrockOfficialHandoffTarget.MicrosoftStore,
                BedrockOfficialHandoffTarget.MinecraftForWindowsStore,
                BedrockOfficialHandoffTarget.MinecraftPreviewStore,
            },
            Enum.GetValues<BedrockOfficialHandoffTarget>());

        AssertSafeFixedShellHandoff(
            BedrockOfficialHandoffService.CreateStartInfo(
                BedrockOfficialHandoffTarget.Minecraft),
            "minecraft:///");
        AssertSafeFixedShellHandoff(
            BedrockOfficialHandoffService.CreateStartInfo(
                BedrockOfficialHandoffTarget.MicrosoftStore),
            "ms-windows-store://pdp/?ProductId=9PGW18NPBZV5");
        AssertSafeFixedShellHandoff(
            BedrockOfficialHandoffService.CreateStartInfo(
                BedrockOfficialHandoffTarget.MinecraftForWindowsStore),
            "ms-windows-store://pdp/?ProductId=9NBLGGH2JHXJ");
        AssertSafeFixedShellHandoff(
            BedrockOfficialHandoffService.CreateStartInfo(
                BedrockOfficialHandoffTarget.MinecraftPreviewStore),
            "ms-windows-store://pdp/?ProductId=9P5X4QVLC2XR");
    }

    private static void AssertSafeFixedShellHandoff(
        ProcessStartInfo startInfo,
        string expectedUri)
    {
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(expectedUri, startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
        Assert.True(string.IsNullOrEmpty(startInfo.Verb));
        Assert.True(string.IsNullOrEmpty(startInfo.WorkingDirectory));
        Assert.True(string.IsNullOrEmpty(startInfo.UserName));
    }
}
