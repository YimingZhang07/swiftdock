using System.Runtime.InteropServices;

namespace QuickLaunch.Interop;

internal static partial class NativeMethods
{
    internal const int WmHotkey = 0x0312;
    internal const int WmDpiChanged = 0x02E0;
    internal const uint ModAlt = 0x0001, ModNoRepeat = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    // RegisterHotKey owns a registration until UnregisterHotKey or process exit.
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hwnd, int id);
    // Activation is best effort under Windows foreground rules; no input attachment/injection.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    // Native window positioning stays in physical pixels, including negative monitor origins.
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out Rect rect);
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    // DWM attributes do not allocate resources; failure simply keeps square corners.
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, in int value, uint size);
}
