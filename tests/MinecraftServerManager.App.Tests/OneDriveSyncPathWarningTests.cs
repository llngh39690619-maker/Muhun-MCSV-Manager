using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class OneDriveSyncPathWarningTests
{
    [Fact]
    public void ConfiguredRootCheck_IsCanonicalCaseInsensitiveAndBoundarySafe()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"sync-path-{Guid.NewGuid():N}");
        var root = Path.Combine(parent, "OneDrive");

        Assert.True(OneDriveSyncPathDetector.IsInConfiguredRoot(root, [root]));
        Assert.True(OneDriveSyncPathDetector.IsInConfiguredRoot(
            Path.Combine(root.ToUpperInvariant(), "servers", "..", "servers", "Paper"),
            [root.ToLowerInvariant()]));
        Assert.False(OneDriveSyncPathDetector.IsInConfiguredRoot(
            Path.Combine(parent, "OneDriveBackup", "Paper"),
            [root]));
        Assert.False(OneDriveSyncPathDetector.IsInConfiguredRoot(
            Path.Combine(parent, "LocalServers", "Paper"),
            [root]));
    }

    [Fact]
    public void ConfiguredRoots_CanBeInjectedAndUseEverySupportedOneDriveVariable()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"sync-env-{Guid.NewGuid():N}");
        var consumer = Path.Combine(parent, "Consumer");
        var commercial = Path.Combine(parent, "Commercial");
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OneDrive"] = consumer,
            ["OneDriveConsumer"] = consumer + Path.DirectorySeparatorChar,
            ["OneDriveCommercial"] = commercial
        };

        var roots = OneDriveSyncPathDetector.ReadConfiguredRoots(name => values[name]);

        Assert.Equal(2, roots.Count);
        Assert.True(OneDriveSyncPathDetector.IsInConfiguredRoot(
            Path.Combine(consumer, "ServerA"),
            roots));
        Assert.True(OneDriveSyncPathDetector.IsInConfiguredRoot(
            Path.Combine(commercial, "ServerB"),
            roots));
    }

    [Fact]
    public void ViewModel_ExposesTraditionalChineseWarningOnlyForInjectedSyncRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"OneDrive-{Guid.NewGuid():N}");
        var inside = CreateServer(Path.Combine(root, "servers", "Forge"), [root]);
        var outside = CreateServer(Path.Combine(Path.GetTempPath(), $"local-{Guid.NewGuid():N}"), [root]);

        Assert.True(inside.IsInOneDriveSyncFolder);
        Assert.Contains("世界、region 與 log 會高頻寫入", inside.OneDrivePerformanceWarning, StringComparison.Ordinal);
        Assert.Contains("先停止 Server", inside.OneDrivePerformanceWarning, StringComparison.Ordinal);
        Assert.False(outside.IsInOneDriveSyncFolder);
    }

    [Fact]
    public async Task WarningBanner_IsVisibleOnlyForSelectedServerInsideSyncRootOnRealSta()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        Directory.CreateDirectory(temporary.Path);
        MainWindowViewModel? main = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
                var root = Path.Combine(temporary.Path, "ConfiguredOneDrive");
                var inside = CreateServer(Path.Combine(root, "Inside"), [root]);
                var outside = CreateServer(Path.Combine(temporary.Path, "OneDriveBackup"), [root]);
                main.Servers.Add(inside);
                main.Servers.Add(outside);
                main.SelectedServer = inside;
                main.SelectedWorkspaceTabKey = "ServerSettings";

                var window = new MainWindow(main)
                {
                    Width = 1180,
                    Height = 760,
                    Left = -10_000,
                    Top = -10_000,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };

                window.Show();
                try
                {
                    DrainDispatcher();
                    var banner = Assert.IsType<Border>(window.FindName("OneDrivePerformanceWarningBanner"));
                    Assert.Equal(Visibility.Visible, banner.Visibility);

                    main.SelectedServer = outside;
                    DrainDispatcher();
                    Assert.Equal(Visibility.Collapsed, banner.Visibility);
                }
                finally
                {
                    window.PrepareForApplicationShutdown();
                    window.Close();
                }
            });
        }
        finally
        {
            if (main is not null)
            {
                await main.DisposeAsync();
            }
        }
    }

    [Fact]
    public void MainWindowContract_BindsNonBlockingAmberWarningToDetectionFlag()
    {
        var projectRoot = FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "MinecraftServerManager.App",
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"OneDrivePerformanceWarningBanner\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding SelectedServer.IsInOneDriveSyncFolder, Converter={StaticResource BoolToVisibility}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource WarningBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedServer.OneDrivePerformanceWarning}\"", xaml, StringComparison.Ordinal);
    }

    private static ServerInstanceViewModel CreateServer(
        string directoryPath,
        IEnumerable<string?> configuredRoots)
        => new(
            new ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = Path.GetFileName(directoryPath),
                DirectoryPath = directoryPath,
                ServerJarPath = Path.Combine(directoryPath, "server.jar")
            },
            static (_, _) => Task.CompletedTask,
            configuredOneDriveRoots: configuredRoots);

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            () => frame.Continue = false,
            DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }

    private static string FindProjectRoot([CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            ".."));
}
