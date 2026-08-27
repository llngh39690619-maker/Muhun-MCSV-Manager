using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;

namespace MinecraftServerManager.App.Tests;

public sealed class ServerRemovalVisualTests
{
    [Fact]
    public void OpenContextMenu_UsesOnlyThemedSurfacesWithoutAWhiteIconGutter()
    {
        WpfStaTestHost.Run(() =>
        {
            var target = new Border { Width = 140, Height = 36 };
            var owner = new Window
            {
                Content = target,
                Width = 240,
                Height = 140,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            var contextMenu = new ContextMenu
            {
                Style = Assert.IsType<Style>(Application.Current.FindResource(
                    "ThemedContextMenuStyle")),
                Placement = PlacementMode.Bottom,
                PlacementTarget = target
            };
            var item = new MenuItem
            {
                Header = "從管理清單移除",
                Style = Assert.IsType<Style>(Application.Current.FindResource(
                    "ThemedContextMenuItemStyle"))
            };
            var deleteItem = new MenuItem
            {
                Header = "完全刪除 Server",
                Style = Assert.IsType<Style>(Application.Current.FindResource(
                    "ThemedContextMenuItemStyle")),
                Foreground = Assert.IsAssignableFrom<Brush>(Application.Current.FindResource(
                    "DangerBrush"))
            };
            contextMenu.Items.Add(item);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(deleteItem);

            owner.Show();
            try
            {
                contextMenu.IsOpen = true;
                DrainDispatcher();
                contextMenu.ApplyTemplate();
                item.ApplyTemplate();
                deleteItem.ApplyTemplate();

                var contextSurface = Assert.IsType<Border>(
                    contextMenu.Template.FindName("ContextMenuSurface", contextMenu));
                var itemSurface = Assert.IsType<Border>(
                    item.Template.FindName("MenuItemBorder", item));
                var deleteItemSurface = Assert.IsType<Border>(
                    deleteItem.Template.FindName("MenuItemBorder", deleteItem));
                Assert.IsType<ContentPresenter>(itemSurface.Child);
                Assert.IsType<ContentPresenter>(deleteItemSurface.Child);

                Assert.False(IsWhite(contextMenu.Background));
                Assert.False(IsWhite(contextSurface.Background));
                Assert.False(IsWhite(itemSurface.Background));
                Assert.False(IsWhite(deleteItemSurface.Background));
                Assert.DoesNotContain(
                    Descendants(contextMenu)
                        .Concat(Descendants(item))
                        .Concat(Descendants(deleteItem)),
                    HasWhiteBackground);
                Assert.Empty(Descendants(item).OfType<Image>());
                Assert.Empty(Descendants(deleteItem).OfType<Image>());
            }
            finally
            {
                contextMenu.IsOpen = false;
                owner.Close();
            }
        });
    }

    [Fact]
    public void ShowDialog_ConfirmKeepsWindowAndRootDarkThroughClosing()
    {
        WpfStaTestHost.Run(() =>
        {
            var owner = new Window
            {
                Width = 320,
                Height = 220,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            owner.Show();
            try
            {
                var dialog = new RemoveServerConfirmationDialog(
                    "Paper-1.21.2",
                    @"C:\servers\Paper-1.21.2")
                {
                    Owner = owner
                };
                var closingObserved = false;
                dialog.Closing += (_, _) =>
                {
                    closingObserved = true;
                    var root = Assert.IsType<Grid>(dialog.FindName("DialogRoot"));
                    Assert.False(IsWhite(dialog.Background));
                    Assert.False(IsWhite(root.Background));

                    var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(dialog));
                    Assert.NotEqual(Colors.White, source.CompositionTarget.BackgroundColor);
                    AssertDarkRender(root);
                };
                dialog.ContentRendered += (_, _) => dialog.Dispatcher.BeginInvoke(
                    () =>
                    {
                        var button = Assert.IsType<Button>(dialog.FindName("ConfirmButton"));
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    },
                    DispatcherPriority.ApplicationIdle);

                var result = dialog.ShowDialog();

                Assert.True(result);
                Assert.True(closingObserved);
                Assert.False(dialog.IsVisible);
                Assert.True(owner.IsEnabled);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [Fact]
    public void ShowPermanentDeletionDialog_ConfirmKeepsWindowAndRootDarkThroughClosing()
    {
        WpfStaTestHost.Run(() =>
        {
            var owner = new Window
            {
                Width = 320,
                Height = 220,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            owner.Show();
            try
            {
                var dialog = new DeleteServerConfirmationDialog(
                    "Paper-1.21.2",
                    @"C:\servers\Paper-1.21.2")
                {
                    Owner = owner
                };
                var closingObserved = false;
                dialog.Closing += (_, _) =>
                {
                    closingObserved = true;
                    var root = Assert.IsType<Grid>(dialog.FindName("DialogRoot"));
                    Assert.False(IsWhite(dialog.Background));
                    Assert.False(IsWhite(root.Background));

                    var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(dialog));
                    Assert.NotEqual(Colors.White, source.CompositionTarget.BackgroundColor);
                    AssertDarkRender(root);
                };
                dialog.ContentRendered += (_, _) => dialog.Dispatcher.BeginInvoke(
                    () =>
                    {
                        var button = Assert.IsType<Button>(dialog.FindName("ConfirmButton"));
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    },
                    DispatcherPriority.ApplicationIdle);

                var result = dialog.ShowDialog();

                Assert.True(result);
                Assert.True(closingObserved);
                Assert.False(dialog.IsVisible);
                Assert.True(owner.IsEnabled);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool HasWhiteBackground(DependencyObject element)
        => element switch
        {
            Border border => IsWhite(border.Background),
            Panel panel => IsWhite(panel.Background),
            Control control => IsWhite(control.Background),
            _ => false
        };

    private static bool IsWhite(Brush? brush)
        => brush is SolidColorBrush solid
            && solid.Color.A > 0
            && solid.Color.R >= 245
            && solid.Color.G >= 245
            && solid.Color.B >= 245;

    private static void AssertDarkRender(FrameworkElement root)
    {
        root.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var whitePixels = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] > 0
                && pixels[index] >= 245
                && pixels[index + 1] >= 245
                && pixels[index + 2] >= 245)
            {
                whitePixels++;
            }
        }

        Assert.True(
            whitePixels < width * height / 4,
            $"Closing render unexpectedly contained {whitePixels} white pixels out of {width * height}.");
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
