using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.App.Tests;

public sealed class ExistingServerImportChoiceDialogLifecycleTests
{
    [Fact]
    public void Dialog_UsesThemeResourcesAndKeyboardAccessibleActions()
    {
        var sourcePath = GetAppSourcePath(Path.Combine(
            "Dialogs",
            "ExistingServerImportChoiceDialog.xaml"));
        var document = XDocument.Load(sourcePath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xaml = File.ReadAllText(sourcePath);
        var buttons = document.Descendants(presentation + "Button").ToArray();

        Assert.Contains("{DynamicResource WindowBrush}", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource PanelRaisedBrush}", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource AccentBrush}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#", xaml, StringComparison.Ordinal);
        Assert.Contains(buttons, button => (string?)button.Attribute("IsDefault") == "True");
        Assert.Contains(buttons, button => (string?)button.Attribute("IsCancel") == "True");
        var window = Assert.Single(document.Elements(presentation + "Window"));
        Assert.Equal("CanResizeWithGrip", (string?)window.Attribute("ResizeMode"));
        Assert.True(double.Parse((string?)window.Attribute("Width") ?? "0") >= 700);
        Assert.True(double.Parse((string?)window.Attribute("Height") ?? "0") >= 500);
        Assert.True(double.Parse((string?)window.Attribute("MinWidth") ?? "0") >= 640);
        Assert.True(double.Parse((string?)window.Attribute("MinHeight") ?? "0") >= 470);
        Assert.DoesNotContain(
            buttons.Where(button => ((string?)button.Attribute(xamlNamespace + "Name"))?.StartsWith("Import", StringComparison.Ordinal) == true),
            button => button.Attribute("Height") is not null);
        var accessTextResources = document.Descendants(presentation + "AccessText")
            .Select(element => (string?)element.Attribute("Text") ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "{DynamicResource L10n.importChoice.folder}",
                "{DynamicResource L10n.importChoice.jar}",
            ],
            accessTextResources);
        foreach (var culture in ProductLocalizationCatalog.SupportedCultures)
        {
            var strings = ProductLocalizationCatalog.GetDocument(culture).Strings;
            Assert.Contains('_', strings["importChoice.folder"]);
            Assert.Contains('_', strings["importChoice.jar"]);
        }
    }

    [Fact]
    public void MinimumWindowLayout_KeepsBothChoiceCardsCompleteAndColumnAligned()
    {
        WpfStaTestHost.Run(() =>
        {
            var dialog = new ExistingServerImportChoiceDialog
            {
                Width = 640,
                Height = 470,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false
            };
            dialog.Show();

            try
            {
                dialog.UpdateLayout();

                var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
                var folderButton = Assert.IsType<Button>(dialog.FindName("ImportFolderButton"));
                var jarButton = Assert.IsType<Button>(dialog.FindName("ImportJarButton"));
                var folderLayout = Assert.IsType<Grid>(dialog.FindName("FolderChoiceLayout"));
                var jarLayout = Assert.IsType<Grid>(dialog.FindName("JarChoiceLayout"));

                Assert.True(folderButton.ActualHeight >= 112);
                Assert.True(jarButton.ActualHeight >= 112);
                Assert.Equal(folderButton.ActualWidth, jarButton.ActualWidth, precision: 1);
                Assert.Equal(folderLayout.ActualWidth, jarLayout.ActualWidth, precision: 1);
                AssertInside(root, folderButton);
                AssertInside(root, jarButton);

                AssertAligned(
                    root,
                    Assert.IsType<Border>(dialog.FindName("FolderChoiceIcon")),
                    Assert.IsType<Border>(dialog.FindName("JarChoiceIcon")));
                AssertAligned(
                    root,
                    Assert.IsType<StackPanel>(dialog.FindName("FolderChoiceText")),
                    Assert.IsType<StackPanel>(dialog.FindName("JarChoiceText")));
                AssertAligned(
                    root,
                    Assert.IsType<Border>(dialog.FindName("FolderRecommendationSlot")),
                    Assert.IsType<Border>(dialog.FindName("JarRecommendationSlot")));
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void ShowDialog_FirstLayoutAndBothChoices_ReturnExactSelectionWithOwner()
    {
        WpfStaTestHost.Run(() =>
        {
            var owner = new Window
            {
                Width = 320,
                Height = 220,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10_000,
                Top = -10_000
            };
            owner.Show();
            try
            {
                AssertSelection(owner, "ImportFolderButton", ExistingServerImportKind.ServerFolder);
                AssertSelection(owner, "ImportJarButton", ExistingServerImportKind.ServerJar);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [Fact]
    public void ShowDialog_CloseWithoutChoice_ReturnsFalseAndLeavesSelectionEmpty()
    {
        WpfStaTestHost.Run(() =>
        {
            var dialog = new ExistingServerImportChoiceDialog();
            var contentRendered = false;
            dialog.ContentRendered += (_, _) =>
            {
                contentRendered = true;
                dialog.Dispatcher.BeginInvoke(dialog.Close, DispatcherPriority.ApplicationIdle);
            };

            var result = dialog.ShowDialog();

            Assert.True(contentRendered);
            Assert.False(result);
            Assert.Null(dialog.SelectedImportKind);
            Assert.False(dialog.IsVisible);
        });
    }

    private static void AssertSelection(
        Window owner,
        string buttonName,
        ExistingServerImportKind expectedSelection)
    {
        var dialog = new ExistingServerImportChoiceDialog
        {
            Owner = owner
        };
        var contentRendered = false;
        dialog.ContentRendered += (_, _) =>
        {
            contentRendered = true;
            dialog.Dispatcher.BeginInvoke(
                () =>
                {
                    var button = Assert.IsType<Button>(dialog.FindName(buttonName));
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                },
                DispatcherPriority.ApplicationIdle);
        };

        var result = dialog.ShowDialog();

        Assert.True(contentRendered);
        Assert.True(result);
        Assert.Same(owner, dialog.Owner);
        Assert.Equal(expectedSelection, dialog.SelectedImportKind);
        Assert.True(owner.IsEnabled);
        Assert.False(dialog.IsVisible);
    }

    private static void AssertInside(FrameworkElement root, FrameworkElement element)
    {
        var origin = element.TransformToAncestor(root).Transform(new Point());
        const double tolerance = 0.75;

        Assert.InRange(origin.X, -tolerance, root.ActualWidth + tolerance);
        Assert.InRange(origin.Y, -tolerance, root.ActualHeight + tolerance);
        Assert.True(origin.X + element.ActualWidth <= root.ActualWidth + tolerance);
        Assert.True(origin.Y + element.ActualHeight <= root.ActualHeight + tolerance);
    }

    private static void AssertAligned(
        FrameworkElement root,
        FrameworkElement first,
        FrameworkElement second)
    {
        var firstOrigin = first.TransformToAncestor(root).Transform(new Point());
        var secondOrigin = second.TransformToAncestor(root).Transform(new Point());

        Assert.InRange(Math.Abs(firstOrigin.X - secondOrigin.X), 0, 0.75);
        Assert.InRange(Math.Abs(first.ActualWidth - second.ActualWidth), 0, 0.75);
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);
}
