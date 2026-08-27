using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ConsoleDiagnosticOutputVisualTests
{
    [Fact]
    public async Task SettingsToggleAndServerSwitch_KeepStablePhysicalTabSelectionOnRealSta()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        Directory.CreateDirectory(temporary.Path);
        MainWindowViewModel? main = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
                var first = AddServer(main, "First", separateDiagnosticOutput: true);
                var second = AddServer(main, "Second", separateDiagnosticOutput: false);
                main.SelectedServer = first;
                main.SecondaryServer = second;

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
                    var workspace = Assert.Single(VisualDescendants<TabControl>(window));
                    var tabs = workspace.Items.Cast<TabItem>().ToDictionary(
                        item => Assert.IsType<string>(item.Tag),
                        StringComparer.Ordinal);
                    var diagnostics = tabs[MainWindowViewModel.DiagnosticWorkspaceTabKey];

                    Assert.Equal(Visibility.Visible, diagnostics.Visibility);
                    main.SelectedWorkspaceTabKey = "ServerSettings";
                    DrainDispatcher();
                    Assert.Same(tabs["ServerSettings"], workspace.SelectedItem);

                    first.SeparateDiagnosticOutput = false;
                    DrainDispatcher();
                    Assert.Equal(Visibility.Collapsed, diagnostics.Visibility);
                    Assert.Equal("ServerSettings", main.SelectedWorkspaceTabKey);
                    Assert.Same(tabs["ServerSettings"], workspace.SelectedItem);

                    first.SeparateDiagnosticOutput = true;
                    DrainDispatcher();
                    Assert.Equal(Visibility.Visible, diagnostics.Visibility);
                    Assert.Equal("ServerSettings", main.SelectedWorkspaceTabKey);
                    Assert.Same(tabs["ServerSettings"], workspace.SelectedItem);

                    main.SelectedWorkspaceTabKey = MainWindowViewModel.DiagnosticWorkspaceTabKey;
                    DrainDispatcher();
                    Assert.Same(diagnostics, workspace.SelectedItem);
                    first.SeparateDiagnosticOutput = false;
                    DrainDispatcher();
                    Assert.Equal(MainWindowViewModel.ConsoleWorkspaceTabKey, main.SelectedWorkspaceTabKey);
                    Assert.Same(tabs[MainWindowViewModel.ConsoleWorkspaceTabKey], workspace.SelectedItem);

                    first.SeparateDiagnosticOutput = true;
                    main.SelectedServer = first;
                    main.SelectedWorkspaceTabKey = MainWindowViewModel.DiagnosticWorkspaceTabKey;
                    DrainDispatcher();
                    main.SelectedServer = second;
                    DrainDispatcher();
                    Assert.Equal(MainWindowViewModel.ConsoleWorkspaceTabKey, main.SelectedWorkspaceTabKey);
                    Assert.Equal(Visibility.Collapsed, diagnostics.Visibility);

                    main.SelectedServer = first;
                    main.IsSplitConsoleVisible = true;
                    Assert.False(main.IsSplitDiagnosticOutputVisible);
                    second.SeparateDiagnosticOutput = true;
                    Assert.True(main.IsSplitDiagnosticOutputVisible);
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
                await main.LastDiagnosticOutputPreferenceSave;
                await main.DisposeAsync();
            }
        }
    }

    private static ServerInstanceViewModel AddServer(
        MainWindowViewModel main,
        string name,
        bool? separateDiagnosticOutput)
    {
        var model = new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = name,
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"diagnostic-{Guid.NewGuid():N}"),
            ServerJarPath = "server.jar",
            SeparateDiagnosticOutput = separateDiagnosticOutput
        };
        var factory = typeof(MainWindowViewModel).GetMethod(
            "CreateServerViewModel",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindowViewModel), "CreateServerViewModel");
        var server = Assert.IsType<ServerInstanceViewModel>(factory.Invoke(
            main,
            [model, false, true]));
        main.Servers.Add(server);
        return server;
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in VisualDescendants<T>(child)) yield return nested;
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            () => frame.Continue = false,
            DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
}
