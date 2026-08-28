using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class BulkServerSelectionVisualTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void MainWindow_DeclaresTheThreeExactBulkControlsAndCollapsibleRowCheckbox()
    {
        var document = XDocument.Load(GetAppSourcePath("MainWindow.xaml"));
        var buttons = document.Descendants(Presentation + "Button").ToArray();

        AssertButtonBinding(
            buttons,
            "{DynamicResource L10n.main.select}",
            "{Binding ToggleBulkSelectionModeCommand}");
        AssertButtonBinding(
            buttons,
            "{DynamicResource L10n.main.startSelected}",
            "{Binding StartCheckedServersCommand}");
        AssertButtonBinding(
            buttons,
            "{DynamicResource L10n.main.stopSelected}",
            "{Binding StopCheckedServersCommand}");

        Assert.DoesNotContain(buttons, button =>
            (string?)button.Attribute("Content") is "啟動選取" or "停止選取");

        var checkbox = Assert.Single(
            document.Descendants(Presentation + "CheckBox"),
            element => (string?)element.Attribute("Tag") == "BulkServerSelection");
        Assert.Equal("0", (string?)checkbox.Attribute("Grid.Column"));
        Assert.Equal(
            "{Binding IsBulkSelected, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            (string?)checkbox.Attribute("IsChecked"));
        Assert.Equal(
            "{Binding DataContext.IsBulkSelectionMode, RelativeSource={RelativeSource AncestorType={x:Type ListBox}}, Converter={StaticResource BoolToVisibility}}",
            (string?)checkbox.Attribute("Visibility"));
        var batchTrigger = Assert.Single(
            checkbox.Descendants(Presentation + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") ==
                       "{Binding DataContext.IsBatchLifecycleOperationRunning, RelativeSource={RelativeSource AncestorType={x:Type ListBox}}}");
        Assert.Equal("True", (string?)batchTrigger.Attribute("Value"));
        Assert.Contains(
            batchTrigger.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "IsEnabled"
                      && (string?)setter.Attribute("Value") == "False");

        var rowGrid = Assert.IsType<XElement>(checkbox.Parent);
        Assert.Equal(Presentation + "Grid", rowGrid.Name);
        var columns = Assert.IsType<XElement>(
                rowGrid.Element(Presentation + "Grid.ColumnDefinitions"))
            .Elements(Presentation + "ColumnDefinition")
            .ToArray();
        Assert.Equal("Auto", (string?)columns[0].Attribute("Width"));

        var icon = Assert.Single(
            rowGrid.Elements(Presentation + "Border"),
            element => (string?)element.Attribute("Grid.Column") == "1"
                       && (string?)element.Attribute("Width") == "30");
        Assert.Equal("1", (string?)icon.Attribute("Grid.Column"));
    }

    [Fact]
    public async Task MainWindow_RowCheckboxOccupiesNoSpaceUntilSelectionModeAndDrivesBothButtons()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? viewModel = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                var paths = new ApplicationPaths(temporary.Path);
                paths.EnsureCreated();
                viewModel = new MainWindowViewModel(paths);
                var server = CreateServer(temporary.Path, "Bulk visual server");
                viewModel.Servers.Add(server);
                viewModel.SelectedServer = server;

                var window = new MainWindow(viewModel)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.WorkArea.Left + 30,
                    Top = SystemParameters.WorkArea.Top + 30,
                    Width = 1280,
                    Height = 720,
                    ShowInTaskbar = false,
                };
                window.Show();
                try
                {
                    DrainDispatcher();
                    window.UpdateLayout();

                    var checkbox = Assert.Single(
                        FindVisualChildren<CheckBox>(window),
                        candidate => Equals(candidate.Tag, "BulkServerSelection"));
                    var rowGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(checkbox));
                    var startButton = Assert.Single(
                        FindVisualChildren<Button>(window),
                        button => AutomationProperties.GetName(button) ==
                                  "啟動所有已勾選的伺服器");
                    var stopButton = Assert.Single(
                        FindVisualChildren<Button>(window),
                        button => AutomationProperties.GetName(button) ==
                                  "關閉所有已勾選的伺服器");

                    Assert.Equal(Visibility.Collapsed, checkbox.Visibility);
                    Assert.Equal(0d, rowGrid.ColumnDefinitions[0].ActualWidth);
                    Assert.False(startButton.IsEnabled);
                    Assert.False(stopButton.IsEnabled);

                    viewModel.ToggleBulkSelectionModeCommand.Execute(null);
                    DrainDispatcher();
                    window.UpdateLayout();

                    Assert.Equal(Visibility.Visible, checkbox.Visibility);
                    Assert.True(rowGrid.ColumnDefinitions[0].ActualWidth > 0);
                    Assert.False(startButton.IsEnabled);
                    Assert.False(stopButton.IsEnabled);

                    checkbox.IsChecked = true;
                    checkbox.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateSource();
                    DrainDispatcher();

                    Assert.True(server.IsBulkSelected);
                    Assert.True(startButton.IsEnabled);
                    Assert.True(stopButton.IsEnabled);

                    typeof(MainWindowViewModel)
                        .GetProperty(nameof(MainWindowViewModel.IsBatchLifecycleOperationRunning))!
                        .GetSetMethod(nonPublic: true)!
                        .Invoke(viewModel, [true]);
                    DrainDispatcher();

                    Assert.False(checkbox.IsEnabled);
                    Assert.False(startButton.IsEnabled);
                    Assert.False(stopButton.IsEnabled);

                    typeof(MainWindowViewModel)
                        .GetProperty(nameof(MainWindowViewModel.IsBatchLifecycleOperationRunning))!
                        .GetSetMethod(nonPublic: true)!
                        .Invoke(viewModel, [false]);
                    DrainDispatcher();

                    Assert.True(checkbox.IsEnabled);
                    Assert.True(startButton.IsEnabled);
                    Assert.True(stopButton.IsEnabled);

                    viewModel.ToggleBulkSelectionModeCommand.Execute(null);
                    DrainDispatcher();
                    window.UpdateLayout();

                    Assert.False(server.IsBulkSelected);
                    Assert.Equal(Visibility.Collapsed, checkbox.Visibility);
                    Assert.Equal(0d, rowGrid.ColumnDefinitions[0].ActualWidth);
                    Assert.False(startButton.IsEnabled);
                    Assert.False(stopButton.IsEnabled);
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
            if (viewModel is not null)
            {
                await viewModel.DisposeAsync();
            }
        }
    }

    private static void AssertButtonBinding(
        IEnumerable<XElement> buttons,
        string content,
        string commandBinding)
    {
        var button = Assert.Single(
            buttons,
            element => (string?)element.Attribute("Content") == content);
        Assert.Equal(commandBinding, (string?)button.Attribute("Command"));
    }

    private static ServerInstanceViewModel CreateServer(string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return new ServerInstanceViewModel(
            new ServerInstance
            {
                Name = name,
                DirectoryPath = directory,
                ServerJarPath = Path.Combine(directory, "server.jar")
            },
            (_, _) => Task.CompletedTask);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
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

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
