using System.Runtime.InteropServices;

namespace QuickLaunch.Interop;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
        public nuint PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommitUsage;
    }
    // EX2 is supported by current Windows 11; failure is reported as unavailable, never zero.
    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);
    // Image factory returns an owned IUnknown pointer; caller releases it and every returned HBITMAP.
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint result);
    // PrivateExtractIcons returns owned HICONs at the requested physical size; negative indices address resource IDs.
    [LibraryImport("user32.dll", EntryPoint = "PrivateExtractIconsW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PrivateExtractIcons(string path, int index, int width, int height, out nint icon, out uint resourceId, uint count, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);
    // Known-folder paths are allocated by the Shell and freed with CoTaskMemFree.
    [LibraryImport("shell32.dll")]
    internal static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);
    [LibraryImport("ole32.dll")]
    internal static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll")]
    internal static partial void OleUninitialize();
    internal const int WmSysCommand = 0x0112, ScKeyMenu = 0xF100;
    internal const int WmHotkey = 0x0312;
    internal const int WmDpiChanged = 0x02E0;
    internal const int WmChangeUiState = 0x0127, WmUpdateUiState = 0x0128;
    internal const int WmEnterSizeMove = 0x0231, WmExitSizeMove = 0x0232, WmSizing = 0x0214;
    internal const int WmNcHitTest = 0x0084;
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
    internal static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);
    // A user-invoked secondary process may grant foreground permission to the primary.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint processId);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint data);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);
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
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindow(string className, string? windowName);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    // DWM attributes do not allocate resources; failure simply keeps square corners.
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, in int value, uint size);
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, uint size);
}
