using System.IO;
using System.Windows;
using System.Windows.Controls;
using MinecraftServerManager.App.Views;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientMouseWheelRoutingTests
{
    [Fact]
    public void ClientWorkspace_InterceptsPreviewWheelBeforeNestedControlsConsumeIt()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml.cs"));

        Assert.Contains("Mouse.PreviewMouseWheelEvent", source, StringComparison.Ordinal);
        Assert.Contains("new MouseWheelEventHandler(OnPreviewMouseWheel)", source, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", source, StringComparison.Ordinal);
        Assert.Contains("TryRouteMouseWheel(source, e.Delta, this)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WheelTarget_SkipsNestedViewerWithoutOverflowAndUsesPageViewer()
    {
        WpfStaTestHost.Run(() =>
        {
            var leaf = new Border { Height = 48 };
            var nested = new ScrollViewer
            {
                Height = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = leaf
            };
            var page = CreateScrollablePage(nested);

            Layout(page);

            Assert.Equal(0, nested.ScrollableHeight);
            Assert.True(page.ScrollableHeight > 0);
            Assert.Same(
                page,
                ClientWorkspaceView.FindWheelScrollTarget(leaf, delta: -120, page));
        });
    }

    [Fact]
    public void WheelTarget_PrefersNestedViewerUntilItReachesItsEdge()
    {
        WpfStaTestHost.Run(() =>
        {
            var leaf = new Border { Height = 320 };
            var nested = new ScrollViewer
            {
                Height = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = leaf
            };
            var page = CreateScrollablePage(nested);

            Layout(page);

            Assert.True(nested.ScrollableHeight > 0);
            Assert.Same(
                nested,
                ClientWorkspaceView.FindWheelScrollTarget(leaf, delta: -120, page));

            nested.ScrollToVerticalOffset(nested.ScrollableHeight);
            nested.UpdateLayout();

            Assert.Equal(nested.ScrollableHeight, nested.VerticalOffset, precision: 3);
            Assert.Same(
                page,
                ClientWorkspaceView.FindWheelScrollTarget(leaf, delta: -120, page));
        });
    }

    [Fact]
    public void WheelTarget_SkipsViewerWhoseVerticalScrollingIsDisabled()
    {
        WpfStaTestHost.Run(() =>
        {
            var leaf = new Border { Height = 320 };
            var nested = new ScrollViewer
            {
                Height = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = leaf
            };
            var page = CreateScrollablePage(nested);

            Layout(page);

            Assert.Same(
                page,
                ClientWorkspaceView.FindWheelScrollTarget(leaf, delta: -120, page));
        });
    }

    [Fact]
    public void WheelRoute_MovesTheResolvedPageViewer()
    {
        WpfStaTestHost.Run(() =>
        {
            var leaf = new Border { Height = 48 };
            var nested = new ScrollViewer
            {
                Height = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = leaf
            };
            var page = CreateScrollablePage(nested);

            Layout(page);

            var routed = ClientWorkspaceView.TryRouteMouseWheel(leaf, delta: -120, page);
            page.UpdateLayout();

            Assert.Equal(SystemParameters.WheelScrollLines != 0, routed);
            if (routed)
            {
                Assert.True(page.VerticalOffset > 0);
                Assert.Equal(0, nested.VerticalOffset);
            }
        });
    }

    private static ScrollViewer CreateScrollablePage(UIElement nested)
    {
        var content = new StackPanel();
        content.Children.Add(nested);
        content.Children.Add(new Border { Height = 560 });

        return new ScrollViewer
        {
            Width = 320,
            Height = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }
}
