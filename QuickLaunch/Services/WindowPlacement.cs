using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QuickLaunch.Interop;

namespace QuickLaunch.Services;

public static class WindowPlacement
{
    public static void AtCursor(Window window, int columns = 6, double cellSize = 88, int itemCount = 1)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.GetCursorPos(out var point)) return;
        var monitor = NativeMethods.MonitorFromPoint(point, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        double scale = NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0
            ? dpi / 96.0 : NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        var work = info.Work;
        window.Width = Math.Min(columns * cellSize + 34 + SystemParameters.VerticalScrollBarWidth, (work.Right - work.Left) / scale);
        int actualColumns = Math.Max(1, (int)((window.Width - 34 - SystemParameters.VerticalScrollBarWidth) / cellSize));
        window.Height = Math.Min(Math.Max(340, Math.Ceiling(Math.Max(1, itemCount) / (double)actualColumns) * cellSize + 245), (work.Bottom - work.Top) / scale * 0.7);
        var width = (int)Math.Round(window.Width * scale);
        var height = (int)Math.Round(window.Height * scale);
        int x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);
        int y = Math.Clamp(work.Top + (work.Bottom - work.Top) / 4, work.Top, Math.Max(work.Top, work.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, 0, x, y, width, height, 0x0014); // NOZORDER | NOACTIVATE
    }
    public static void FitContent(Window window, double cellSize, int itemCount)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = (rect.Left + rect.Right) / 2, Y = rect.Top + 1 }, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(hwnd)) / 96.0;
        int columns = Math.Max(1, (int)((window.Width - 34 - SystemParameters.VerticalScrollBarWidth) / cellSize));
        double height = Math.Min(Math.Max(340, Math.Ceiling(Math.Max(1, itemCount) / (double)columns) * cellSize + 245), (info.Work.Bottom - info.Work.Top) / scale * .7);
        if (Math.Abs(height - window.Height) < .5) return;
        window.Height = height;
        int pixels = (int)Math.Round(height * scale);
        NativeMethods.SetWindowPos(hwnd, 0, rect.Left, Math.Max(info.Work.Top, Math.Min(rect.Top, info.Work.Bottom - pixels)), rect.Right - rect.Left, pixels, 0x0014);
    }
}
