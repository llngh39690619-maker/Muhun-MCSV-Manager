using System.Diagnostics;
using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class AutomaticMemoryRecommendationUxTests
{
    [Fact]
    public async Task SelectingAutomatic_ReturnsImmediatelyThenUpdatesThatServerInBackground()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        var serverRoot = Path.Combine(paths.Servers, "automatic-memory");
        var mods = Directory.CreateDirectory(Path.Combine(serverRoot, "mods")).FullName;
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.jar"), "test");
        for (var index = 0; index < 51; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(mods, $"mod-{index:D2}.jar"), []);
        }

        using (var store = new JsonSettingsStore<ManagerSettings>(paths.SettingsFile))
        {
            await store.SaveAsync(new ManagerSettings
            {
                Instances =
                [
                    new ServerInstance
                    {
                        Name = "Automatic memory test",
                        DirectoryPath = serverRoot,
                        ServerJarPath = Path.Combine(serverRoot, "server.jar"),
                        CoreType = CoreType.Forge,
                        MemoryAllocationMode = MemoryAllocationMode.Manual,
                        MinimumMemoryMb = 512,
                        MaximumMemoryMb = 512,
                    },
                ],
            });
        }

        await using var manager = new MainWindowViewModel(paths);
        await manager.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(manager.Servers);

        var stopwatch = Stopwatch.StartNew();
        server.IsMemoryAutomatic = true;
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Automatic selection blocked for {stopwatch.Elapsed}.");
        await manager.LastAutomaticMemoryRecommendation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(server.IsMemoryAutomatic);
        Assert.False(server.IsAutomaticMemoryRecommendationRunning);
        Assert.InRange(server.MinimumMemoryMb, 512, server.MaximumMemoryMb);
        Assert.Contains("偵測到 51 個", server.MemoryConfigurationHint, StringComparison.Ordinal);

        server.RecalculateAutomaticMemoryCommand.Execute(null);
        server.MinimumMemorySliderMb = 3328;

        Assert.True(server.IsMemoryManual);
        Assert.False(server.IsAutomaticMemoryRecommendationRunning);
        await manager.LastAutomaticMemoryRecommendation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3328, server.MinimumMemoryMb);
    }
}
