using System.Windows;
using System.Windows.Input;
using Swiftdock.Views;
using Xunit;

namespace Swiftdock.Tests;

public sealed class TileGeometryTests
{
    [Theory]
    [InlineData(335.5, 84, 3)] [InlineData(336, 84, 4)] [InlineData(379, 84, 4)]
    [InlineData(400, 92, 4)] [InlineData(399.5, 100, 3)] [InlineData(400, 100, 4)]
    [InlineData(60, 100, 1)]
    public void ColumnsFillAvailableWidthWithoutChangingRows(double width, double cell, int columns)
    {
        var geometry = TileGeometry.Create(width, cell, cell);
        Assert.Equal(columns, geometry.Columns);
        Assert.Equal(width, geometry.Columns * geometry.ColumnWidth, 8);
        Assert.Equal(cell, geometry.RowHeight);
    }

    [Theory]
    [InlineData(0, 0, 0, false)] [InlineData(49, 30, 0, false)] [InlineData(51, 30, 0, true)]
    [InlineData(99, 30, 0, true)] [InlineData(100, 30, 1, false)] [InlineData(399, 30, 3, true)]
    [InlineData(0, 84, 4, false)] [InlineData(250, 100, 5, true)] [InlineData(0, 500, 5, true)]
    public void DropsUseDistributedColumnMidpointsAndAppendInEmptyLastRowCells(double x, double y, int item, bool after)
    {
        var geometry = TileGeometry.Create(400, 84, 84);
        Assert.Equal((item, after), geometry.DropTarget(new Point(x, y), 6));
        Assert.Equal((0, false), geometry.DropTarget(new Point(x, y), 0));
    }

    [Theory]
    [InlineData(1, Key.Down, 5)] [InlineData(1, Key.Up, 1)] [InlineData(5, Key.Up, 1)]
    [InlineData(7, Key.Down, 9)] [InlineData(9, Key.Down, 9)] [InlineData(3, Key.Right, 4)]
    [InlineData(4, Key.Left, 3)] [InlineData(1, Key.PageDown, 9)] [InlineData(9, Key.PageUp, 1)]
    [InlineData(5, Key.Home, 0)] [InlineData(5, Key.End, 9)]
    public void NavigationUsesTheSameColumnsAndFixedRowHeight(int index, Key key, int expected)
    {
        var geometry = TileGeometry.Create(400, 84, 84);
        Assert.Equal(expected, geometry.Navigate(index, 10, 180, key));
        Assert.Equal(-1, geometry.Navigate(-1, 0, 180, key));
    }
}
