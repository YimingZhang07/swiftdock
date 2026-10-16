using System.Windows;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class WindowPlacementTests
{
    [Theory]
    [InlineData(80, 73.6, 263, 144, 483.2, 217.6)]
    [InlineData(100, 92, 324, 180, 600, 272)]
    [InlineData(150, 138, 478, 270, 892, 408)]
    public void ScaledMetricsFitColumnsRowsAndMinimum(int scale, double cell, double minWidth, double minHeight, double width, double height)
    {
        var metrics = new PanelMetrics(scale);
        Assert.Equal(cell, metrics.CellSize, 8);
        var minimum = WindowPlacement.MinimumSize(cell, metrics.Factor);
        Assert.Equal(new Size(minWidth, minHeight), minimum);
        var size = WindowPlacement.ResolveSize(null, new(2000, 1200), minimum, 6, cell, 12, metrics.HorizontalChrome, metrics.VerticalChrome);
        Assert.Equal(width, size.Width, 8); Assert.Equal(height, size.Height, 8);
        var threeRows = WindowPlacement.ResolveSize(null, new(2000, 1200), minimum, 6, cell, 13, metrics.HorizontalChrome, metrics.VerticalChrome);
        Assert.Equal(height + cell, threeRows.Height, 8);
    }

    [Theory]
    [InlineData(620, 320, 100, 150, 1200, 800, 930, 480)]
    [InlineData(930, 480, 150, 100, 1200, 800, 620, 320)]
    [InlineData(620, 320, 100, 80, 1200, 800, 496, 256)]
    [InlineData(1000, 700, 100, 150, 1200, 800, 1200, 800)]
    [InlineData(100, 100, 100, 150, 1200, 800, 478, 270)]
    [InlineData(620, 320, 100, 150, 250, 200, 250, 200)]
    public void ScaleChangeResizesAndClamps(double width, double height, int oldScale, int newScale,
        double workWidth, double workHeight, double expectedWidth, double expectedHeight) =>
        Assert.Equal(new Size(expectedWidth, expectedHeight), WindowPlacement.RescaleSize(new(width, height), oldScale, newScale, new(workWidth, workHeight)));

    private static readonly WindowPlacement.Monitor[] Monitors =
    [
        new(new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040), 96),
        new(new(0, 0, 3840, 2160), new(0, 0, 3840, 2080), 192)
    ];

    [Theory]
    [InlineData(-1800, 100, -1800, 100)]
    [InlineData(-2000, -40, -1920, 0)]
    [InlineData(-600, 1000, -600, 640)]
    [InlineData(3000, 1800, 2840, 1280)]
    public void SavedPositionUsesTargetMonitorDpiAndClampsToWorkArea(int left, int top, int expectedLeft, int expectedTop)
    {
        var rect = WindowPlacement.ResolvePlacement("lastPosition", new(left, top), new(500, 400), Monitors, new(2000, 600));
        double scale = expectedLeft < 0 ? 1 : 2;
        Assert.Equal(new Rect(expectedLeft, expectedTop, 500 * scale, 400 * scale), rect);
    }

    [Theory]
    [InlineData(null, -5000, -5000)]
    [InlineData("lastPosition", 9000, 100)]
    [InlineData("cursor", -1800, 100)]
    public void MissingMonitorOrCursorModeUsesCursorMonitorDefault(string? mode, int left, int top) =>
        Assert.Equal(new Rect(1420, 520, 1000, 800),
            WindowPlacement.ResolvePlacement(mode, new(left, top), new(500, 400), Monitors, new(2000, 600)));

    [Fact]
    public void NoPositionUsesCursorMonitorAndNearestMonitorForDesktopGaps()
    {
        Assert.Equal(new Rect(-1210, 260, 500, 400),
            WindowPlacement.ResolvePlacement(null, null, new(500, 400), Monitors, new(-1000, 200)));
        Assert.Equal(new Rect(-1210, 260, 500, 400),
            WindowPlacement.ResolvePlacement(null, null, new(500, 400), Monitors, new(-2000, 200)));
    }

    [Fact]
    public void LargestIntersectionWinsOverMonitorContainingTopLeft()
    {
        WindowPlacement.Monitor[] monitors = [new(new(0, 0, 1000, 1000), new(0, 0, 1000, 960), 96),
            new(new(1000, 0, 1000, 1000), new(1000, 0, 1000, 960), 96)];
        Assert.Equal(new Rect(1000, 100, 500, 400),
            WindowPlacement.ResolvePlacement(null, new(900, 100), new(500, 400), monitors, new(0, 0)));
    }

    [Fact]
    public void OversizedSavedRectFitsEntirelyInsideWorkArea() =>
        Assert.Equal(new Rect(0, 0, 3840, 2080),
            WindowPlacement.ResolvePlacement(null, new(100, 100), new(5000, 5000), Monitors, new(-100, 0)));

    [Fact]
    public void AutoPanelSizeIsResolvedUsingSavedMonitorWorkArea()
    {
        var result = WindowPlacement.ResolvePanel(null, new(-1800, 100), null, WindowPlacement.MinimumSize(84), 6, 84, 100,
            new(Monitors, new(2000, 600)));
        Assert.Equal(new Rect(-1800, 100, 552, 728), result.Bounds);
        Assert.Equal(96, result.Monitor.Dpi);
        Assert.Equal(new Size(552, 728), result.Size);
    }

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
    [InlineData(0, 256)]
    [InlineData(7, 256)]
    [InlineData(13, 340)]
    [InlineData(100, 560)]
    public void WithoutSavedSizeUsesColumnsRowsAndSeventyPercentHeight(int count, double expectedHeight)
    {
        Size size = WindowPlacement.ResolveSize(null, new(1200, 800), WindowPlacement.MinimumSize(84), 6, 84, count, WindowPlacement.HorizontalChrome);
        Assert.Equal(new Size(552, expectedHeight), size);
    }

    [Fact]
    public void AutoSizeUsesColumnsThatFitAndMinimumRowHeight()
    {
        Assert.Equal(new Size(300, 256), WindowPlacement.ResolveSize(null, new(300, 800), WindowPlacement.MinimumSize(84), 6, 84, 4, WindowPlacement.HorizontalChrome));
        Assert.Equal(new Size(300, 340), WindowPlacement.ResolveSize(null, new(300, 800), WindowPlacement.MinimumSize(84), 6, 84, 7, WindowPlacement.HorizontalChrome));
        Assert.Equal(new Size(588, 448), WindowPlacement.ResolveSize(null, new(1200, 800), WindowPlacement.MinimumSize(180), 1, 180, 1, WindowPlacement.HorizontalChrome));
    }

    [Theory]
    [InlineData(84, 300, 172)]
    [InlineData(92, 324, 180)]
    [InlineData(100, 348, 188)]
    [InlineData(180, 588, 268)]
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
