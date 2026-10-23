using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Swiftdock.E2E;

internal static class Desktop
{
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    internal static string WindowClass(nint window)
    {
        var name = new StringBuilder(256);
        return window != 0 && GetClassName(window, name, name.Capacity) != 0 ? name.ToString() : "<unavailable>";
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    internal static string WindowTitle(nint window)
    {
        var title = new StringBuilder(1024);
        return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString() : "";
    }
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    internal static int? WindowCloaking(nint window) =>
        DwmGetWindowAttribute(window, 14, out int value, sizeof(int)) >= 0 ? value : null; // DWMWA_CLOAKED
    internal static bool IsDiscoverableWindow(nint window) => window != 0 && IsWindowVisible(window) && WindowCloaking(window) == 0;

    public static string? UnavailableReason()
    {
        if (!OperatingSystem.IsWindows()) return "E2E requires Windows.";
        if (Environment.GetEnvironmentVariable("SWIFTDOCK_E2E") != "1")
            return "Real input is opt-in. Run scripts/E2E.ps1 on an unlocked desktop (SWIFTDOCK_E2E=1).";
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
            return "E2E requires an interactive user session, not a service/CI desktop.";
        nint input = OpenInputDesktop(0, false, 0x0001 | 0x0100); // READOBJECTS | SWITCHDESKTOP
        if (input == 0) return "No accessible input desktop (locked, disconnected, or sandboxed).";
        try
        {
            if (!SwitchDesktop(input) || DesktopName(input) != "Default"
                || DesktopName(GetThreadDesktop(GetCurrentThreadId())) != "Default")
                return "E2E requires the unlocked Default input desktop.";
        }
        finally { CloseDesktop(input); }
        if (GetForegroundWindow() == 0 || GetShellWindow() == 0)
            return "E2E requires a foreground window and the Windows shell.";
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            double scale = GetDpiForSystem() / 96.0;
            if (!SystemParametersInfo(0x0030, 0, out var work, 0)
                || (work.Right - work.Left) / scale < 1000 || (work.Bottom - work.Top) / scale < 840)
                return "E2E requires a primary work area of at least 1000 × 840 DIPs for dialogs and drag margins.";
        }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
        return null;
    }

    private static string DesktopName(nint desktop)
    {
        var name = new StringBuilder(256);
        return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ? name.ToString() : "";
    }

    internal static nint[] ProcessWindowHandles(int processId)
        => TopLevelWindowHandles().Where(window =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            return owner == processId;
        }).ToArray();

    internal static nint[] TopLevelWindowHandles()
    {
        var windows = new List<nint>();
        // EnumWindows includes owned top-level windows regardless of their UIA parent.
        if (!EnumWindows((window, _) =>
        {
            windows.Add(window);
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return windows.ToArray();
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern bool SwitchDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true, SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        public override string ToString() => Rectangle.ToString();
    }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    internal static extern int GetWindowLong(nint window, int index);
    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    internal static GuiThreadInfo GuiInfo(uint thread = 0)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    internal static string FocusDescription(nint window = 0)
    {
        nint foreground = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(window == 0 ? foreground : window, out uint process);
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        try
        {
            var info = GuiInfo(thread);
            return $"foreground=0x{foreground:X} foregroundPID={foregroundPid}; thread={thread} PID={process} "
                + $"GetGUIThreadInfo.active=0x{info.Active:X} focus=0x{info.Focus:X} capture=0x{info.Capture:X}";
        }
        catch (Win32Exception error) { return $"foreground=0x{foreground:X} PID={foregroundPid}; GetGUIThreadInfo: {error.Message}"; }
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendTextMessageTimeout(nint window, uint message, nint length, StringBuilder text,
        uint flags, uint timeout, out nuint result);
    internal static void SetEditText(nint window, string value)
    {
        if (SendTextMessageTimeout(window, 0x000C, 0, new StringBuilder(value), 0x0002, 500, out nuint accepted) == 0)
            throw new TimeoutException("Test-owned EDIT WM_SETTEXT failed or exceeded 500 ms.");
        if (accepted == 0) throw new InvalidOperationException("Test-owned EDIT rejected WM_SETTEXT.");
    }
    internal static string ReadEditText(nint window)
    {
        var text = new StringBuilder(256);
        if (SendTextMessageTimeout(window, 0x000D, text.Capacity, text, 0x0002, 500, out _) == 0)
            throw new TimeoutException("Test-owned EDIT WM_GETTEXT failed or exceeded 500 ms.");
        return text.ToString();
    }
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, out NativeRect value, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, uint size);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size, Tick; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    internal static uint LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Tick;
    }
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
