using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;
using Xunit;

namespace MinecraftServerManager.App.Tests;

public sealed class ApplicationResourceIsolationTests
{
    [Fact]
    public void AppTestAssembly_DisablesParallelizationForProcessGlobalWpfState()
    {
        var behavior = typeof(ApplicationResourceIsolationTests).Assembly
            .GetCustomAttribute<CollectionBehaviorAttribute>();

        Assert.NotNull(behavior);
        Assert.True(behavior.DisableTestParallelization);
    }

    [Fact]
    public async Task BackgroundInitialize_DoesNotMutateDispatcherOwnedApplicationResources()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        paths.EnsureCreated();
        await new JsonSettingsStore<ManagerSettings>(paths.SettingsFile).SaveAsync(new ManagerSettings
        {
            Appearance = new ApplicationAppearanceSettings
            {
                WindowColor = "#010203",
                AccentColor = "#040506"
            }
        });

        object? originalWindowColor = null;
        object? originalWindowBrush = null;
        var sentinel = Color.FromRgb(0x31, 0x41, 0x59);
        WpfStaTestHost.Run(() =>
        {
            var resources = Application.Current.Resources;
            originalWindowColor = resources[ThemeResourceKeys.WindowColor];
            originalWindowBrush = resources[ThemeResourceKeys.WindowBrush];
            resources[ThemeResourceKeys.WindowColor] = sentinel;
            resources[ThemeResourceKeys.WindowBrush] = new SolidColorBrush(sentinel);
        });

        try
        {
            await using var viewModel = new MainWindowViewModel(paths);
            await Task.Run(() => viewModel.InitializeAsync(allowInteractiveAutoImport: false));

            WpfStaTestHost.Run(() =>
            {
                var resources = Application.Current.Resources;
                Assert.Equal(sentinel, Assert.IsType<Color>(resources[ThemeResourceKeys.WindowColor]));
                Assert.Equal(
                    sentinel,
                    Assert.IsType<SolidColorBrush>(resources[ThemeResourceKeys.WindowBrush]).Color);
            });
        }
        finally
        {
            WpfStaTestHost.Run(() =>
            {
                var resources = Application.Current.Resources;
                resources[ThemeResourceKeys.WindowColor] = originalWindowColor!;
                resources[ThemeResourceKeys.WindowBrush] = originalWindowBrush!;
            });
        }
    }
}
