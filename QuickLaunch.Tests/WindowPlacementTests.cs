using System.Windows;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class WindowPlacementTests
{
    [Theory]
    [InlineData(1600, 1200, 1200, 800)]
    [InlineData(100, 100, 315, 340)]
    [InlineData(700.5, 750.25, 700.5, 750.25)]
    [InlineData(1600, 100, 1200, 340)]
    public void SavedSizeIsClampedToWorkAreaAndMinimum(double width, double height, double expectedWidth, double expectedHeight)
    {
        Size size = WindowPlacement.ResolveSize(new(width, height), new(1200, 800), new(315, 340), 6, 88, 100, 51);
        Assert.Equal(new Size(expectedWidth, expectedHeight), size);
    }

    [Theory]
    [InlineData(0, 236)]
    [InlineData(7, 236)]
    [InlineData(13, 320)]
    [InlineData(100, 560)]
    public void WithoutSavedSizeUsesColumnsRowsAndSeventyPercentHeight(int count, double expectedHeight)
    {
        Size size = WindowPlacement.ResolveSize(null, new(1200, 800), WindowPlacement.MinimumSize(84), 6, 84, count, WindowPlacement.HorizontalChrome);
        Assert.Equal(new Size(542, expectedHeight), size);
    }

    [Fact]
    public void AutoSizeUsesColumnsThatFitAndMinimumRowHeight()
    {
        Assert.Equal(new Size(290, 236), WindowPlacement.ResolveSize(null, new(290, 800), WindowPlacement.MinimumSize(84), 6, 84, 4, WindowPlacement.HorizontalChrome));
        Assert.Equal(new Size(290, 320), WindowPlacement.ResolveSize(null, new(290, 800), WindowPlacement.MinimumSize(84), 6, 84, 7, WindowPlacement.HorizontalChrome));
        Assert.Equal(new Size(578, 428), WindowPlacement.ResolveSize(null, new(1200, 800), WindowPlacement.MinimumSize(180), 1, 180, 1, WindowPlacement.HorizontalChrome));
    }

    [Theory]
    [InlineData(84, 290, 152)]
    [InlineData(92, 314, 160)]
    [InlineData(100, 338, 168)]
    [InlineData(180, 578, 248)]
    public void MinimumFitsThreeColumnsAndOneRow(double cell, double width, double height) =>
        Assert.Equal(new Size(width, height), WindowPlacement.MinimumSize(cell));

    [Theory]
    [InlineData(3, 84)] [InlineData(6, 92)] [InlineData(12, 100)]
    public void AutoWidthHasExactlyConfiguredCellsAndChrome(int columns, double cell)
    {
        Size size = WindowPlacement.ResolveSize(null, new(3000, 2000), WindowPlacement.MinimumSize(cell), columns, cell, 0, WindowPlacement.HorizontalChrome);
        Assert.Equal(columns * cell + WindowPlacement.HorizontalChrome, size.Width);
        Assert.Equal(2 * cell + WindowPlacement.VerticalChrome, size.Height);
    }

    [Fact]
    public void WorkAreaWinsWhenSmallerThanMinimum()
    {
        Assert.Equal(new Size(250, 200), WindowPlacement.ResolveSize(new(700, 500), new(250, 200), new(315, 340), 6, 88, 1, 51));
    }
}
