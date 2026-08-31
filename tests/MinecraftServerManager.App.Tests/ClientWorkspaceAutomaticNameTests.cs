using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientWorkspaceAutomaticNameTests
{
    [Fact]
    public async Task UntouchedVanillaName_TracksTheSelectedGameVersion()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = CreateViewModel(directory.Path);

        viewModel.SelectedRelease = Release("1.14.1");
        Assert.Equal("Minecraft 1.14.1", viewModel.NewInstanceName);

        viewModel.SelectedRelease = Release("1.21.1");
        Assert.Equal("Minecraft 1.21.1", viewModel.NewInstanceName);
    }

    [Theory]
    [InlineData(MinecraftClientLoader.Fabric, "Fabric 1.14.1")]
    [InlineData(MinecraftClientLoader.Forge, "Forge 1.14.1")]
    [InlineData(MinecraftClientLoader.Quilt, "Quilt 1.14.1")]
    [InlineData(MinecraftClientLoader.NeoForge, "NeoForge 1.14.1")]
    [InlineData(MinecraftClientLoader.OptiFine, "OptiFine 1.14.1")]
    [InlineData(MinecraftClientLoader.LabyMod, "LabyMod 1.14.1")]
    public async Task UntouchedName_UsesTheSelectedLoaderAndGameVersion(
        MinecraftClientLoader loader,
        string expectedName)
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = CreateViewModel(directory.Path);
        viewModel.SelectedRelease = Release("1.14.1");

        viewModel.SelectedLoader = Loader(loader, "1.14.1");

        Assert.Equal(expectedName, viewModel.NewInstanceName);
    }

    [Fact]
    public async Task UntouchedName_TracksLoaderChangesButNotTheSpecificLoaderBuild()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = CreateViewModel(directory.Path);
        viewModel.SelectedRelease = Release("1.14.1");
        var fabric = Loader(MinecraftClientLoader.Fabric, "1.14.1", "0.16.10", "0.16.9");
        viewModel.SelectedLoader = fabric;

        viewModel.SelectedLoaderVersion = fabric.Versions[1];
        Assert.Equal("Fabric 1.14.1", viewModel.NewInstanceName);

        viewModel.SelectedLoader = Loader(MinecraftClientLoader.Forge, "1.14.1");
        Assert.Equal("Forge 1.14.1", viewModel.NewInstanceName);
    }

    [Fact]
    public async Task ManualName_IsPreservedAcrossGameLoaderAndLoaderVersionChanges()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = CreateViewModel(directory.Path);
        viewModel.SelectedRelease = Release("1.14.1");
        var fabric = Loader(MinecraftClientLoader.Fabric, "1.14.1", "0.16.10", "0.16.9");
        viewModel.SelectedLoader = fabric;
        viewModel.NewInstanceName = "我的自訂客戶端";

        viewModel.SelectedLoaderVersion = fabric.Versions[1];
        viewModel.SelectedRelease = Release("1.21.1");
        viewModel.SelectedLoader = Loader(MinecraftClientLoader.Forge, "1.21.1");

        Assert.Equal("我的自訂客戶端", viewModel.NewInstanceName);
    }

    [Fact]
    public async Task ManuallyClearedName_IsNotRepopulatedByVersionOrLoaderChanges()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = CreateViewModel(directory.Path);
        viewModel.SelectedRelease = Release("1.14.1");
        viewModel.NewInstanceName = string.Empty;

        viewModel.SelectedRelease = Release("1.21.1");
        viewModel.SelectedLoader = Loader(MinecraftClientLoader.Fabric, "1.21.1");

        Assert.Equal(string.Empty, viewModel.NewInstanceName);
    }

    [Fact]
    public async Task ReinvokingCreateWhileItIsOpen_DoesNotOverwriteTheManualName()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = CreateViewModel(directory.Path);
        viewModel.NewInstanceCommand.Execute(null);
        viewModel.SelectedRelease = Release("1.14.1");
        viewModel.SelectedLoader = Loader(MinecraftClientLoader.Fabric, "1.14.1");
        viewModel.NewInstanceName = "同一頁保留名稱";

        viewModel.NewInstanceCommand.Execute(null);

        Assert.Equal("同一頁保留名稱", viewModel.NewInstanceName);
    }

    [Fact]
    public async Task ReopeningCreatePage_RestoresAutomaticNamingForTheCurrentSelection()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = CreateViewModel(directory.Path);
        viewModel.NewInstanceCommand.Execute(null);
        viewModel.SelectedRelease = Release("1.14.1");
        viewModel.SelectedLoader = Loader(MinecraftClientLoader.Fabric, "1.14.1");
        viewModel.NewInstanceName = "暫時名稱";
        viewModel.CloseCreateCommand.Execute(null);

        viewModel.NewInstanceCommand.Execute(null);

        Assert.Equal("Fabric 1.14.1", viewModel.NewInstanceName);
    }

    private static ClientWorkspaceViewModel CreateViewModel(string path) =>
        new(
            new ApplicationPaths(path),
            static () => new NewMinecraftClientDefaultsSettings());

    private static MinecraftReleaseInfo Release(string version) =>
        new(
            version,
            DateTimeOffset.UtcNow,
            new Uri($"https://piston-meta.mojang.com/v1/packages/{new string('a', 40)}/{version}.json"),
            new string('a', 40),
            1);

    private static ClientLoaderChoiceViewModel Loader(
        MinecraftClientLoader loader,
        string gameVersion,
        params string[] loaderVersions)
    {
        var versions = (loaderVersions.Length == 0 ? ["1.0.0"] : loaderVersions)
            .Select(version => new MinecraftLoaderCatalogEntry(
                loader,
                gameVersion,
                version,
                MinecraftLoaderReleaseChannel.Stable,
                MinecraftClientLoaderInstallKind.Managed,
                new Uri("https://example.com/official/"),
                new Uri("https://example.com/official/loader.jar"),
                "fixture"))
            .ToArray();
        return new ClientLoaderChoiceViewModel(loader, versions);
    }
}
