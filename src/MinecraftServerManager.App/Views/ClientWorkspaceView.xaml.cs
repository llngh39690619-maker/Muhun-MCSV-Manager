using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MinecraftServerManager.App.Views;

public partial class ClientWorkspaceView : UserControl
{
    private const int WheelDeltaPerDetent = 120;
    private const double PhysicalLineScrollAmount = 16;
    private const double ScrollEdgeTolerance = 0.5;

    public ClientWorkspaceView()
    {
        InitializeComponent();
        AddHandler(
            Mouse.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0
            || e.OriginalSource is not DependencyObject source
            || !TryRouteMouseWheel(source, e.Delta, this))
        {
            return;
        }

        e.Handled = true;
    }

    internal static bool TryRouteMouseWheel(
        DependencyObject source,
        int delta,
        DependencyObject scope)
    {
        if (delta == 0 || SystemParameters.WheelScrollLines == 0)
        {
            return false;
        }

        var scrollViewer = FindWheelScrollTarget(source, delta, scope);
        if (scrollViewer is null)
        {
            return false;
        }

        var wheelLines = SystemParameters.WheelScrollLines;
        var detents = Math.Max(1L, Math.Abs((long)delta) / WheelDeltaPerDetent);
        if (wheelLines < 0)
        {
            var pageAmount = Math.Max(1, scrollViewer.ViewportHeight) * detents;
            return ScrollBy(scrollViewer, delta, pageAmount);
        }

        var lineAmount = detents * wheelLines;
        var scrollAmount = scrollViewer.CanContentScroll
            ? lineAmount
            : lineAmount * PhysicalLineScrollAmount;
        return ScrollBy(scrollViewer, delta, scrollAmount);
    }

    internal static ScrollViewer? FindWheelScrollTarget(
        DependencyObject? source,
        int delta,
        DependencyObject scope)
    {
        if (delta == 0)
        {
            return null;
        }

        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is ScrollViewer scrollViewer
                && CanScrollVertically(scrollViewer, delta))
            {
                return scrollViewer;
            }

            if (ReferenceEquals(current, scope))
            {
                break;
            }
        }

        return null;
    }

    private static bool CanScrollVertically(ScrollViewer scrollViewer, int delta)
    {
        if (scrollViewer.Visibility != Visibility.Visible
            || !scrollViewer.IsEnabled
            || scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled
            || scrollViewer.ScrollableHeight <= ScrollEdgeTolerance)
        {
            return false;
        }

        return delta > 0
            ? scrollViewer.VerticalOffset > ScrollEdgeTolerance
            : scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - ScrollEdgeTolerance;
    }

    private static bool ScrollBy(ScrollViewer scrollViewer, int delta, double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0)
        {
            return false;
        }

        var targetOffset = delta > 0
            ? Math.Max(0, scrollViewer.VerticalOffset - amount)
            : Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + amount);
        if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) <= ScrollEdgeTolerance)
        {
            return false;
        }

        scrollViewer.ScrollToVerticalOffset(targetOffset);
        return true;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(child);
        }

        if (child is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement)
                   ?? (contentElement as FrameworkContentElement)?.Parent;
        }

        return LogicalTreeHelper.GetParent(child);
    }
}
