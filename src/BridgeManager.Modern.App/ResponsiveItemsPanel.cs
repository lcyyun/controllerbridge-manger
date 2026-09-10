using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BridgeManager.Modern;

// The small option lists are not virtualized. Row height follows content instead
// of clipping text into fixed ItemsWrapGrid cells inside an unbounded scroller.
public sealed class ResponsiveItemsPanel : Panel
{
    public double ItemMinWidth { get; set; } = 290;
    private double _rowHeight;
    private int _columns = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : ItemMinWidth;
        _columns = Math.Max(1, Math.Min(Math.Max(1, Children.Count),
            (int)(width / ItemMinWidth)));
        var cellWidth = Math.Max(0, width / _columns);
        _rowHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }
        return new Size(width, Math.Ceiling((double)Children.Count / _columns) * _rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cellWidth = finalSize.Width / _columns;
        for (var i = 0; i < Children.Count; i++)
            Children[i].Arrange(new Rect((i % _columns) * cellWidth,
                (i / _columns) * _rowHeight, cellWidth, _rowHeight));
        return finalSize;
    }
}
