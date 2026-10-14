using System.Drawing;
using System.Windows;
using QuickLaunch.Interop;

namespace QuickLaunch.Services;

internal static class AppIcon
{
    private static readonly Uri ResourceUri = new("/QuickLaunch;component/Assets/QuickLaunch.ico", UriKind.Relative);

    public static int TrayPixelSize()
    {
        nint taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        uint dpi = NativeMethods.GetDpiForWindow(taskbar);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        return NativeMethods.GetSystemMetricsForDpi(49, dpi); // SM_CXSMICON, at the taskbar's DPI.
    }

    public static Icon Load(int pixels)
    {
        using var stream = Application.GetResourceStream(ResourceUri).Stream;
        using var icon = new Icon(stream, pixels, pixels);
        return (Icon)icon.Clone();
    }
}
