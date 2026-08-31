using System.Collections.Specialized;
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
        var finalLinePublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void ObservePublishedBatch(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (viewModel.GameLogLines.Count > 0 &&
                string.Equals(viewModel.GameLogLines[^1], "line-4999", StringComparison.Ordinal))
            {
                finalLinePublished.TrySetResult(true);
            }
        }

        viewModel.GameLogLines.CollectionChanged += ObservePublishedBatch;
        try
        {
            for (var index = 0; index < 5_000; index++)
            {
                viewModel.QueueGameLogLine($"line-{index}");
            }

            await finalLinePublished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            viewModel.GameLogLines.CollectionChanged -= ObservePublishedBatch;
        }

        Assert.Equal(2_000, viewModel.GameLogLines.Count);
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

}
