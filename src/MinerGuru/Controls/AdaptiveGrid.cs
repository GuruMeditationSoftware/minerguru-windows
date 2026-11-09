using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MinerGuru.Controls;

/// <summary>
/// Adaptive grid with equal-height rows and full-width cells, like the Mac app's AdaptiveGrid:
/// as many columns of at least <see cref="MinColumnWidth"/> as fit, cards in one row always line up.
/// </summary>
public sealed partial class AdaptiveGrid : Panel
{
    public double MinColumnWidth { get; set; } = 340;
    public double Spacing { get; set; } = 16;

    private int Columns(double width)
    {
        if (double.IsInfinity(width)) return 1;
        var fitting = Math.Max(1, (int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing)));
        return Math.Min(fitting, Math.Max(1, Children.Count));
    }

    private double ColumnWidth(double width, int columns) =>
        double.IsInfinity(width) ? MinColumnWidth : (width - (columns - 1) * Spacing) / columns;

    private List<double> RowHeights(int columns)
    {
        var heights = new List<double>();
        for (var start = 0; start < Children.Count; start += columns)
            heights.Add(Children.Skip(start).Take(columns).Max(c => c.DesiredSize.Height));
        return heights;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var columnWidth = ColumnWidth(availableSize.Width, columns);
        foreach (var child in Children) child.Measure(new Size(columnWidth, double.PositiveInfinity));
        var heights = RowHeights(columns);
        var height = heights.Sum() + Math.Max(0, heights.Count - 1) * Spacing;
        return new Size(double.IsInfinity(availableSize.Width) ? columnWidth : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var columnWidth = ColumnWidth(finalSize.Width, columns);
        var heights = RowHeights(columns);
        var y = 0.0;
        for (var row = 0; row < heights.Count; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= Children.Count) break;
                Children[index].Arrange(new Rect(column * (columnWidth + Spacing), y, columnWidth, heights[row]));
            }
            y += heights[row] + Spacing;
        }
        return finalSize;
    }
}
