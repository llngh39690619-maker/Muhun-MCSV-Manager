using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientContentDownloadProjectItemViewModelTests
{
    [Fact]
    public void DiscoveryMetadata_IsProjectedFromTheRealCatalogProject()
    {
        var modified = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var project = new ModrinthClientContentProject(
            "project-id",
            "project-slug",
            MinecraftClientContentKind.Mod,
            "Real Project",
            "Real summary",
            "Real Author",
            new Uri("https://cdn.example.test/icon.png"),
            ["1.21.1", "1.21"],
            ["neoforge"],
            1_250,
            modified,
            new Uri("https://modrinth.com/mod/project-slug"));

        var item = new ClientContentDownloadProjectItemViewModel(
            project,
            "1,250 downloads · neoforge",
            "1.21.1");

        Assert.Equal("Real Project", item.Title);
        Assert.Equal("Real summary", item.Summary);
        Assert.Equal("1.21.1", item.GameVersionText);
        Assert.Equal("Minecraft 1.21.1 · neoforge", item.CompatibilityDetailText);
        Assert.Contains("Real Author", item.LocalizedAuthorText, StringComparison.Ordinal);
        Assert.Contains("1.3K", item.DownloadText, StringComparison.Ordinal);
        Assert.Contains("2026-09-01", item.UpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyDetails_RefreshesEveryDiscoveryMetricThatCanChange()
    {
        var initial = CreateProject(downloads: 12, modifiedDay: 1, author: "Initial Author");
        var item = new ClientContentDownloadProjectItemViewModel(
            initial,
            "12 downloads · fabric",
            "1.21.1");
        var changedProperties = new List<string>();
        item.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName ?? string.Empty);

        item.ApplyDetails(CreateProject(downloads: 42, modifiedDay: 2, author: "Updated Author"));

        Assert.Equal(42, item.Downloads);
        Assert.Equal("Updated Author", item.Author);
        Assert.Contains(nameof(item.LocalizedAuthorText), changedProperties);
        Assert.Contains(nameof(item.DownloadText), changedProperties);
        Assert.Contains(nameof(item.UpdatedText), changedProperties);
    }

    private static ModrinthClientContentProject CreateProject(
        long downloads,
        int modifiedDay,
        string author) =>
        new(
            "project-id",
            "project-slug",
            MinecraftClientContentKind.Mod,
            "Project",
            "Summary",
            author,
            null,
            ["1.21.1"],
            ["fabric"],
            downloads,
            new DateTimeOffset(2026, 9, modifiedDay, 12, 0, 0, TimeSpan.Zero),
            new Uri("https://modrinth.com/mod/project-slug"));
}
