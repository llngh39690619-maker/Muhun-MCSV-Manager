using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Controls;

namespace MinecraftServerManager.App.Tests;

public sealed class ResponsiveWrapPanelTests
{
    [Fact]
    public void Arrange_UsesEqualColumnsAndStretchesTheIncompleteLastRow()
    {
        RunSta(() =>
        {
            var panel = CreatePanel(itemCount: 5, minItemWidth: 260d, horizontalSpacing: 10d);

            MeasureAndArrange(panel, width: 920d);

            Assert.Equal(88d, panel.DesiredSize.Height, precision: 6);
            AssertChild(panel, index: 0, x: 0d, y: 0d, width: 300d);
            AssertChild(panel, index: 1, x: 310d, y: 0d, width: 300d);
            AssertChild(panel, index: 2, x: 620d, y: 0d, width: 300d);
            AssertChild(panel, index: 3, x: 0d, y: 48d, width: 455d);
            AssertChild(panel, index: 4, x: 465d, y: 48d, width: 455d);

            var lastChild = panel.Children[4];
            var lastOffset = VisualTreeHelper.GetOffset(lastChild);
            Assert.Equal(920d, lastOffset.X + lastChild.RenderSize.Width, precision: 6);
        });
    }

    [Fact]
    public void Arrange_ReflowsColumnCountWhenTheAvailableWidthChanges()
    {
        RunSta(() =>
        {
            var panel = CreatePanel(itemCount: 4, minItemWidth: 200d, horizontalSpacing: 10d);

            MeasureAndArrange(panel, width: 850d);
            Assert.Equal(40d, panel.DesiredSize.Height, precision: 6);
            AssertChild(panel, index: 3, x: 645d, y: 0d, width: 205d);

            MeasureAndArrange(panel, width: 430d);
            Assert.Equal(88d, panel.DesiredSize.Height, precision: 6);
            AssertChild(panel, index: 0, x: 0d, y: 0d, width: 210d);
            AssertChild(panel, index: 1, x: 220d, y: 0d, width: 210d);
            AssertChild(panel, index: 2, x: 0d, y: 48d, width: 210d);
            AssertChild(panel, index: 3, x: 220d, y: 48d, width: 210d);
        });
    }

    [Fact]
    public void Arrange_UsesOneColumnWhenViewportIsNarrowerThanMinimumItemWidth()
    {
        RunSta(() =>
        {
            var panel = CreatePanel(itemCount: 3, minItemWidth: 200d, horizontalSpacing: 10d);

            MeasureAndArrange(panel, width: 180d);

            Assert.Equal(136d, panel.DesiredSize.Height, precision: 6);
            AssertChild(panel, index: 0, x: 0d, y: 0d, width: 180d);
            AssertChild(panel, index: 1, x: 0d, y: 48d, width: 180d);
            AssertChild(panel, index: 2, x: 0d, y: 96d, width: 180d);
        });
    }

    [Fact]
    public void Arrange_NarrowerThanMeasure_RequestsOneStableRemeasureWithCorrectExtent()
    {
        RunSta(() =>
        {
            var panel = CreateProbePanel(itemCount: 5, minItemWidth: 260d, horizontalSpacing: 10d);

            var initialMeasure = panel.MeasureContent(new Size(920d, double.PositiveInfinity));
            Assert.Equal(88d, initialMeasure.Height, precision: 6);
            panel.ArrangeContent(new Size(430d, initialMeasure.Height));
            DrainDispatcher();

            var correctedMeasure = panel.MeasureContent(new Size(920d, double.PositiveInfinity));
            Assert.Equal(232d, correctedMeasure.Height, precision: 6);
            panel.ArrangeContent(new Size(430d, correctedMeasure.Height));
            DrainDispatcher();

            AssertChild(panel, index: 4, x: 0d, y: 192d, width: 430d);

            var resizedMeasure = panel.MeasureContent(new Size(1400d, double.PositiveInfinity));
            panel.ArrangeContent(new Size(1400d, resizedMeasure.Height));
            Assert.Equal(40d, resizedMeasure.Height, precision: 6);
        });
    }

    [Fact]
    public void ScrollViewer_ResizeMaintainsCompleteResponsiveExtent()
    {
        RunSta(() =>
        {
            var panel = CreatePanel(itemCount: 7, minItemWidth: 260d, horizontalSpacing: 10d);
            var scrollViewer = new ScrollViewer
            {
                Content = panel,
                CanContentScroll = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            MeasureAndArrange(scrollViewer, width: 920d, height: 120d);
            Assert.Equal(136d, panel.DesiredSize.Height, precision: 6);
            Assert.True(scrollViewer.ExtentHeight >= panel.DesiredSize.Height);

            MeasureAndArrange(scrollViewer, width: 430d, height: 120d);
            Assert.Equal(328d, panel.DesiredSize.Height, precision: 6);
            Assert.True(scrollViewer.ExtentHeight >= panel.DesiredSize.Height);
            Assert.True(scrollViewer.ScrollableHeight > 0d);

            MeasureAndArrange(scrollViewer, width: 920d, height: 120d);
            Assert.Equal(136d, panel.DesiredSize.Height, precision: 6);
            Assert.True(scrollViewer.ExtentHeight >= panel.DesiredSize.Height);
            Assert.True(panel.IsMeasureValid);
        });
    }

    [Fact]
    public void ClientCatalogCards_ResizeReflowsWithFixedHeightAndFillsEveryRow()
    {
        RunSta(() =>
        {
            var panel = CreatePanel(itemCount: 6, minItemWidth: 240d, horizontalSpacing: 12d);
            panel.ItemHeight = 324d;
            panel.MaximumColumns = 5;
            foreach (FrameworkElement child in panel.Children)
            {
                child.ClearValue(FrameworkElement.HeightProperty);
            }

            MeasureAndArrange(panel, width: 1000d);
            Assert.Equal(656d, panel.DesiredSize.Height, precision: 6);
            Assert.Equal(241d, panel.Children[0].RenderSize.Width, precision: 6);
            Assert.Equal(494d, panel.Children[5].RenderSize.Width, precision: 6);
            Assert.Equal(324d, panel.Children[5].RenderSize.Height, precision: 6);
            var wideLastOffset = VisualTreeHelper.GetOffset(panel.Children[5]);
            Assert.Equal(1000d, wideLastOffset.X + panel.Children[5].RenderSize.Width, precision: 6);

            MeasureAndArrange(panel, width: 520d);
            Assert.Equal(988d, panel.DesiredSize.Height, precision: 6);
            Assert.Equal(254d, panel.Children[0].RenderSize.Width, precision: 6);
            Assert.Equal(254d, panel.Children[5].RenderSize.Width, precision: 6);
            var narrowLastOffset = VisualTreeHelper.GetOffset(panel.Children[5]);
            Assert.Equal(520d, narrowLastOffset.X + panel.Children[5].RenderSize.Width, precision: 6);
        });
    }

    private static ResponsiveWrapPanel CreatePanel(
        int itemCount,
        double minItemWidth,
        double horizontalSpacing)
    {
        var panel = new ResponsiveWrapPanel
        {
            MinItemWidth = minItemWidth,
            HorizontalSpacing = horizontalSpacing,
            VerticalSpacing = 8d
        };

        for (var index = 0; index < itemCount; index++)
        {
            panel.Children.Add(new Border { Height = 40d });
        }

        return panel;
    }

    private static ProbeResponsiveWrapPanel CreateProbePanel(
        int itemCount,
        double minItemWidth,
        double horizontalSpacing)
    {
        var panel = new ProbeResponsiveWrapPanel
        {
            MinItemWidth = minItemWidth,
            HorizontalSpacing = horizontalSpacing,
            VerticalSpacing = 8d
        };

        for (var index = 0; index < itemCount; index++)
        {
            panel.Children.Add(new Border { Height = 40d });
        }

        return panel;
    }

    private static void MeasureAndArrange(ResponsiveWrapPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0d, 0d, width, panel.DesiredSize.Height));
        panel.UpdateLayout();
    }

    private static void MeasureAndArrange(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0d, 0d, width, height));
        element.UpdateLayout();
    }

    private static void AssertChild(
        ResponsiveWrapPanel panel,
        int index,
        double x,
        double y,
        double width)
    {
        var child = panel.Children[index];
        var offset = VisualTreeHelper.GetOffset(child);
        Assert.Equal(x, offset.X, precision: 6);
        Assert.Equal(y, offset.Y, precision: 6);
        Assert.Equal(width, child.RenderSize.Width, precision: 6);
        Assert.Equal(40d, child.RenderSize.Height, precision: 6);
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static void DrainDispatcher()
        => Dispatcher.CurrentDispatcher.Invoke(
            static () => { },
            DispatcherPriority.ContextIdle);

    private sealed class ProbeResponsiveWrapPanel : ResponsiveWrapPanel
    {
        public Size MeasureContent(Size availableSize)
            => MeasureOverride(availableSize);

        public Size ArrangeContent(Size finalSize)
            => ArrangeOverride(finalSize);
    }
}
