using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientWorkspaceEmptyStateTests
{
    [Fact]
    public async Task EmptyWorkspace_StaysBlankUntilCreateIsRequested()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new ClientWorkspaceViewModel(
            new ApplicationPaths(directory.Path),
            static () => new NewMinecraftClientDefaultsSettings());

        Assert.False(viewModel.HasSelectedInstance);
        Assert.False(viewModel.IsCreatePage);
        Assert.False(viewModel.IsCatalogPage);
        Assert.False(viewModel.IsSettingsPage);
        Assert.False(viewModel.IsDashboardPage);

        viewModel.NewInstanceCommand.Execute(null);

        Assert.True(viewModel.IsCreatePage);
        Assert.False(viewModel.IsDashboardPage);
        Assert.True(viewModel.CloseCreateCommand.CanExecute(null));

        viewModel.CloseCreateCommand.Execute(null);

        Assert.False(viewModel.IsCreatePage);
        Assert.False(viewModel.IsDashboardPage);
    }

    [Fact]
    public async Task SelectingDownloadedInstance_ShowsItsDashboard()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new ClientWorkspaceViewModel(
            new ApplicationPaths(directory.Path),
            static () => new NewMinecraftClientDefaultsSettings());
        var instance = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Name = "Downloaded client",
            GameVersion = "1.21.1",
            InstalledVersionId = "1.21.1",
            DirectoryPath = directory.Path,
        });

        viewModel.NewInstanceCommand.Execute(null);
        viewModel.SelectedInstance = instance;

        Assert.True(viewModel.HasSelectedInstance);
        Assert.False(viewModel.IsCreatePage);
        Assert.False(viewModel.IsCatalogPage);
        Assert.False(viewModel.IsSettingsPage);
        Assert.True(viewModel.IsDashboardPage);
    }

    [Fact]
    public async Task SelectingAnInstanceDuringInitialization_PreservesExplicitCreateNavigation()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new ClientWorkspaceViewModel(
            new ApplicationPaths(directory.Path),
            static () => new NewMinecraftClientDefaultsSettings());
        var instance = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Name = "Discovered while loading",
            GameVersion = "1.21.1",
            InstalledVersionId = "1.21.1",
            DirectoryPath = directory.Path,
        });

        viewModel.NewInstanceCommand.Execute(null);
        viewModel.ApplyInitialInstanceSelection(instance);

        Assert.Same(instance, viewModel.SelectedInstance);
        Assert.True(viewModel.IsCreatePage);
        Assert.False(viewModel.IsDashboardPage);
    }
}
