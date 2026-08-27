using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class PlayerRegistryLoadingTests
{
    [Fact]
    public async Task SelectingServer_DoesNotReadPlayerRegistryUntilPlayersTabIsVisible()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "usercache.json"),
            """[{"name":"LazyUser","uuid":"lazy-id"}]""");
        await using var main = new MainWindowViewModel(new ApplicationPaths(directory.Path));
        var server = new ServerInstanceViewModel(
            new ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = "Lazy Registry",
                DirectoryPath = directory.Path,
                ServerJarPath = Path.Combine(directory.Path, "server.jar")
            },
            static (_, _) => Task.CompletedTask);
        main.Servers.Add(server);

        main.SelectedServer = server;

        Assert.Equal(MainWindowViewModel.ConsoleWorkspaceTabKey, main.SelectedWorkspaceTabKey);
        Assert.Empty(server.Players);
        Assert.True(main.LastPlayerRegistryReload.IsCompleted);

        main.SelectedWorkspaceTabKey = MainWindowViewModel.PlayersWorkspaceTabKey;
        await main.LastPlayerRegistryReload;

        Assert.Equal("LazyUser", Assert.Single(server.Players).Name);
    }

    [Fact]
    public async Task ReturningToPlayersTab_UsesLoadedSnapshotUntilManualRefresh()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "usercache.json"),
            """[{"name":"CachedUser","uuid":"cached-id"}]""");
        await using var main = new MainWindowViewModel(new ApplicationPaths(directory.Path));
        var server = new ServerInstanceViewModel(
            new ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = "Cached Registry",
                DirectoryPath = directory.Path,
                ServerJarPath = Path.Combine(directory.Path, "server.jar")
            },
            static (_, _) => Task.CompletedTask);
        main.Servers.Add(server);
        main.SelectedServer = server;
        main.SelectedWorkspaceTabKey = MainWindowViewModel.PlayersWorkspaceTabKey;
        await main.LastPlayerRegistryReload;
        var completedLoad = main.LastPlayerRegistryReload;

        main.SelectedWorkspaceTabKey = MainWindowViewModel.ConsoleWorkspaceTabKey;
        main.SelectedWorkspaceTabKey = MainWindowViewModel.PlayersWorkspaceTabKey;

        Assert.Same(completedLoad, main.LastPlayerRegistryReload);
        Assert.Equal("CachedUser", Assert.Single(server.Players).Name);
    }
}
