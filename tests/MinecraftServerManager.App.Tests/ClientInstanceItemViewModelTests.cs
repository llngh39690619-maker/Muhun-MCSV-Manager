using System.IO;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientInstanceItemViewModelTests
{
    [Fact]
    public async Task GameLogBurst_IsPublishedInBatchesAndBounded()
    {
        var model = new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "Log test",
            GameVersion = "1.21.1",
            ShowGameLog = true,
            DirectoryPath = Path.GetTempPath(),
        };
        var viewModel = new ClientInstanceItemViewModel(model);

        for (var index = 0; index < 5_000; index++)
        {
            viewModel.QueueGameLogLine($"line-{index}");
        }

        await WaitUntilAsync(() => viewModel.GameLogLines.Count > 0, TimeSpan.FromSeconds(3));

        Assert.InRange(viewModel.GameLogLines.Count, 1, 2_000);
        Assert.Equal("line-4999", viewModel.GameLogLines[^1]);
        Assert.True(viewModel.HasGameLogLines);
    }

    [Fact]
    public async Task GameLogDisabled_DoesNotRetainOutput()
    {
        var model = new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "No log",
            GameVersion = "1.21.1",
            ShowGameLog = false,
            DirectoryPath = Path.GetTempPath(),
        };
        var viewModel = new ClientInstanceItemViewModel(model);

        viewModel.QueueGameLogLine("secret output");
        await Task.Delay(150);

        Assert.Empty(viewModel.GameLogLines);
        Assert.False(viewModel.HasGameLogLines);
    }

    [Fact]
    public void IconImagePath_FallsBackToCatalogPreviewWhenNoIconExists()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        var instanceRoot = Path.Combine(temporary.Path, "instance");
        var assets = Path.Combine(instanceRoot, ".x-mcsv", "assets");
        Directory.CreateDirectory(assets);
        var preview = Path.Combine(assets, "catalog-preview.webp");
        File.WriteAllBytes(preview, "RIFF\0\0\0\0WEBP"u8.ToArray());
        var viewModel = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "Artwork fallback",
            GameVersion = "1.21.1",
            DirectoryPath = instanceRoot,
            CatalogPreviewImagePath = preview,
        });

        Assert.Equal(preview, viewModel.IconImagePath);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The client log projection did not publish in time.");
            }

            await Task.Delay(20);
        }
    }
}
