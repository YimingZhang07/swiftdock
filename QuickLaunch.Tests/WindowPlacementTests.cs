using System.Windows;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class WindowPlacementTests
{
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
        Assert.Equal(new Rect(-1800, 100, 542, 728), result.Bounds);
        Assert.Equal(96, result.Monitor.Dpi);
        Assert.Equal(new Size(542, 728), result.Size);
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
