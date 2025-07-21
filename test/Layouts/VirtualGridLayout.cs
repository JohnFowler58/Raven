using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace test.layouts;

/// <summary>
/// Defines constants that specify how items are aligned on the horizontal axis.
/// </summary>
public enum UniformGridLayoutItemsJustification
{
    Start,
    Center,
    End,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
}

/// <summary>
/// Defines constants that specify how items are sized to fill the available space.
/// </summary>
public enum UniformGridLayoutItemsStretch
{
    None,
    Fill,
    Uniform,
}

/// <summary>
/// A virtualized layout that arranges items in a uniform grid with equal sized cells.
/// </summary>
public class VirtualGridLayout : VirtualizingLayout
{
    #region Dependency Properties
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(VirtualGridLayout),
        new PropertyMetadata(191.0, OnMeasurePropertyChanged)
    );
    public static readonly DependencyProperty MinItemHeightProperty = DependencyProperty.Register(
        nameof(MinItemHeight),
        typeof(double),
        typeof(VirtualGridLayout),
        new PropertyMetadata(224.0, OnMeasurePropertyChanged)
    );
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(VirtualGridLayout),
        new PropertyMetadata(int.MaxValue, OnMeasurePropertyChanged)
    );
    public static readonly DependencyProperty MinColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(MinColumnSpacing),
            typeof(double),
            typeof(VirtualGridLayout),
            new PropertyMetadata(17.0, OnMeasurePropertyChanged)
        );
    public static readonly DependencyProperty MinRowSpacingProperty = DependencyProperty.Register(
        nameof(MinRowSpacing),
        typeof(double),
        typeof(VirtualGridLayout),
        new PropertyMetadata(32.0, OnMeasurePropertyChanged)
    );

    public static readonly DependencyProperty ItemsJustificationProperty =
        DependencyProperty.Register(
            nameof(ItemsJustification),
            typeof(UniformGridLayoutItemsJustification),
            typeof(VirtualGridLayout),
            new PropertyMetadata(
                UniformGridLayoutItemsJustification.SpaceEvenly,
                OnArrangePropertyChanged
            )
        );
    public static readonly DependencyProperty ItemsStretchProperty = DependencyProperty.Register(
        nameof(ItemsStretch),
        typeof(UniformGridLayoutItemsStretch),
        typeof(VirtualGridLayout),
        new PropertyMetadata(UniformGridLayoutItemsStretch.Fill, OnArrangePropertyChanged)
    );
    #endregion

    #region Public Properties (with XML Documentation)
    /// <summary>
    /// Gets or sets the minimum width of an item. This is used to calculate the number of columns.
    /// </summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the height of an item. This defines the uniform height for all cells in the grid.
    /// </summary>
    public double MinItemHeight
    {
        get => (double)GetValue(MinItemHeightProperty);
        set => SetValue(MinItemHeightProperty, value);
    }

    /// <summary>
    /// Gets or sets the maximum number of columns to display.
    /// </summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum spacing between items in the same row.
    /// </summary>
    public double MinColumnSpacing
    {
        get => (double)GetValue(MinColumnSpacingProperty);
        set => SetValue(MinColumnSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets the spacing between rows.
    /// </summary>
    public double MinRowSpacing
    {
        get => (double)GetValue(MinRowSpacingProperty);
        set => SetValue(MinRowSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets a value that indicates how items are aligned on the horizontal axis.
    /// </summary>
    public UniformGridLayoutItemsJustification ItemsJustification
    {
        get => (UniformGridLayoutItemsJustification)GetValue(ItemsJustificationProperty);
        set => SetValue(ItemsJustificationProperty, value);
    }

    /// <summary>
    /// Gets or sets a value that indicates how items are sized to fill the available space within a cell.
    /// </summary>
    public UniformGridLayoutItemsStretch ItemsStretch
    {
        get => (UniformGridLayoutItemsStretch)GetValue(ItemsStretchProperty);
        set => SetValue(ItemsStretchProperty, value);
    }
    #endregion

    #region Private Fields
    private double _cellWidth;
    private double _cellHeight;
    private int _columns;

    private Size _lastAvailableSize;
    private bool _layoutInvalid = true;
    private int _lastItemCount = -1;
    private int _cachedRowCount;
    private bool _significantSizeChange;
    private int _previousColumns;

    private const double FloatingPointEpsilon = 0.01;
    private const int ExtraBufferItems = 1;
    #endregion

    #region Overridden Methods
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context.ItemCount == 0)
        {
            _lastItemCount = 0;
            return new Size(0, 0);
        }

        bool sizeChanged = !SizeEquals(_lastAvailableSize, availableSize);
        int newColumns = CalculateColumnCount(availableSize.Width);

        _significantSizeChange =
            sizeChanged
            && (
                (_previousColumns != 0 && newColumns > _previousColumns * 1.5)
                || Math.Abs(_lastAvailableSize.Width - availableSize.Width) > 200
            );

        if (sizeChanged || _layoutInvalid)
        {
            _lastAvailableSize = availableSize;
            CalculateLayoutParameters(availableSize);
            _layoutInvalid = false;
            _previousColumns = _columns;
        }

        int rows = RecalculateRowCountIfNeeded(context.ItemCount);
        var realizationRect = context.RealizationRect;
        var visibleRange = GetVisibleRange(realizationRect, rows);

        MeasureVisibleItems(context, visibleRange, context.ItemCount);

        double totalHeight = CalculateTotalHeight(rows);
        return new Size(availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.ItemCount == 0)
            return finalSize;

        if (!SizeEquals(_lastAvailableSize, finalSize) || _layoutInvalid)
        {
            _lastAvailableSize = finalSize;
            CalculateLayoutParameters(finalSize);
            _layoutInvalid = false;
        }

        int rows = RecalculateRowCountIfNeeded(context.ItemCount);
        bool processAll = _significantSizeChange;
        _significantSizeChange = false;

        var realizationRect = context.RealizationRect;
        var visibleRange = GetVisibleRange(realizationRect, rows, processAll);

        for (int rowIndex = visibleRange.StartRow; rowIndex <= visibleRange.EndRow; rowIndex++)
        {
            int baseIndex = rowIndex * _columns;
            int itemsInRow = Math.Min(_columns, context.ItemCount - baseIndex);
            if (itemsInRow > 0)
            {
                ArrangeRow(context, rowIndex, itemsInRow, finalSize.Width);
            }
        }
        return finalSize;
    }

    protected override void OnItemsChangedCore(
        VirtualizingLayoutContext context,
        object source,
        NotifyCollectionChangedEventArgs args
    )
    {
        _lastItemCount = -1;
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Move:
                InvalidateArrange();
                break;
            default:
                InvalidateMeasure();
                break;
        }
        base.OnItemsChangedCore(context, source, args);
    }
    #endregion

    #region Layout Calculation
    private int RecalculateRowCountIfNeeded(int itemCount)
    {
        if (itemCount != _lastItemCount || _significantSizeChange)
        {
            _cachedRowCount = CalculateRowCount(itemCount);
            _lastItemCount = itemCount;
        }
        return _cachedRowCount;
    }

    private void CalculateLayoutParameters(Size availableSize)
    {
        _columns = CalculateColumnCount(availableSize.Width);
        _cellHeight = MinItemHeight;
        double totalSpacing = (_columns > 1) ? (_columns - 1) * MinColumnSpacing : 0;
        _cellWidth = (_columns > 0) ? (availableSize.Width - totalSpacing) / _columns : 0;
    }

    private int CalculateColumnCount(double availableWidth)
    {
        if (double.IsInfinity(availableWidth))
            return MaxColumns;
        if (MinItemWidth + MinColumnSpacing <= 0)
            return 1;

        int calculatedColumns = (int)(
            (availableWidth + MinColumnSpacing) / (MinItemWidth + MinColumnSpacing)
        );
        return Math.Max(1, Math.Min(calculatedColumns, MaxColumns));
    }

    private int CalculateRowCount(int itemCount) =>
        (_columns > 0) ? (int)Math.Ceiling((double)itemCount / _columns) : 0;

    private double CalculateTotalHeight(int rows) =>
        (rows > 0) ? (rows * _cellHeight) + ((rows - 1) * MinRowSpacing) : 0;
    #endregion

    #region Virtualization and Arrangement
    private RowRange GetVisibleRange(Rect realizationRect, int totalRows, bool processAll = false)
    {
        if (processAll || totalRows <= 0 || realizationRect.Height < 1)
        {
            return new RowRange(0, Math.Max(0, totalRows - 1));
        }

        double rowPitch = _cellHeight + MinRowSpacing;
        if (rowPitch <= 0)
            return new RowRange(0, Math.Max(0, totalRows - 1));

        int startRow = Math.Max(0, (int)(realizationRect.Y / rowPitch) - ExtraBufferItems);
        int endRow = Math.Min(
            totalRows - 1,
            (int)(realizationRect.Bottom / rowPitch) + ExtraBufferItems
        );
        return new RowRange(startRow, endRow);
    }

    private void MeasureVisibleItems(
        VirtualizingLayoutContext context,
        RowRange visibleRange,
        int itemCount
    )
    {
        var itemSize = new Size(_cellWidth, _cellHeight);
        for (int rowIndex = visibleRange.StartRow; rowIndex <= visibleRange.EndRow; rowIndex++)
        {
            int baseIndex = rowIndex * _columns;
            int itemsInRow = Math.Min(_columns, itemCount - baseIndex);

            if (itemsInRow <= 0)
                continue;

            for (int colIndex = 0; colIndex < itemsInRow; colIndex++)
            {
                int itemIndex = baseIndex + colIndex;
                if (itemIndex < itemCount)
                {
                    context.GetOrCreateElementAt(itemIndex).Measure(itemSize);
                }
            }
        }
    }

    private void ArrangeRow(
        VirtualizingLayoutContext context,
        int rowIndex,
        int itemsInRow,
        double availableWidth
    )
    {
        double cellPitch = _cellWidth + MinColumnSpacing;
        double totalRowWidth = (itemsInRow * cellPitch) - MinColumnSpacing;
        double freeSpace = Math.Max(0, availableWidth - totalRowWidth);

        (double rowOffset, double interItemSpacing) = CalculateRowMetrics(itemsInRow, freeSpace);
        double yPosition = rowIndex * (_cellHeight + MinRowSpacing);
        int baseIndex = rowIndex * _columns;

        for (int colIndex = 0; colIndex < itemsInRow; colIndex++)
        {
            int itemIndex = baseIndex + colIndex;
            var element = context.GetOrCreateElementAt(itemIndex);

            double cellX = rowOffset + colIndex * (_cellWidth + interItemSpacing);
            var cellRect = new Rect(cellX, yPosition, _cellWidth, _cellHeight);
            Rect finalRect = CalculateItemRect(cellRect);

            element.Arrange(finalRect);
        }
    }

    private (double Offset, double Spacing) CalculateRowMetrics(int itemsInRow, double freeSpace)
    {
        double offset = 0;
        double spacing = MinColumnSpacing;
        switch (ItemsJustification)
        {
            case UniformGridLayoutItemsJustification.Center:
                offset = freeSpace / 2;
                break;
            case UniformGridLayoutItemsJustification.End:
                offset = freeSpace;
                break;
            case UniformGridLayoutItemsJustification.SpaceBetween:
                if (itemsInRow > 1)
                    spacing += freeSpace / (itemsInRow - 1);
                break;
            case UniformGridLayoutItemsJustification.SpaceAround:
                if (itemsInRow > 0)
                {
                    spacing += freeSpace / itemsInRow;
                    offset = (spacing - MinColumnSpacing) / 2;
                }
                break;
            case UniformGridLayoutItemsJustification.SpaceEvenly:
                if (itemsInRow > 0)
                {
                    double evenSpace = freeSpace / (itemsInRow + 1);
                    spacing += evenSpace;
                    offset = evenSpace;
                }
                break;
        }
        return (offset, spacing);
    }

    private Rect CalculateItemRect(Rect cellRect)
    {
        if (ItemsStretch == UniformGridLayoutItemsStretch.Fill)
            return cellRect;

        double itemWidth = MinItemWidth;
        double itemHeight = MinItemHeight;

        if (ItemsStretch == UniformGridLayoutItemsStretch.Uniform)
        {
            if (MinItemHeight > 0)
            {
                double cellAspectRatio = cellRect.Width / cellRect.Height;
                double itemAspectRatio = MinItemWidth / MinItemHeight;
                if (cellAspectRatio > itemAspectRatio)
                {
                    itemHeight = cellRect.Height;
                    itemWidth = itemHeight * itemAspectRatio;
                }
                else
                {
                    itemWidth = cellRect.Width;
                    itemHeight = itemWidth / itemAspectRatio;
                }
            }
        }
        double x = cellRect.X + (cellRect.Width - itemWidth) / 2;
        double y = cellRect.Y + (cellRect.Height - itemHeight) / 2;
        return new Rect(x, y, itemWidth, itemHeight);
    }
    #endregion

    #region Property Changed Handlers
    /// <summary>
    /// Handles changes to properties that affect the measurement of the layout (e.g., cell sizes, row/column counts).
    /// </summary>
    private static void OnMeasurePropertyChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e
    )
    {
        if (d is VirtualGridLayout layout)
        {
            layout._layoutInvalid = true;
            layout._lastItemCount = -1;
            layout._significantSizeChange = true;
            layout.InvalidateMeasure();
        }
    }

    /// <summary>
    /// Handles changes to properties that only affect the arrangement of items within their existing cells.
    /// </summary>
    private static void OnArrangePropertyChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e
    )
    {
        if (d is VirtualGridLayout layout)
        {
            layout.InvalidateArrange();
        }
    }
    #endregion

    #region Helpers
    private static bool SizeEquals(Size s1, Size s2) =>
        Math.Abs(s1.Width - s2.Width) < FloatingPointEpsilon
        && Math.Abs(s1.Height - s2.Height) < FloatingPointEpsilon;

    private readonly struct RowRange
    {
        public int StartRow { get; }
        public int EndRow { get; }

        public RowRange(int start, int end)
        {
            StartRow = start;
            EndRow = end;
        }
    }
    #endregion
}
