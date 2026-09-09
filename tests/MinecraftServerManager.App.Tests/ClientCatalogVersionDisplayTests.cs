using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientCatalogVersionDisplayTests
{
    [Fact]
    public void CurseForgeVersion_UsesConciseSemanticFields()
    {
        var version = new OnlineModpackVersion(
            OnlineModpackProvider.CurseForge,
            "285109",
            "4612979",
            "RLCraft 1.12.2 - Release v2.9.3.zip",
            "1.12.2",
            "forge",
            "release",
            DateTimeOffset.UnixEpoch,
            HasOfficialServerPack: false);

        var item = new ClientCatalogVersionItemViewModel(version, "RLCraft");

        Assert.Equal("RLCraft v2.9.3", item.PackVersionDisplay);
        Assert.Equal("MC 1.12.2", item.GameVersionDisplay);
        Assert.Equal("Forge", item.LoaderDisplay);
        Assert.Equal("RLCraft v2.9.3 · MC 1.12.2 · Forge", item.Name);
        Assert.DoesNotContain(".zip", item.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Release", item.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingLoader_IsInferredOnlyFromKnownLoaderKeywords()
    {
        var version = new OnlineModpackVersion(
            OnlineModpackProvider.CurseForge,
            "project",
            "version",
            "Example Pack v4.0 for Neo-Forge.mrpack",
            "1.21.1",
            string.Empty,
            "release",
            DateTimeOffset.UnixEpoch,
            HasOfficialServerPack: false);

        var item = new ClientCatalogVersionItemViewModel(version, "Example Pack");

        Assert.Equal("Example Pack v4.0", item.PackVersionDisplay);
        Assert.Equal("MC 1.21.1", item.GameVersionDisplay);
        Assert.Equal("NeoForge", item.LoaderDisplay);
        Assert.DoesNotContain(".mrpack", item.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbeddedArchiveExtension_IsRemovedFromTheSemanticVersion()
    {
        var version = new OnlineModpackVersion(
            OnlineModpackProvider.CurseForge,
            "285109",
            "4612979",
            "RLCraft 1.12.2 - Release v2.9.3.zip - Minecraft 1.12.2 - Forge",
            "1.12.2",
            "forge",
            "release",
            DateTimeOffset.UnixEpoch,
            HasOfficialServerPack: false);

        var item = new ClientCatalogVersionItemViewModel(version, "RLCraft");

        Assert.Equal("RLCraft v2.9.3", item.PackVersionDisplay);
        Assert.DoesNotContain(".zip", item.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModrinthAndFtbVersions_IncludeTheModpackTitle()
    {
        var modrinth = new ModrinthClientModpackVersion(
            "project",
            "version",
            "Release v2.9.3.mrpack",
            "v2.9.3",
            "client_and_server",
            ["1.12.2"],
            ["forge"],
            DateTimeOffset.UnixEpoch,
            1,
            new ModrinthClientMrpackFile(
                "RLCraft-v2.9.3.mrpack",
                new Uri("https://cdn.example.test/rlcraft.mrpack"),
                1,
                "sha512",
                null,
                true));
        var ftb = new FtbClientCatalogVersion(
            1,
            2,
            "v1.11.0",
            "1.21.1",
            "neoforge",
            "21.1.248",
            DateTimeOffset.UnixEpoch);

        var modrinthItem = new ClientCatalogVersionItemViewModel(modrinth, "RLCraft");
        var ftbItem = new ClientCatalogVersionItemViewModel(ftb, "FTB Skies 2: Aero");

        Assert.Equal("RLCraft v2.9.3", modrinthItem.PackVersionDisplay);
        Assert.Equal("FTB Skies 2: Aero v1.11.0", ftbItem.PackVersionDisplay);
    }

    [Theory]
    [InlineData("NeoForge 21.1.248", "NeoForge")]
    [InlineData("neo-forge", "NeoForge")]
    [InlineData("Forge 47.3.0", "Forge")]
    [InlineData("fabric-loader", "Fabric")]
    [InlineData("Quilt", "Quilt")]
    [InlineData("custom runtime", null)]
    public void LoaderFormatting_UsesOnlySupportedLoaderFamilies(
        string rawLoader,
        string? expected)
    {
        Assert.Equal(expected, ClientCatalogVersionDisplayFormatter.FormatLoader(rawLoader));
    }

    [Fact]
    public void ModVersion_ShowsItsVersionGameAndActualLoader()
    {
        var item = CreateContentVersion(
            MinecraftClientContentKind.Mod,
            "Sodium",
            "Sodium 0.8.13 for NeoForge 1.21.1",
            "mc1.21.1-0.8.13-neoforge",
            ["neoforge"],
            preferredLoader: "NeoForge");

        Assert.Equal("Sodium 0.8.13", item.ContentVersionDisplay);
        Assert.Equal("MC 1.21.1", item.GameVersionDisplay);
        Assert.Equal("NeoForge", item.LoaderDisplay);
        Assert.Equal("Sodium 0.8.13 · MC 1.21.1 · NeoForge", item.DisplayName);
    }

    [Theory]
    [InlineData(MinecraftClientContentKind.ResourcePack, "Fresh Animations", "Fresh Animations v1.9.3")]
    [InlineData(MinecraftClientContentKind.ShaderPack, "Complementary Shaders", "Complementary Shaders v5.4")]
    public void NonModVersion_OmitsMeaninglessLoaderMetadata(
        MinecraftClientContentKind kind,
        string projectTitle,
        string versionName)
    {
        var item = CreateContentVersion(
            kind,
            projectTitle,
            versionName,
            versionName,
            ["fabric", "forge"],
            preferredLoader: "Forge");

        Assert.Equal(string.Empty, item.LoaderDisplay);
        Assert.Equal($"{versionName} · MC 1.21.1", item.DisplayName);
        Assert.DoesNotContain("Forge", item.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fabric", item.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModArchiveFileName_IsReducedToNameVersionGameAndLoader()
    {
        var item = CreateContentVersion(
            MinecraftClientContentKind.Mod,
            "Sodium",
            "Sodium 0.8.13.jar - Minecraft 1.21.1 - NeoForge",
            "mc1.21.1-0.8.13-neoforge.jar",
            ["neoforge"],
            preferredLoader: "NeoForge");

        Assert.Equal("Sodium 0.8.13", item.ContentVersionDisplay);
        Assert.Equal("Sodium 0.8.13 · MC 1.21.1 · NeoForge", item.DisplayName);
        Assert.DoesNotContain(".jar", item.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static ClientContentDownloadVersionItemViewModel CreateContentVersion(
        MinecraftClientContentKind kind,
        string projectTitle,
        string versionName,
        string versionNumber,
        IReadOnlyList<string> loaders,
        string? preferredLoader)
    {
        var version = new ModrinthClientContentVersion(
            "project",
            "version",
            versionName,
            versionNumber,
            ["1.21.1"],
            loaders,
            DateTimeOffset.UnixEpoch,
            [],
            []);
        return new ClientContentDownloadVersionItemViewModel(
            version,
            projectTitle,
            kind,
            "1.21.1",
            preferredLoader);
    }
}
