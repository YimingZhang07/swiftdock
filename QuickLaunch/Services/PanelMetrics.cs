namespace QuickLaunch.Services;

// Panel DIPs only. Scrollbars and native window chrome keep their Windows metrics.
public readonly record struct PanelMetrics(int Scale)
{
    public double Factor => Scale / 100d;
    public int IconSize => Scale * 40 / 100;
    public double CellSize => 92 * Factor;
    public double TileSize => 88 * Factor;
    public double HorizontalChrome => WindowPlacement.ContentPadding * 2 * Factor + WindowPlacement.ScrollBarWidth + 2;
    public double VerticalChrome => WindowPlacement.VerticalChrome * Factor;

    public static int Normalize(int scale) => (int)Math.Round(Math.Clamp(scale, 80, 150) / 10d, MidpointRounding.AwayFromZero) * 10;
    public static int FromLegacyIconSize(int size) => Normalize((int)Math.Clamp(size * 2.5, 80, 150));
    public static int Step(int scale, int direction) => Normalize(scale + Math.Sign(direction) * 10);
}
