using System.Globalization;
using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientDashboardProjectionTests
{
    [Fact]
    public async Task ModsPreview_IsBoundedAndIndependentFromTheSelectedContentCategory()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        InitializeLocalization(directory.Path);
        var firstRoot = CreateInstanceContent(directory.Path, "first", modCount: 7, resourcePackCount: 2);
        var secondRoot = CreateInstanceContent(directory.Path, "second", modCount: 2, resourcePackCount: 0);
        await using var viewModel = new ClientWorkspaceViewModel(
            new ApplicationPaths(directory.Path),
            static () => new NewMinecraftClientDefaultsSettings());

        viewModel.SelectedInstance = CreateInstance("First", firstRoot);
        await viewModel.RefreshDashboardModsAsync();

        Assert.Equal(5, viewModel.DashboardModItems.Count);
        Assert.All(viewModel.DashboardModItems, item =>
        {
            Assert.EndsWith(".jar", item.Name, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("first-mod-", item.Name, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal("模組 (7)", viewModel.DashboardModsHeading);
        Assert.Equal("顯示前 5 個，共 7 個。", viewModel.DashboardModsStatusText);

        viewModel.SelectContentKindCommand.Execute(nameof(MinecraftClientContentKind.ResourcePack));
        Assert.Equal(MinecraftClientContentKind.ResourcePack, viewModel.SelectedContentKind);
        await viewModel.RefreshDashboardModsAsync();

        Assert.Equal(5, viewModel.DashboardModItems.Count);
        Assert.Equal("模組 (7)", viewModel.DashboardModsHeading);
        Assert.All(viewModel.DashboardModItems, item =>
            Assert.StartsWith("first-mod-", item.Name, StringComparison.OrdinalIgnoreCase));

        viewModel.SelectedInstance = CreateInstance("Second", secondRoot);
        await viewModel.RefreshDashboardModsAsync();

        Assert.Equal(2, viewModel.DashboardModItems.Count);
        Assert.Equal("模組 (2)", viewModel.DashboardModsHeading);
        Assert.All(viewModel.DashboardModItems, item =>
            Assert.StartsWith("second-mod-", item.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static ClientInstanceItemViewModel CreateInstance(string name, string rootPath) =>
        new(new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = name,
            GameVersion = "1.21.1",
            DirectoryPath = rootPath,
        });

    private static string CreateInstanceContent(
        string rootPath,
        string name,
        int modCount,
        int resourcePackCount)
    {
        var instanceRoot = Path.Combine(rootPath, name);
        var mods = Path.Combine(instanceRoot, "mods");
        var resourcePacks = Path.Combine(instanceRoot, "resourcepacks");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(resourcePacks);
        for (var index = 0; index < modCount; index++)
        {
            File.WriteAllBytes(Path.Combine(mods, $"{name}-mod-{index:D2}.jar"), [1]);
        }

        for (var index = 0; index < resourcePackCount; index++)
        {
            File.WriteAllBytes(Path.Combine(resourcePacks, $"{name}-resource-{index:D2}.zip"), [2]);
        }

        return instanceRoot;
    }

    private static void InitializeLocalization(string rootPath) =>
        LocalizationService.Current.Initialize(
            Path.Combine(rootPath, "language.json"),
            CultureInfo.GetCultureInfo("zh-TW"));
}
