using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MinecraftServerManager.App.Controls;

/// <summary>
/// Arranges items in as many columns as the available width can support. Items in each row share
/// the row width equally; an incomplete final row is stretched across the full available width.
/// </summary>
public class ResponsiveWrapPanel : Panel
{
    private const double LayoutWidthTolerance = 0.5d;

    private double _arrangedWidthHint = double.NaN;
    private double _lastMeasureConstraintWidth = double.NaN;
    private double _measuredLayoutWidth = double.NaN;
    private bool _measureInvalidationQueued;

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(
            260d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsPositiveFiniteDouble);

    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(
            12d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsNonNegativeFiniteDouble);

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing),
        typeof(double),
        typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(
            12d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsNonNegativeFiniteDouble);

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight),
        typeof(double),
        typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(
            double.NaN,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsAutoOrPositiveFiniteDouble);

    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns),
        typeof(int),
        typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(
            int.MaxValue,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        static value => value is int columns && columns > 0);

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public int MaximumColumns
    {
        get => (int)GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateMeasureConstraint(availableSize.Width);
        var children = GetVisibleChildren();
        if (children.Count == 0)
        {
            _measuredLayoutWidth = IsFinite(availableSize.Width)
                ? Math.Max(0d, availableSize.Width)
                : 0d;
            return new Size(IsFinite(availableSize.Width) ? availableSize.Width : 0d, 0d);
        }

        var layoutWidth = ResolveMeasureWidth(availableSize.Width, children.Count);
        if (IsFinite(_arrangedWidthHint) && _arrangedWidthHint < layoutWidth)
        {
            layoutWidth = _arrangedWidthHint;
        }

        _measuredLayoutWidth = layoutWidth;
        var columnCount = GetColumnCount(layoutWidth, children.Count);
        var totalHeight = 0d;

        for (var rowStart = 0; rowStart < children.Count; rowStart += columnCount)
        {
            var rowCount = Math.Min(columnCount, children.Count - rowStart);
            var itemWidth = GetItemWidth(layoutWidth, rowCount);
            var rowHeight = 0d;

            for (var index = 0; index < rowCount; index++)
            {
                var child = children[rowStart + index];
                child.Measure(new Size(
                    itemWidth,
                    double.IsNaN(ItemHeight) ? double.PositiveInfinity : ItemHeight));
                rowHeight = double.IsNaN(ItemHeight)
                    ? Math.Max(rowHeight, child.DesiredSize.Height)
                    : ItemHeight;
            }

            if (rowStart > 0)
            {
                totalHeight += VerticalSpacing;
            }

            totalHeight += rowHeight;
        }

        return new Size(layoutWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = GetVisibleChildren();
        if (children.Count == 0)
        {
            return finalSize;
        }

        var layoutWidth = IsFinite(finalSize.Width) ? Math.Max(0d, finalSize.Width) : 0d;
        var columnCount = GetColumnCount(layoutWidth, children.Count);
        var y = 0d;

        for (var rowStart = 0; rowStart < children.Count; rowStart += columnCount)
        {
            var rowCount = Math.Min(columnCount, children.Count - rowStart);
            var itemWidth = GetItemWidth(layoutWidth, rowCount);
            var rowHeight = 0d;

            for (var index = 0; index < rowCount; index++)
            {
                rowHeight = double.IsNaN(ItemHeight)
                    ? Math.Max(rowHeight, children[rowStart + index].DesiredSize.Height)
                    : ItemHeight;
            }

            var x = 0d;
            for (var index = 0; index < rowCount; index++)
            {
                children[rowStart + index].Arrange(new Rect(x, y, itemWidth, rowHeight));
                x += itemWidth + HorizontalSpacing;
            }

            y += rowHeight + VerticalSpacing;
        }

        QueueRemeasureForNarrowerArrange(layoutWidth);

        return finalSize;
    }

    private IReadOnlyList<UIElement> GetVisibleChildren()
        => InternalChildren
            .Cast<UIElement>()
            .Where(child => child.Visibility != Visibility.Collapsed)
            .ToArray();

    private double ResolveMeasureWidth(double availableWidth, int childCount)
    {
        if (IsFinite(availableWidth))
        {
            return Math.Max(0d, availableWidth);
        }

        return childCount * MinItemWidth + Math.Max(0, childCount - 1) * HorizontalSpacing;
    }

    private void UpdateMeasureConstraint(double availableWidth)
    {
        if (!IsFinite(availableWidth))
        {
            if (IsFinite(_lastMeasureConstraintWidth))
            {
                _arrangedWidthHint = double.NaN;
            }

            _lastMeasureConstraintWidth = double.NaN;
            return;
        }

        var normalizedWidth = Math.Max(0d, availableWidth);
        if (IsFinite(_lastMeasureConstraintWidth)
            && Math.Abs(_lastMeasureConstraintWidth - normalizedWidth) > LayoutWidthTolerance)
        {
            // A changed measure constraint represents a real host resize, so stale scrollbar
            // feedback must not keep the panel at the old, narrower width.
            _arrangedWidthHint = double.NaN;
        }

        _lastMeasureConstraintWidth = normalizedWidth;
    }

    private void QueueRemeasureForNarrowerArrange(double arrangedWidth)
    {
        // Some WPF hosts first measure content before reserving a vertical scrollbar, then arrange
        // it into the narrower viewport. A narrower width can add rows and increase the scroll
        // extent. Feed that width into exactly one subsequent measure pass. InvalidateMeasure called
        // directly from ArrangeOverride is consumed by the active arrange pass, so defer it until
        // that pass has completed. A wider arrange never queues work, preventing layout oscillation.
        if (arrangedWidth + LayoutWidthTolerance >= _measuredLayoutWidth
            || (IsFinite(_arrangedWidthHint)
                && Math.Abs(_arrangedWidthHint - arrangedWidth) <= LayoutWidthTolerance))
        {
            return;
        }

        _arrangedWidthHint = arrangedWidth;
        if (_measureInvalidationQueued)
        {
            return;
        }

        _measureInvalidationQueued = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _measureInvalidationQueued = false;
                InvalidateMeasure();
            }));
    }

    private int GetColumnCount(double availableWidth, int childCount)
    {
        if (childCount <= 1 || availableWidth <= MinItemWidth)
        {
            return 1;
        }

        var columns = (int)Math.Floor((availableWidth + HorizontalSpacing) / (MinItemWidth + HorizontalSpacing));
        return Math.Clamp(columns, 1, Math.Min(childCount, MaximumColumns));
    }

    private double GetItemWidth(double availableWidth, int rowCount)
    {
        var totalSpacing = Math.Max(0, rowCount - 1) * HorizontalSpacing;
        return Math.Max(0d, (availableWidth - totalSpacing) / rowCount);
    }

    private static bool IsPositiveFiniteDouble(object value)
        => value is double number && IsFinite(number) && number > 0d;

    private static bool IsNonNegativeFiniteDouble(object value)
        => value is double number && IsFinite(number) && number >= 0d;

    private static bool IsAutoOrPositiveFiniteDouble(object value)
        => value is double number && (double.IsNaN(number) || IsFinite(number) && number > 0d);

    private static bool IsFinite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);
}
