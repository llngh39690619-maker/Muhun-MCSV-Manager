using System.Windows;
using System.Windows.Controls;

namespace MinecraftServerManager.App.Controls;

/// <summary>
/// Keeps the leading source selector at the left and fixed-size filters at the right.
/// When they no longer fit together, filters move below the source and wrap without stretching.
/// </summary>
public sealed class ResponsiveToolbarPanel : Panel
{
    private const double HorizontalSpacing = 7d;
    private const double VerticalSpacing = 9d;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        return Layout(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double availableWidth, bool arrange)
    {
        var children = InternalChildren.Cast<UIElement>()
            .Where(child => child.Visibility != Visibility.Collapsed)
            .ToArray();
        if (children.Length == 0)
        {
            return default;
        }

        var naturalWidth = children.Sum(child => child.DesiredSize.Width)
                           + HorizontalSpacing * (children.Length - 1);
        var width = double.IsFinite(availableWidth) ? availableWidth : naturalWidth;
        if (naturalWidth <= width)
        {
            var height = children.Max(child => child.DesiredSize.Height);
            if (arrange)
            {
                children[0].Arrange(new Rect(0, 0, children[0].DesiredSize.Width, height));
                var x = width - (naturalWidth - children[0].DesiredSize.Width - HorizontalSpacing);
                for (var index = 1; index < children.Length; index++)
                {
                    var child = children[index];
                    child.Arrange(new Rect(x, 0, child.DesiredSize.Width, height));
                    x += child.DesiredSize.Width + HorizontalSpacing;
                }
            }

            return new Size(width, height);
        }

        var y = children[0].DesiredSize.Height;
        if (arrange)
        {
            children[0].Arrange(new Rect(0, 0, children[0].DesiredSize.Width, y));
        }

        for (var rowStart = 1; rowStart < children.Length;)
        {
            var rowEnd = rowStart;
            var rowWidth = 0d;
            var rowHeight = 0d;
            while (rowEnd < children.Length)
            {
                var child = children[rowEnd];
                var candidateWidth = rowWidth + (rowEnd > rowStart ? HorizontalSpacing : 0)
                                     + child.DesiredSize.Width;
                if (rowEnd > rowStart && candidateWidth > width)
                {
                    break;
                }

                rowWidth = candidateWidth;
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                rowEnd++;
            }

            y += VerticalSpacing;
            if (arrange)
            {
                var x = Math.Max(0, width - rowWidth);
                for (var index = rowStart; index < rowEnd; index++)
                {
                    var child = children[index];
                    child.Arrange(new Rect(x, y, child.DesiredSize.Width, rowHeight));
                    x += child.DesiredSize.Width + HorizontalSpacing;
                }
            }

            y += rowHeight;
            rowStart = rowEnd;
        }

        return new Size(width, y);
    }
}
