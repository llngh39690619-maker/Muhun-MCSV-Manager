using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class CatalogArtworkPersistenceTests
{
    [Fact]
    public async Task PortableRepair_RelocatesManagedArtworkAndMigratesLegacyProviderId()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var instanceId = Guid.NewGuid();
        var serverRoot = Path.Combine(paths.Servers, "catalog-pack");
        var serverAssets = Path.Combine(serverRoot, ".mcsv", "assets");
        var previewRoot = paths.OnlineModpackArtworkCache;
        Directory.CreateDirectory(serverAssets);
        Directory.CreateDirectory(previewRoot);
        var icon = Path.Combine(serverAssets, "catalog-icon.png");
        var preview = Path.Combine(previewRoot, $"{instanceId:N}.jpg");
        await File.WriteAllBytesAsync(icon, [1]);
        await File.WriteAllBytesAsync(preview, [2]);
        var model = new ServerInstance
        {
            Id = instanceId,
            Name = "Portable catalog pack",
            DirectoryPath = serverRoot,
            ModpackSource = ModpackSourceKind.Modrinth,
            CatalogIconImagePath = Path.Combine("Z:\\old-portable", "catalog-icon.png"),
            CatalogPreviewImagePath = Path.Combine("Z:\\old-portable", $"{instanceId:N}.jpg"),
        };

        await using var viewModel = new MainWindowViewModel(paths);
        viewModel.RepairPortablePaths(model);

        Assert.Equal(Path.GetFullPath(icon), model.CatalogIconImagePath);
        Assert.Equal(Path.GetFullPath(preview), model.CatalogPreviewImagePath);
        Assert.Equal("modrinth", model.ModpackProviderId);
    }

    [Fact]
    public async Task PortableRepair_DropsCatalogArtworkOutsideManagedRootsButKeepsUserIcon()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(Path.Combine(temporary.Path, "application"));
        paths.EnsureCreated();
        var outside = Path.Combine(temporary.Path, "outside.png");
        await File.WriteAllBytesAsync(outside, [1]);
        var model = new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = "Untrusted artwork",
            DirectoryPath = Path.Combine(paths.Servers, "missing"),
            IconImagePath = outside,
            CatalogIconImagePath = outside,
            CatalogPreviewImagePath = outside,
            ModpackProviderId = "  CURSEFORGE  ",
        };

        await using var viewModel = new MainWindowViewModel(paths);
        viewModel.RepairPortablePaths(model);

        Assert.Equal(outside, model.IconImagePath);
        Assert.Null(model.CatalogIconImagePath);
        Assert.Null(model.CatalogPreviewImagePath);
        Assert.Equal("curseforge", model.ModpackProviderId);
    }

    [Fact]
    public void SchemaTwelveMigration_IsMonotonicAndDoesNotDiscardUnknownFutureProvider()
    {
        var settings = new ManagerSettings
        {
            SchemaVersion = 10,
            RemoteControl = new RemoteControlSettings { Enabled = true },
        };

        Assert.True(MainWindowViewModel.ApplyRemoteAutoStartMigration(settings));
        Assert.Equal(12, ManagerSettings.CurrentSchemaVersion);
        Assert.Equal(ManagerSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.RemoteControl.Enabled);
    }
}
