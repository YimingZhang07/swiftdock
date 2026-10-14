using System.Windows;
using System.Windows.Interop;
using QuickLaunch.Interop;
using QuickLaunch.Services;

namespace QuickLaunch.Views;

internal static class DialogTheme
{
    public static void Attach(Window window, ThemeService? theme)
    {
        void Update() => Apply(window, theme?.IsDark ?? ThemeService.ReadPreferences().Dark);
        void Initialized(object? sender, EventArgs e)
        {
            nint hwnd = new WindowInteropHelper(window).Handle;
            nint style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, style | NativeMethods.WsExDlgModalFrame);
            NativeMethods.SendMessage(hwnd, NativeMethods.WmSetIcon, 0, 0);
            NativeMethods.SendMessage(hwnd, NativeMethods.WmSetIcon, 1, 0);
            NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0037); // Frame changed, no move/size/z-order/activation.
            Update();
        }
        window.SourceInitialized += Initialized;
        if (theme is not null) theme.Changed += Update;
        window.Closed += (_, _) =>
        {
            window.SourceInitialized -= Initialized;
            if (theme is not null) theme.Changed -= Update;
        };
    }

    internal static void Apply(Window window, bool dark)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        int immersive = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20, in immersive, sizeof(int));
    }
}
