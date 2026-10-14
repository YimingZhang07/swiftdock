using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QuickLaunch.Interop;

namespace QuickLaunch.Services;

public static class WindowPlacement
{
    public const double HeaderHeight = 44, ContentPadding = 12, ScrollBarWidth = 12;
    public static GridLength HeaderRowHeight => new(HeaderHeight);
    public static Thickness ContentMargin => new(ContentPadding);
    // Two extra DIPs cover layout rounding at fractional monitor scales.
    public const double HorizontalChrome = ContentPadding * 2 + ScrollBarWidth + 2;
    public const double VerticalChrome = HeaderHeight + ContentPadding * 2;
    public static Size MinimumSize(double cellSize) => new(Math.Ceiling(3 * cellSize + HorizontalChrome), Math.Ceiling(VerticalChrome + cellSize));

    public static Size ResolveSize(Size? savedSize, Size workArea, Size minimum, int columns, double cellSize, int itemCount, double horizontalChrome)
    {
        double width = Math.Min(Math.Max(savedSize?.Width ?? columns * cellSize + horizontalChrome, minimum.Width), workArea.Width);
        double height = savedSize?.Height ?? AutoHeight(width, workArea.Height, cellSize, itemCount, horizontalChrome);
        return new(width, Math.Min(Math.Max(height, minimum.Height), workArea.Height));
    }

    private static double AutoHeight(double width, double workHeight, double cellSize, int itemCount, double horizontalChrome)
    {
        int columns = Math.Max(1, (int)((width - horizontalChrome) / cellSize));
        return Math.Min(Math.Max(2, Math.Ceiling(Math.Max(1, itemCount) / (double)columns)) * cellSize + VerticalChrome, workHeight * .7);
    }

    public static void AtCursor(Window window, int columns = 6, double cellSize = 84, int itemCount = 1, Size? savedSize = null)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.GetCursorPos(out var point) && NativeMethods.GetWindowRect(hwnd, out var current))
            point = new() { X = (current.Left + current.Right) / 2, Y = (current.Top + current.Bottom) / 2 };
        var monitor = NativeMethods.MonitorFromPoint(point, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        double scale = NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0
            ? dpi / 96.0 : NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        var work = info.Work;
        var workArea = new Size((work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
        window.MaxWidth = workArea.Width; window.MaxHeight = workArea.Height;
        Size size = ResolveSize(savedSize, workArea, new(window.MinWidth, window.MinHeight), columns, cellSize, itemCount,
            HorizontalChrome);
        window.Width = size.Width; window.Height = size.Height;
        var width = (int)Math.Round(window.Width * scale);
        var height = (int)Math.Round(window.Height * scale);
        int x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);
        int y = Math.Clamp(work.Top + (work.Bottom - work.Top) / 4, work.Top, Math.Max(work.Top, work.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, 0, x, y, width, height, 0x0014); // NOZORDER | NOACTIVATE
    }
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

    public static void FitContent(Window window, double cellSize, int itemCount, Size? savedSize = null)
    {
        if (savedSize.HasValue) return;
        ResizeInPlace(window, cellSize, itemCount);
    }

    public static void ResizeInPlace(Window window, double cellSize, int itemCount, int? autoColumns = null)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = (rect.Left + rect.Right) / 2, Y = rect.Top + 1 }, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(hwnd)) / 96.0;
        var workArea = new Size((info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
        window.MaxWidth = workArea.Width; window.MaxHeight = workArea.Height;
        double desiredWidth = autoColumns is { } columns ? columns * cellSize + HorizontalChrome : window.Width;
        window.Width = Math.Min(Math.Max(desiredWidth, window.MinWidth), workArea.Width);
        window.Height = Math.Min(Math.Max(AutoHeight(window.Width, workArea.Height, cellSize, itemCount, HorizontalChrome), window.MinHeight), workArea.Height);
        int width = (int)Math.Round(window.Width * scale), height = (int)Math.Round(window.Height * scale);
        int x = Math.Clamp(rect.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        int y = Math.Clamp(rect.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, 0, x, y, width, height, 0x0014);
    }
}
