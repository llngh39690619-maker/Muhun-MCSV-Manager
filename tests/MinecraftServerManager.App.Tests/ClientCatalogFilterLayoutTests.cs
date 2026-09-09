using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MinecraftServerManager.App.Controls;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Views;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientCatalogFilterLayoutTests
{
    [Fact]
    public void CatalogAndContentFilters_StayFixedWidthAndRightAlignedWithVisibleArrowsAfterResizing()
    {
        WpfStaTestHost.Run(() =>
        {
            var catalog = new ClientWorkspaceView();
            var content = new ClientContentDownloadCenterWindow();
            var toolbars = new[]
            {
                (ResponsiveToolbarPanel)catalog.FindName("CatalogSourceAndFilters"),
                (ResponsiveToolbarPanel)content.FindName("ContentDownloadSourceAndFilters")
            };

            foreach (var toolbar in toolbars)
            {
                ((Panel)toolbar.Parent).Children.Remove(toolbar);
                toolbar.Margin = default;
                toolbar.DataContext = new { ShowsCatalogSortFilter = true, IsModContentDownload = true };
                var filters = toolbar.Children.Cast<FrameworkElement>().Skip(1).ToArray();
                var selectors = filters.SelectMany(FindVisuals<ComboBox>).ToArray();
                // A StackPanel has no visual children until its first measure.
                if (selectors.Length == 0)
                {
                    selectors = filters.SelectMany(filter => filter is ComboBox combo
                        ? new[] { combo }
                        : ((Panel)filter).Children.OfType<ComboBox>()).ToArray();
                }

                foreach (var selector in selectors)
                {
                    selector.ItemsSource = new[] { new Choice(new string('W', 100)) };
                    selector.SelectedIndex = 0;
                }

                // A toolbar removed from an initially collapsed page has suspended layout.
                // Reattach it to a real presentation source on the isolated test desktop,
                // so WPF resumes the entire subtree as it does when the catalog is opened.
                toolbar.Width = 1400;
                var host = new Window
                {
                    Content = toolbar,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStyle = WindowStyle.None,
                    ShowActivated = false,
                    ShowInTaskbar = false
                };
                try
                {
                    host.Show();
                    foreach (var width in new[] { 1400d, 770d, 460d, 770d, 1400d })
                    {
                        toolbar.Width = width;
                        toolbar.Measure(new Size(width, double.PositiveInfinity));
                        toolbar.Arrange(new Rect(0, 0, width, toolbar.DesiredSize.Height));
                        toolbar.UpdateLayout();
                        foreach (var filter in filters)
                        {
                            Assert.Equal(filter.Width, filter.ActualWidth, precision: 4);
                            var bounds = BoundsWithin(filter, toolbar);
                            Assert.InRange(bounds.Left, 0, width);
                            Assert.InRange(bounds.Right, 0, width + 0.1);
                            Assert.InRange(bounds.Bottom, 0, toolbar.ActualHeight + 0.1);
                        }

                        foreach (var row in filters.GroupBy(filter => BoundsWithin(filter, toolbar).Top))
                        {
                            Assert.Equal(width, row.Max(filter => BoundsWithin(filter, toolbar).Right), precision: 4);
                        }

                        foreach (var selector in selectors)
                        {
                            var toggle = (ToggleButton)selector.Template.FindName("DropDownToggle", selector);
                            var arrow = Assert.Single(FindVisuals<System.Windows.Shapes.Path>(toggle));
                            var arrowBounds = BoundsWithin(arrow, selector);
                            Assert.InRange(arrowBounds.Left, selector.ActualWidth - 30, selector.ActualWidth);
                            Assert.InRange(arrowBounds.Right, 0, selector.ActualWidth);
                            var selectedContent = (ContentPresenter)selector.Template.FindName("SelectionContentPresenter", selector);
                            Assert.True(BoundsWithin(selectedContent, selector).Right <= selector.ActualWidth - 30);
                            Assert.All(FindVisuals<TextBlock>(selectedContent), text =>
                                Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming));
                        }
                    }
                }
                finally
                {
                    host.Close();
                }
            }
        });
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisuals<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private static Rect BoundsWithin(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private sealed record Choice(string Name)
    {
        public string DisplayName => Name;
    }
}
