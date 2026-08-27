using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void NewSettings_UseCurrentSchemaVersion()
    {
        Assert.Equal(ManagerSettings.CurrentSchemaVersion, new ManagerSettings().SchemaVersion);
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsNull()
    {
        using var directory = new TemporaryDirectory();
        using var store = new JsonSettingsStore<ManagerSettings>(
            Path.Combine(directory.Path, "config", "manager.json"));

        var loaded = await store.LoadAsync();

        Assert.Null(loaded);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsSettingsAndEnums()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "config", "manager.json");
        using var store = new JsonSettingsStore<ManagerSettings>(filePath);
        var id = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var settings = new ManagerSettings
        {
            ServiceServerAppearances = new Dictionary<Guid, ServerAppearancePreference>
            {
                [serviceId] = new()
                {
                    BackgroundImagePath = "themes/backgrounds/service.png",
                    BackgroundImageOpacity = 0.42,
                    IconImagePath = "themes/icons/service.png",
                    CatalogIconImagePath = "themes/catalog-icons/service.png",
                    CatalogPreviewImagePath = "themes/catalog-previews/service.png",
                },
            },
            Instances =
            [
                new ServerInstance
                {
                    Id = id,
                    Name = "生存服",
                    DirectoryPath = "servers/survival",
                    ServerJarPath = "paper.jar",
                    CoreType = CoreType.Paper,
                    MinecraftVersion = "1.21.4",
                    JavaMajorVersion = 21,
                    JvmArguments = ["-XX:+UseG1GC"],
                    SeparateDiagnosticOutput = true,
                    CatalogIconImagePath = "cache/modpack-artwork/icons/example.png",
                    CatalogPreviewImagePath = "cache/modpack-artwork/previews/example.png",
                    ModpackProviderId = "modrinth",
                }
            ]
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        var instance = Assert.Single(loaded.Instances);
        Assert.Equal(id, instance.Id);
        Assert.Equal("生存服", instance.Name);
        Assert.Equal(CoreType.Paper, instance.CoreType);
        Assert.Equal(21, instance.JavaMajorVersion);
        Assert.True(instance.SeparateDiagnosticOutput);
        Assert.Equal("cache/modpack-artwork/icons/example.png", instance.CatalogIconImagePath);
        Assert.Equal("cache/modpack-artwork/previews/example.png", instance.CatalogPreviewImagePath);
        Assert.Equal("modrinth", instance.ModpackProviderId);
        Assert.Contains("\"coreType\": \"paper\"", await File.ReadAllTextAsync(filePath));
        Assert.Contains("\"separateDiagnosticOutput\": true", await File.ReadAllTextAsync(filePath));
        Assert.Contains("\"modpackProviderId\": \"modrinth\"", await File.ReadAllTextAsync(filePath));
        var serviceAppearance = Assert.Single(loaded.ServiceServerAppearances);
        Assert.Equal(serviceId, serviceAppearance.Key);
        Assert.Equal("themes/backgrounds/service.png", serviceAppearance.Value.BackgroundImagePath);
        Assert.Equal(0.42, serviceAppearance.Value.BackgroundImageOpacity);
        Assert.Equal("themes/icons/service.png", serviceAppearance.Value.IconImagePath);
        Assert.Equal("themes/catalog-icons/service.png", serviceAppearance.Value.CatalogIconImagePath);
        Assert.Equal("themes/catalog-previews/service.png", serviceAppearance.Value.CatalogPreviewImagePath);
    }

    [Fact]
    public async Task SaveAsync_OverwritesWithValidJsonAndLeavesNoTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "manager.json");
        using var store = new JsonSettingsStore<ManagerSettings>(filePath);

        await store.SaveAsync(new ManagerSettings
        {
            Instances = [new ServerInstance { Name = "first" }]
        });
        await store.SaveAsync(new ManagerSettings
        {
            Instances = [new ServerInstance { Name = "second" }]
        });

        var loaded = await store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("second", Assert.Single(loaded.Instances).Name);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task LoadAsync_LegacyInstanceWithoutLaunchFields_DefaultsToExecutableJar()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "manager.json");
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "instances": [
                {
                  "name": "legacy",
                  "directoryPath": "servers/legacy",
                  "serverJarPath": "paper.jar",
                  "coreType": "paper"
                }
              ]
            }
            """);
        using var store = new JsonSettingsStore<ManagerSettings>(filePath);

        var loaded = await store.LoadAsync();

        var instance = Assert.Single(loaded!.Instances);
        Assert.Equal(ServerLaunchKind.ExecutableJar, instance.LaunchKind);
        Assert.Empty(instance.JavaArgumentFilePaths);
        Assert.Null(instance.SourceLaunchScriptPath);
        Assert.Null(instance.StopCommand);
        Assert.Null(instance.SeparateDiagnosticOutput);
        Assert.False(instance.SeparateDiagnosticOutput == true);
        Assert.Null(instance.CatalogIconImagePath);
        Assert.Null(instance.CatalogPreviewImagePath);
        Assert.Null(instance.ModpackProviderId);
        Assert.Equal("paper.jar", instance.ServerJarPath);
    }

    [Fact]
    public async Task LoadAsync_ExplicitNullDiagnosticSeparation_IsEffectivelyDisabled()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "manager.json");
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "schemaVersion": 5,
              "instances": [
                {
                  "name": "nullable-setting",
                  "separateDiagnosticOutput": null
                }
              ]
            }
            """);
        using var store = new JsonSettingsStore<ManagerSettings>(filePath);

        var instance = Assert.Single((await store.LoadAsync())!.Instances);

        Assert.Null(instance.SeparateDiagnosticOutput);
        Assert.False(instance.SeparateDiagnosticOutput == true);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsArgumentFileLaunchFieldsInOrder()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "manager.json");
        using var store = new JsonSettingsStore<ManagerSettings>(filePath);
        var expected = new ServerInstance
        {
            Name = "FTB",
            DirectoryPath = "packs/ftb",
            ServerJarPath = string.Empty,
            LaunchKind = ServerLaunchKind.JavaArgumentFiles,
            JavaArgumentFilePaths =
            [
                "user_jvm_args.txt",
                "libraries/net/neoforged/neoforge/21.1.248/win_args.txt",
            ],
            SourceLaunchScriptPath = "packs/ftb/run.bat",
        };

        await store.SaveAsync(new ManagerSettings { Instances = [expected] });
        var loaded = Assert.Single((await store.LoadAsync())!.Instances);

        Assert.Equal(ServerLaunchKind.JavaArgumentFiles, loaded.LaunchKind);
        Assert.Equal(expected.JavaArgumentFilePaths, loaded.JavaArgumentFilePaths);
        Assert.Equal(expected.SourceLaunchScriptPath, loaded.SourceLaunchScriptPath);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsGlobalAppearanceAndPattern()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "manager.json");
        using var store = new JsonSettingsStore<ManagerSettings>(filePath);
        var settings = new ManagerSettings
        {
            Appearance = new ApplicationAppearanceSettings
            {
                WindowColor = "#010203",
                AccentColor = "#AABBCC",
                Pattern = AppearancePattern.Diagonal,
                PatternOpacity = 0.2,
                BackgroundImageOpacity = 0.3
            }
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("#010203", loaded.Appearance.WindowColor);
        Assert.Equal("#AABBCC", loaded.Appearance.AccentColor);
        Assert.Equal(AppearancePattern.Diagonal, loaded.Appearance.Pattern);
        Assert.Equal(0.2, loaded.Appearance.PatternOpacity);
        Assert.Contains("\"pattern\": \"diagonal\"", await File.ReadAllTextAsync(filePath));
    }
}
