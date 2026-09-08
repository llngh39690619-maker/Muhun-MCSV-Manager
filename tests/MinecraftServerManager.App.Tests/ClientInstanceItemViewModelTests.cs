using System.Collections.Specialized;
using System.IO;
using MinecraftServerManager.App.Services;
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

    [Fact]
    public void HeroProjection_PrefersPreviewAndRefreshesMemoryAfterModelReplacement()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        var instanceRoot = Path.Combine(temporary.Path, "instance");
        var assets = Path.Combine(instanceRoot, ".x-mcsv", "assets");
        Directory.CreateDirectory(assets);
        var icon = Path.Combine(assets, "catalog-icon.png");
        var preview = Path.Combine(assets, "catalog-preview.png");
        File.WriteAllBytes(icon, [1]);
        File.WriteAllBytes(preview, [2]);
        var original = new MinecraftClientInstance
        {
            Id = Guid.NewGuid(),
            Name = "Hero projection",
            GameVersion = "1.21.1",
            DirectoryPath = instanceRoot,
            CatalogIconImagePath = icon,
            CatalogPreviewImagePath = preview,
            CatalogProvider = "ftb",
            JavaMajorVersion = 21,
            JavaExecutablePath = @"C:\Runtimes\Java 21\bin\javaw.exe",
            JvmArguments = ["-XX:+UseG1GC", "-Dfile.encoding=UTF-8"],
            MinimumMemoryMb = 2048,
            MaximumMemoryMb = 4096,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 14, 6, 30, 0, TimeSpan.Zero),
        };
        var viewModel = new ClientInstanceItemViewModel(original);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { } name)
            {
                changed.Add(name);
            }
        };

        Assert.Equal(preview, viewModel.HeroImagePath);
        Assert.Equal(4096, viewModel.MaximumMemoryMb);
        Assert.Equal("Java 21", viewModel.JavaDisplay);
        Assert.Equal(@"C:\Runtimes\Java 21\bin\javaw.exe", viewModel.JavaExecutableDisplay);
        Assert.Equal("-XX:+UseG1GC -Dfile.encoding=UTF-8", viewModel.JvmArgumentsText);
        Assert.Equal(instanceRoot, viewModel.DirectoryPath);
        Assert.Equal("FTB", viewModel.CatalogProviderText);
        Assert.Equal("2,048–4,096 MB", viewModel.MemoryRangeText);

        viewModel.ReplaceModel(new MinecraftClientInstance
        {
            Id = original.Id,
            Name = original.Name,
            GameVersion = original.GameVersion,
            DirectoryPath = instanceRoot,
            CatalogIconImagePath = icon,
            CatalogProvider = "curseforge",
            JavaMajorVersion = 17,
            MinimumMemoryMb = 3072,
            MaximumMemoryMb = 8192,
        });

        Assert.Equal(icon, viewModel.HeroImagePath);
        Assert.Equal(8192, viewModel.MaximumMemoryMb);
        Assert.Equal("Java 17", viewModel.JavaDisplay);
        Assert.Equal(
            LocalizationService.Current.Get("client.vm.instance.javaExecutableAutomatic"),
            viewModel.JavaExecutableDisplay);
        Assert.Equal(
            LocalizationService.Current.Get("client.vm.instance.jvmArgumentsDefault"),
            viewModel.JvmArgumentsText);
        Assert.Equal("CurseForge", viewModel.CatalogProviderText);
        Assert.Equal("3,072–8,192 MB", viewModel.MemoryRangeText);
        Assert.Contains(nameof(ClientInstanceItemViewModel.HeroImagePath), changed);
        Assert.Contains(nameof(ClientInstanceItemViewModel.MaximumMemoryMb), changed);
        Assert.Contains(nameof(ClientInstanceItemViewModel.JavaDisplay), changed);
        Assert.Contains(nameof(ClientInstanceItemViewModel.JavaExecutableDisplay), changed);
        Assert.Contains(nameof(ClientInstanceItemViewModel.JvmArgumentsText), changed);
        Assert.Contains(nameof(ClientInstanceItemViewModel.DirectoryPath), changed);
        Assert.Contains(nameof(ClientInstanceItemViewModel.CatalogProviderText), changed);
        Assert.Contains(nameof(ClientInstanceItemViewModel.CreatedAtText), changed);
        Assert.Contains(nameof(ClientInstanceItemViewModel.MemoryRangeText), changed);
    }

}
