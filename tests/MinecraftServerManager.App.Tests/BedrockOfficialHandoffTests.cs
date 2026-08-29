using System.Diagnostics;
using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.App.Tests;

public sealed class BedrockOfficialHandoffTests
{
    [Fact]
    public void TryOpen_UsesOfficialMinecraftProtocolBeforeStoreFallback()
    {
        var attempts = new List<ProcessStartInfo>();
        var service = new BedrockOfficialHandoffService(startInfo =>
        {
            attempts.Add(startInfo);
            return true;
        });

        var opened = service.TryOpen(out var target);

        Assert.True(opened);
        Assert.Equal(BedrockOfficialHandoffTarget.Minecraft, target);
        var attempt = Assert.Single(attempts);
        AssertSafeFixedShellHandoff(attempt, BedrockOfficialHandoffService.MinecraftUri);
    }

    [Fact]
    public void TryOpen_FallsBackOnlyToOfficialMinecraftLauncherStoreProduct()
    {
        var attempts = new List<ProcessStartInfo>();
        var service = new BedrockOfficialHandoffService(startInfo =>
        {
            attempts.Add(startInfo);
            return attempts.Count == 2;
        });

        var opened = service.TryOpen(out var target);

        Assert.True(opened);
        Assert.Equal(BedrockOfficialHandoffTarget.MicrosoftStore, target);
        Assert.Collection(
            attempts,
            attempt => AssertSafeFixedShellHandoff(
                attempt,
                BedrockOfficialHandoffService.MinecraftUri),
            attempt => AssertSafeFixedShellHandoff(
                attempt,
                BedrockOfficialHandoffService.MicrosoftStoreUri));
        Assert.Equal(
            "ms-windows-store://pdp/?ProductId=9PGW18NPBZV5",
            BedrockOfficialHandoffService.MicrosoftStoreUri.AbsoluteUri);
    }

    [Fact]
    public void TryOpen_WhenBothOfficialHandlersFail_ReturnsFalseWithoutAddingAnotherTarget()
    {
        var attempts = new List<ProcessStartInfo>();
        var service = new BedrockOfficialHandoffService(startInfo =>
        {
            attempts.Add(startInfo);
            return false;
        });

        var opened = service.TryOpen(out _);

        Assert.False(opened);
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt => Assert.True(attempt.UseShellExecute));
    }

    [Fact]
    public void CreateStartInfo_RejectsAnyTargetOutsideTheClosedOfficialSet()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BedrockOfficialHandoffService.CreateStartInfo(
                (BedrockOfficialHandoffTarget)int.MaxValue));
    }

    [Fact]
    public void ClientCreateSurface_UsesIndependentShortcutAndNeverCreatesABedrockJavaInstance()
    {
        var xaml = File.ReadAllText(TestRepositoryPaths.AppSource(
            Path.Combine("Views", "ClientWorkspaceView.xaml")));
        var viewModel = File.ReadAllText(TestRepositoryPaths.AppSource(
            Path.Combine("ViewModels", "ClientWorkspaceViewModel.cs")));

        Assert.Contains("L10n.client.create.bedrockOfficialHeading", xaml, StringComparison.Ordinal);
        Assert.Contains("L10n.client.create.openBedrockOfficial", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenBedrockOfficialCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding BedrockChannelChoices}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding NewBedrockShortcutName", xaml, StringComparison.Ordinal);
        Assert.Contains("IsBedrockShortcutPage", xaml, StringComparison.Ordinal);
        Assert.Contains("_bedrockOfficialHandoff.TryOpenStore(", viewModel, StringComparison.Ordinal);
        Assert.Contains("await CreateBedrockShortcutAsync();", viewModel, StringComparison.Ordinal);
        Assert.Contains("_bedrockShortcutRegistry.AddAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("_bedrockShortcutRegistry.RemoveAsync", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MinecraftClientEdition.Bedrock,",
            viewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BedrockHandoffText_IsCompleteInBothSupportedLanguages()
    {
        foreach (var key in new[]
                 {
                     "client.create.bedrockOfficialHeading",
                     "client.create.bedrockHint",
                     "client.create.bedrockHandoffHint",
                     "client.create.openBedrockOfficial",
                     "client.create.bedrockAliasLabel",
                     "client.create.bedrockChannel",
                     "client.create.bedrockCreate",
                     "client.bedrock.channel.stable",
                     "client.bedrock.channel.preview",
                     "client.bedrock.remove.confirm",
                     "client.vm.status.bedrockOpened",
                     "client.vm.status.bedrockStoreOpened",
                     "client.vm.validation.bedrockHandoffFailed",
                 })
        {
            Assert.Contains(key, ProductLocalizationCatalog.Keys);
            Assert.False(string.IsNullOrWhiteSpace(
                ProductLocalizationCatalog.GetDocument("en-US").Strings[key]));
            Assert.False(string.IsNullOrWhiteSpace(
                ProductLocalizationCatalog.GetDocument("zh-TW").Strings[key]));
        }
    }

    private static void AssertSafeFixedShellHandoff(ProcessStartInfo startInfo, Uri expectedUri)
    {
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(expectedUri.AbsoluteUri, startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
        Assert.True(string.IsNullOrEmpty(startInfo.Verb));
        Assert.True(string.IsNullOrEmpty(startInfo.WorkingDirectory));
        Assert.True(string.IsNullOrEmpty(startInfo.UserName));
    }
}
