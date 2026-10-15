using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickLaunch.Views;

// Keep WrapPanel's horizontal orientation for Gong's insertion adorner. The layout,
// keyboard navigation and drop builder all consume this same geometry.
public sealed class TilePanel : WrapPanel
{
    static TilePanel()
    {
        // ItemsPanel bindings can resolve after its first measure during Show().
        ItemWidthProperty.OverrideMetadata(typeof(TilePanel), new FrameworkPropertyMetadata(84d, FrameworkPropertyMetadataOptions.AffectsMeasure));
        ItemHeightProperty.OverrideMetadata(typeof(TilePanel), new FrameworkPropertyMetadata(84d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    }
    internal TileGeometry Geometry => TileGeometry.Create(ActualWidth, ItemWidth, ItemHeight);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : ItemWidth * Math.Max(1, InternalChildren.Count);
        var geometry = TileGeometry.Create(width, ItemWidth, ItemHeight);
        foreach (UIElement child in InternalChildren) child.Measure(new(geometry.ColumnWidth, ItemHeight));
        return new(width, Math.Ceiling(InternalChildren.Count / (double)geometry.Columns) * ItemHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var geometry = TileGeometry.Create(finalSize.Width, ItemWidth, ItemHeight);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            double width = Math.Min(geometry.ColumnWidth, child.DesiredSize.Width);
            double x = (i % geometry.Columns + .5) * geometry.ColumnWidth - width / 2;
            // Round the tile origin once, rather than rounding both a stretched slot
            // and the fixed-width tile centered inside it at fractional monitor scales.
            if (UseLayoutRounding) x = Math.Round(x * scale) / scale;
            child.Arrange(new Rect(x, i / geometry.Columns * ItemHeight, width, ItemHeight));
        }
        return finalSize;
    }
}

internal readonly record struct TileGeometry(int Columns, double ColumnWidth, double RowHeight)
{
    internal static TileGeometry Create(double width, double minimumWidth, double rowHeight)
    {
        int columns = Math.Max(1, (int)Math.Floor(width / minimumWidth));
        return new(columns, Math.Max(0, width) / columns, rowHeight);
    }
    internal (int Item, bool After) DropTarget(Point point, int count)
    {
        if (count == 0) return (0, false);
        int row = Math.Max(0, (int)Math.Floor(point.Y / RowHeight));
        int column = Math.Clamp((int)Math.Floor(point.X / Math.Max(1, ColumnWidth)), 0, Columns - 1);
        int index = row * Columns + column;
        return index >= count ? (count - 1, true) : (index, point.X > (column + .5) * ColumnWidth);
    }
    internal int Navigate(int index, int count, double viewportHeight, Key key)
    {
        int page = Math.Max(1, (int)(viewportHeight / RowHeight)) * Columns;
        int target = key switch
        {
            Key.Left => index - 1, Key.Right => index + 1,
            Key.Up => index < Columns ? index : index - Columns,
            Key.Down => index / Columns == (count - 1) / Columns ? index : index + Columns,
            Key.Home => 0, Key.End => count - 1,
            Key.PageUp => index - page, Key.PageDown => index + page,
            _ => index
        };
        return count == 0 ? -1 : Math.Clamp(target, 0, count - 1);
    }
}

public sealed class TabInsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double width, double cell, int icon] || cell <= 0) return new Thickness();
        var geometry = TileGeometry.Create(width, cell, cell);
        // The tab's own 10-DIP padding plus this inset lines its text up with the icon.
        return new Thickness(Math.Max(0, (geometry.ColumnWidth - icon) / 2 - 10), 0, 0, 0);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
