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
        var preview = Path.Combine(Path.GetTempPath(), "catalog-preview.webp");
        var viewModel = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "Artwork fallback",
            GameVersion = "1.21.1",
            DirectoryPath = Path.GetTempPath(),
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
