using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MinecraftServerManager.App.Dialogs;

namespace MinecraftServerManager.App.Tests;

public sealed class DarkMessageDialogContractTests
{
    [Fact]
    public void ApplicationCode_UsesNativeMessageBoxOnlyInsideEmergencyFallback()
    {
        var sourceRoot = GetAppSourcePath(string.Empty);
        var nativeCall = new Regex(
            @"(?<![A-Za-z0-9_])MessageBox\s*\.\s*Show\s*\(",
            RegexOptions.CultureInvariant);
        var hits = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new { Path = path, Line = line, Number = index + 1 })
                .Where(candidate => nativeCall.IsMatch(candidate.Line)))
            .ToArray();

        var hit = Assert.Single(hits);
        Assert.Equal("DarkMessageDialog.xaml.cs", Path.GetFileName(hit.Path));
        var wrapper = File.ReadAllText(hit.Path);
        Assert.Contains("ShowStartupPrompt", wrapper, StringComparison.Ordinal);
        Assert.Contains("Emergency startup fallback only", wrapper, StringComparison.Ordinal);
        Assert.Contains("Normal application code must never call the native API directly", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogXaml_UsesApplicationWindowStyleAndDarkRootSurface()
    {
        var document = XDocument.Load(GetAppSourcePath(Path.Combine(
            "Dialogs",
            "DarkMessageDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var window = Assert.Single(document.Elements(presentation + "Window"));
        Assert.Equal("{StaticResource AppWindowStyle}", (string?)window.Attribute("Style"));
        Assert.Equal("{DynamicResource WindowBrush}", (string?)window.Attribute("Background"));
        Assert.Equal("False", (string?)window.Attribute("ShowInTaskbar"));

        var root = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "DialogRoot");
        Assert.Equal("{DynamicResource WindowBrush}", (string?)root.Attribute("Background"));

        var buttons = document.Descendants(presentation + "Button")
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .ToArray();
        Assert.Contains("PrimaryButton", buttons);
        Assert.Contains("SecondaryButton", buttons);
    }

    [Theory]
    [InlineData(MessageBoxButton.OK, MessageBoxResult.None, "確定", MessageBoxResult.OK, null, MessageBoxResult.None, MessageBoxResult.OK, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, MessageBoxResult.Cancel, "確定", MessageBoxResult.OK, "取消", MessageBoxResult.Cancel, MessageBoxResult.Cancel, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.YesNo, MessageBoxResult.No, "是", MessageBoxResult.Yes, "否", MessageBoxResult.No, MessageBoxResult.No, MessageBoxResult.No)]
    [InlineData(MessageBoxButton.YesNo, MessageBoxResult.None, "是", MessageBoxResult.Yes, "否", MessageBoxResult.No, MessageBoxResult.Yes, MessageBoxResult.No)]
    public void ButtonLayout_PreservesSupportedResultsAndExplicitDefault(
        MessageBoxButton buttons,
        MessageBoxResult requestedDefault,
        string primaryLabel,
        MessageBoxResult primaryResult,
        string? secondaryLabel,
        MessageBoxResult secondaryResult,
        MessageBoxResult expectedDefault,
        MessageBoxResult expectedClose)
    {
        var layout = DarkMessageBox.CreateButtonLayout(buttons, requestedDefault);

        Assert.Equal(primaryLabel, layout.PrimaryLabel);
        Assert.Equal(primaryResult, layout.PrimaryResult);
        Assert.Equal(secondaryLabel, layout.SecondaryLabel);
        Assert.Equal(secondaryResult, layout.SecondaryResult);
        Assert.Equal(expectedDefault, layout.DefaultResult);
        Assert.Equal(expectedClose, layout.CloseResult);
    }

    [Fact]
    public void ButtonLayout_RejectsDefaultThatIsNotPresent()
    {
        Assert.Throws<ArgumentException>(() => DarkMessageBox.CreateButtonLayout(
            MessageBoxButton.OKCancel,
            MessageBoxResult.No));
    }

    [Fact]
    public void YesNoDialog_RendersDarkAndReturnsTheSelectedNoResult()
    {
        WpfStaTestHost.Run(() =>
        {
            var owner = new Window
            {
                Width = 500,
                Height = 320,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Background = Brushes.Black
            };
            owner.Show();
            try
            {
                var layout = DarkMessageBox.CreateButtonLayout(
                    MessageBoxButton.YesNo,
                    MessageBoxResult.No);
                var dialog = new DarkMessageDialog(
                    "確定要繼續嗎？",
                    "確認操作",
                    MessageBoxImage.Warning,
                    layout)
                {
                    Owner = owner
                };
                dialog.ContentRendered += (_, _) => dialog.Dispatcher.BeginInvoke(
                    () =>
                    {
                        var root = Assert.IsType<Grid>(dialog.FindName("DialogRoot"));
                        var background = Assert.IsType<SolidColorBrush>(root.Background);
                        Assert.False(background.Color.R > 240
                                     && background.Color.G > 240
                                     && background.Color.B > 240);
                        var noButton = Assert.IsType<Button>(dialog.FindName("SecondaryButton"));
                        Assert.True(noButton.IsDefault);
                        noButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    },
                    DispatcherPriority.ContextIdle);

                var modalResult = dialog.ShowDialog();

                Assert.True(modalResult);
                Assert.Equal(MessageBoxResult.No, dialog.Result);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    private static bool HasPathSegment(string path, string segment)
        => Path.GetFullPath(path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
