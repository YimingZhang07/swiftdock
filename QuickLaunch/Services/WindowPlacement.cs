using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QuickLaunch.Interop;

namespace QuickLaunch.Services;

public static class WindowPlacement
{
    public static void AtCursor(Window window)
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
        window.Width = Math.Min(620, (work.Right - work.Left) / scale);
        window.Height = Math.Min(500, (work.Bottom - work.Top) / scale * 0.7);
        var width = (int)Math.Round(window.Width * scale);
        var height = (int)Math.Round(window.Height * scale);
        int x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);
        int y = Math.Clamp(work.Top + (work.Bottom - work.Top) / 4, work.Top, Math.Max(work.Top, work.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, 0, x, y, width, height, 0x0014); // NOZORDER | NOACTIVATE
    }
}
