using System.IO;
using System.Reflection;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ConsoleDiagnosticOutputPersistenceTests
{
    [Fact]
    public async Task LegacyMissingPreference_IsMixedAndUserTogglePersistsImmediately()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        var serverRoot = Path.Combine(temporary.Path, "servers", "legacy");
        Directory.CreateDirectory(serverRoot);
        await File.WriteAllBytesAsync(Path.Combine(serverRoot, "server.jar"), []);
        var settingsPath = Path.Combine(temporary.Path, "manager.json");
        using (var store = new JsonSettingsStore<ManagerSettings>(settingsPath))
        {
            await store.SaveAsync(new ManagerSettings
            {
                SchemaVersion = 4,
                Instances =
                [
                    new ServerInstance
                    {
                        Name = "Legacy",
                        DirectoryPath = serverRoot,
                        ServerJarPath = Path.Combine(serverRoot, "server.jar"),
                        SeparateDiagnosticOutput = null
                    }
                ]
            });
        }

        await using var main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        await main.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(main.Servers);
        Assert.Null(server.Model.SeparateDiagnosticOutput);
        Assert.False(server.SeparateDiagnosticOutput);

        server.SeparateDiagnosticOutput = true;
        await main.LastDiagnosticOutputPreferenceSave;
        var enabled = await LoadPersistedPreferenceAsync(settingsPath, expected: true);
        Assert.True(enabled.SchemaVersion >= 5);

        server.SeparateDiagnosticOutput = false;
        await main.LastDiagnosticOutputPreferenceSave;
        var disabled = await LoadPersistedPreferenceAsync(settingsPath, expected: false);
        Assert.True(Assert.Single(disabled.Instances).SeparateDiagnosticOutput == false);
    }

    [Fact]
    public async Task NewInstanceSnapshot_DefaultsPreferenceOnBeforeAtomicPersistence()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        await using var main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
        var model = new ServerInstance
        {
            Name = "New",
            DirectoryPath = Path.Combine(temporary.Path, "servers", "new"),
            ServerJarPath = "server.jar",
            SeparateDiagnosticOutput = null
        };
        var method = typeof(MainWindowViewModel).GetMethod(
            "PersistNewInstanceSnapshotAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(MainWindowViewModel),
                "PersistNewInstanceSnapshotAsync");

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(main, [model, CancellationToken.None]));
        await task;

        Assert.True(model.SeparateDiagnosticOutput == true);
        var persisted = await LoadPersistedPreferenceAsync(
            Path.Combine(temporary.Path, "manager.json"),
            expected: true);
        Assert.True(persisted.SchemaVersion >= 5);
    }

    private static async Task<ManagerSettings> LoadPersistedPreferenceAsync(
        string settingsPath,
        bool expected)
    {
        using var store = new JsonSettingsStore<ManagerSettings>(settingsPath);
        var settings = Assert.IsType<ManagerSettings>(await store.LoadAsync());
        Assert.Equal(expected, Assert.Single(settings.Instances).SeparateDiagnosticOutput);
        return settings;
    }
}
