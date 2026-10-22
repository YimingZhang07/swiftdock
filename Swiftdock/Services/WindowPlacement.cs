using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Swiftdock.Interop;

namespace Swiftdock.Services;

public static class WindowPlacement
{
    public readonly record struct Monitor(Rect Bounds, Rect WorkArea, double Dpi)
    {
        public double Scale => Dpi > 0 ? Dpi / 96 : 1;
        public Size WorkSize => new(WorkArea.Width / Scale, WorkArea.Height / Scale);
    }
    internal sealed record Desktop(IReadOnlyList<Monitor> Monitors, Point Cursor);

    // WPF-UI's UiScrollBar reserves 14 DIP, including its 12 DIP hover track.
    public const double HeaderHeight = 48, ContentPadding = 16, ScrollBarWidth = 14;
    // One 12-DIP status line with breathing room, below the scrollable tile area.
    public const double BottomPadding = 24;
    // Two extra DIPs cover layout rounding at fractional monitor scales.
    public const double HorizontalChrome = ContentPadding * 2 + ScrollBarWidth + 2;
    public const double VerticalChrome = HeaderHeight + ContentPadding + BottomPadding;
    public static Size MinimumSize(double cellSize, double factor = 1) => new(
        Math.Ceiling(3 * cellSize + ContentPadding * 2 * factor + ScrollBarWidth + 2),
        Math.Ceiling(VerticalChrome * factor + cellSize));

    public static Size RescaleSize(Size size, int oldScale, int newScale, Size workArea)
    {
        var metrics = new PanelMetrics(newScale);
        var minimum = MinimumSize(metrics.CellSize, metrics.Factor);
        double ratio = newScale / (double)oldScale;
        return new(Math.Min(Math.Max(size.Width * ratio, minimum.Width), workArea.Width),
            Math.Min(Math.Max(size.Height * ratio, minimum.Height), workArea.Height));
    }

    public static Size ResolveSize(Size? savedSize, Size workArea, Size minimum, int columns, double cellSize, int itemCount,
        double horizontalChrome, double verticalChrome = VerticalChrome)
    {
        double width = Math.Min(Math.Max(savedSize?.Width ?? columns * cellSize + horizontalChrome, minimum.Width), workArea.Width);
        double height = savedSize?.Height ?? AutoHeight(width, workArea.Height, cellSize, itemCount, horizontalChrome, verticalChrome);
        return new(width, Math.Min(Math.Max(height, minimum.Height), workArea.Height));
    }

    private static double AutoHeight(double width, double workHeight, double cellSize, int itemCount, double horizontalChrome, double verticalChrome)
    {
        int columns = Math.Max(1, (int)((width - horizontalChrome) / cellSize));
        return Math.Min(Math.Max(2, Math.Ceiling(Math.Max(1, itemCount) / (double)columns)) * cellSize + verticalChrome, workHeight * .7);
    }

    public static Rect ResolvePlacement(string? mode, Point? savedPosition, Size desiredSize, IReadOnlyList<Monitor> monitors, Point cursor)
    {
        var (monitor, position) = SelectMonitor(mode, savedPosition, desiredSize, monitors, cursor);
        return PlaceOnMonitor(monitor, position, desiredSize);
    }

    private static (Monitor Monitor, Point? Position) SelectMonitor(string? mode, Point? savedPosition, Size desiredSize,
        IReadOnlyList<Monitor> monitors, Point cursor)
    {
        if (monitors.Count == 0) throw L.Exception<ArgumentException>(L.Text("AtLeastOneMonitorIsRequired"));
        if (mode != "cursor" && savedPosition is { } position)
        {
            var origin = monitors.MinBy(m => DistanceSquared(position, m.Bounds));
            // Reconstruct the saved rectangle at its origin's DPI, then compare physical intersections.
            var rect = new Rect(position, new Size(Math.Round(desiredSize.Width * origin.Scale), Math.Round(desiredSize.Height * origin.Scale)));
            double largest = 0;
            Monitor? target = null;
            foreach (var monitor in monitors)
            {
                double area = IntersectionArea(rect, monitor.Bounds);
                if (area > largest) { largest = area; target = monitor; }
            }
            if (target is { } savedMonitor) return (savedMonitor, position);
        }
        return (monitors.MinBy(m => DistanceSquared(cursor, m.Bounds)), null);
    }

    private static double IntersectionArea(Rect first, Rect second)
    {
        var intersection = Rect.Intersect(first, second);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }

    private static double DistanceSquared(Point point, Rect bounds)
    {
        double x = point.X - Math.Clamp(point.X, bounds.Left, bounds.Right - 1);
        double y = point.Y - Math.Clamp(point.Y, bounds.Top, bounds.Bottom - 1);
        return x * x + y * y;
    }

    private static Rect PlaceOnMonitor(Monitor monitor, Point? position, Size size)
    {
        var work = monitor.WorkArea;
        double width = Math.Min(work.Width, Math.Round(size.Width * monitor.Scale));
        double height = Math.Min(work.Height, Math.Round(size.Height * monitor.Scale));
        double x = position?.X ?? work.Left + Math.Floor((work.Width - width) / 2);
        double y = position?.Y ?? work.Top + Math.Floor(work.Height / 4);
        return new(Math.Clamp(x, work.Left, work.Right - width), Math.Clamp(y, work.Top, work.Bottom - height), width, height);
    }

    internal static (Rect Bounds, Monitor Monitor, Size Size) ResolvePanel(string? mode, Point? position, Size? savedSize,
        Size minimum, int columns, double cellSize, int itemCount, Desktop desktop, int panelScale = 100)
    {
        var metrics = new PanelMetrics(panelScale);
        var origin = desktop.Monitors.MinBy(m => DistanceSquared(mode != "cursor" && position is { } saved ? saved : desktop.Cursor, m.Bounds));
        var desired = savedSize ?? ResolveSize(null, origin.WorkSize, minimum, columns, cellSize, itemCount, metrics.HorizontalChrome, metrics.VerticalChrome);
        var (monitor, savedPosition) = SelectMonitor(mode, position, desired, desktop.Monitors, desktop.Cursor);
        var size = ResolveSize(savedSize, monitor.WorkSize, minimum, columns, cellSize, itemCount, metrics.HorizontalChrome, metrics.VerticalChrome);
        return (PlaceOnMonitor(monitor, savedPosition, size), monitor, size);
    }

    internal static void PlacePanel(Window window, Desktop desktop, string? mode, Point? position, int columns,
        double cellSize, int itemCount, Size? savedSize, bool currentMonitor = false, int panelScale = 100)
    {
        if (desktop.Monitors.Count == 0) return;
        nint hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (currentMonitor && NativeMethods.GetWindowRect(hwnd, out var currentRect))
        {
            var rect = ToRect(currentRect);
            var monitor = desktop.Monitors.MaxBy(m => IntersectionArea(rect, m.Bounds));
            desktop = desktop with { Cursor = new(monitor.Bounds.Left + monitor.Bounds.Width / 2, monitor.Bounds.Top + monitor.Bounds.Height / 2) };
        }
        var minimum = MinimumSize(cellSize, panelScale / 100d);
        var placement = ResolvePanel(mode, position, savedSize, minimum, columns, cellSize, itemCount, desktop, panelScale);
        window.MinWidth = Math.Min(minimum.Width, placement.Monitor.WorkSize.Width);
        window.MinHeight = Math.Min(minimum.Height, placement.Monitor.WorkSize.Height);
        window.MaxWidth = placement.Monitor.WorkSize.Width; window.MaxHeight = placement.Monitor.WorkSize.Height;
        window.Width = placement.Size.Width; window.Height = placement.Size.Height;
        var bounds = placement.Bounds;
        NativeMethods.SetWindowPos(hwnd, 0, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0x0014);
    }

    internal static Desktop ReadDesktop(nint hwnd)
    {
        if (!NativeMethods.GetCursorPos(out var point) && NativeMethods.GetWindowRect(hwnd, out var current))
            point = new() { X = (current.Left + current.Right) / 2, Y = (current.Top + current.Bottom) / 2 };
        List<Monitor> monitors = [];
        NativeMethods.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeMethods.Rect rect, nint data) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                double dpi = NativeMethods.GetDpiForMonitor(monitor, 0, out var value, out _) == 0 ? value : NativeMethods.GetDpiForWindow(hwnd);
                monitors.Add(new(ToRect(info.Monitor), ToRect(info.Work), dpi));
            }
            return true;
        }, 0);
        return new(monitors, new(point.X, point.Y));
    }

    internal static Rect ToRect(NativeMethods.Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    public static void UpdateWorkAreaLimits(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = (rect.Left + rect.Right) / 2, Y = rect.Top + 1 }, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(hwnd)) / 96.0;
        window.MaxWidth = (info.Work.Right - info.Work.Left) / scale;
        window.MaxHeight = (info.Work.Bottom - info.Work.Top) / scale;
    }

    public static void FitContent(Window window, double cellSize, int itemCount, Size? savedSize = null, int panelScale = 100)
    {
        if (savedSize.HasValue) return;
        ResizeInPlace(window, cellSize, itemCount, panelScale: panelScale);
    }

    public static void ResizeInPlace(Window window, double cellSize, int itemCount, int? autoColumns = null, int panelScale = 100)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = (rect.Left + rect.Right) / 2, Y = rect.Top + 1 }, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(hwnd)) / 96.0;
        var workArea = new Size((info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
        window.MaxWidth = workArea.Width; window.MaxHeight = workArea.Height;
        var metrics = new PanelMetrics(panelScale);
        double desiredWidth = autoColumns is { } columns ? columns * cellSize + metrics.HorizontalChrome : window.Width;
        window.Width = Math.Min(Math.Max(desiredWidth, window.MinWidth), workArea.Width);
        window.Height = Math.Min(Math.Max(AutoHeight(window.Width, workArea.Height, cellSize, itemCount, metrics.HorizontalChrome, metrics.VerticalChrome), window.MinHeight), workArea.Height);
        int width = (int)Math.Round(window.Width * scale), height = (int)Math.Round(window.Height * scale);
        int x = Math.Clamp(rect.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        int y = Math.Clamp(rect.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, 0, x, y, width, height, 0x0014);
    }
}
