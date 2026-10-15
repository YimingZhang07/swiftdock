using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace QuickLaunch.Snapshots;

// Guard native positioning too: WPF/FluentWindow may reposition on load or DPI changes.
internal static class OffscreenWindow
{
    internal static void Prepare(Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.VirtualScreenLeft - 16384;
        window.Top = SystemParameters.VirtualScreenTop;
        window.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => e.Handled = true), handledEventsToo: true);
        window.SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(window).Handle;
            const int extendedStyle = -20;
            long style = GetWindowLongPtr(handle, extendedStyle).ToInt64();
            nint previous = SetWindowLongPtr(handle, extendedStyle, (nint)(style | 0x08000000 | 0x00000080)); // NOACTIVATE | TOOLWINDOW
            int error = Marshal.GetLastWin32Error();
            if (previous == 0 && error != 0) throw new Win32Exception(error);
            HwndSource.FromHwnd(handle)!.AddHook(KeepOffscreen);
        };
    }

    internal static void Show(Window window)
    {
        // Statically dispatch Window.Show, never a library's modal convenience method.
        new WindowInteropHelper(window).EnsureHandle(); // Install the native guard while still hidden.
        window.Show();
        window.UpdateLayout();
    }

    internal static HwndSource MenuSurface(FrameworkElement menu)
    {
        // A ContextMenu must have no normal visual/logical parent. A presentation
        // source can make it a root without opening Popup/ContextMenu.IsOpen.
        menu.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => e.Handled = true), handledEventsToo: true);
        var source = new HwndSource(new HwndSourceParameters("QuickLaunch snapshot menu")
        {
            PositionX = GetSystemMetrics(76) - 16384, PositionY = GetSystemMetrics(77), Width = 640, Height = 640,
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            HwndSourceHook = KeepOffscreen
        });
        try
        {
            source.SizeToContent = SizeToContent.WidthAndHeight;
            source.RootVisual = menu;
            if (!SetWindowPos(source.Handle, 0, GetSystemMetrics(76) - 16384, GetSystemMetrics(77), 0, 0, 0x0055)) // NOSIZE | NOZORDER | NOACTIVATE | SHOWWINDOW
                throw new Win32Exception();
            return source;
        }
        catch { source.Dispose(); throw; }
    }

    private static nint KeepOffscreen(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0046) // WM_WINDOWPOSCHANGING
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            position.X = GetSystemMetrics(76) - 16384; // SM_XVIRTUALSCREEN, physical pixels
            position.Y = GetSystemMetrics(77);
            position.Flags = (position.Flags & ~0x0002u) | 0x0010u | 0x0004u; // move, NOACTIVATE, NOZORDER
            Marshal.StructureToPtr(position, lParam, false);
        }
        else if (message == 0x0021) { handled = true; return 3; } // WM_MOUSEACTIVATE: MA_NOACTIVATE
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
