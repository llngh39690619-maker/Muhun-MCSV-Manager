using System.IO;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient;
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
        Assert.False(viewModel.OpenClientSettingsCommand.CanExecute(null));
        Assert.False(viewModel.OpenClientJavaSettingsCommand.CanExecute(null));
        Assert.True(viewModel.IsClientGameSettingsSection);
        Assert.False(viewModel.IsClientJavaSettingsSection);

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
    public async Task OpeningAlreadySelectedInstance_ReturnsFromCatalogToDashboard()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new ClientWorkspaceViewModel(
            new ApplicationPaths(directory.Path),
            static () => new NewMinecraftClientDefaultsSettings());
        var instance = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Name = "Existing client",
            GameVersion = "1.21.1",
            InstalledVersionId = "1.21.1",
            DirectoryPath = directory.Path,
        });
        viewModel.Instances.Add(instance);
        viewModel.SelectedInstance = instance;
        viewModel.OpenCatalogCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsCatalogPage);

        viewModel.OpenInstanceDashboardCommand.Execute(instance);

        Assert.Same(instance, viewModel.SelectedInstance);
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

    [Fact]
    public async Task SettingsEntrypoints_OpenTheirOwnGameAndJavaSections()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var instanceDirectory = Path.Combine(paths.Clients, "settings-navigation");
        Directory.CreateDirectory(instanceDirectory);
        var model = new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "Settings navigation",
            GameVersion = "1.21.1",
            InstalledVersionId = "1.21.1",
            DirectoryPath = instanceDirectory,
        };
        using (var registry = new MinecraftClientRegistry(paths.ClientRegistryFile))
        {
            await registry.SaveAsync(new MinecraftClientRegistryDocument
            {
                Instances = [model],
            });
        }

        await using var viewModel = new ClientWorkspaceViewModel(
            paths,
            static () => new NewMinecraftClientDefaultsSettings());
        viewModel.SelectedInstance = new ClientInstanceItemViewModel(model);

        viewModel.OpenClientJavaSettingsCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.IsSettingsPage && viewModel.IsClientJavaSettingsSection);

        Assert.False(viewModel.IsClientGameSettingsSection);
        viewModel.CloseClientSettingsCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.OpenClientSettingsCommand.CanExecute(null));

        viewModel.OpenClientSettingsCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.IsSettingsPage && viewModel.IsClientGameSettingsSection);

        Assert.False(viewModel.IsClientJavaSettingsSection);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The client workspace did not reach the expected navigation state.");
            }

            await Task.Delay(10);
        }
    }
}
