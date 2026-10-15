using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickLaunch.E2E;

internal static class Desktop
{
    public static string? UnavailableReason()
    {
        if (!OperatingSystem.IsWindows()) return "E2E requires Windows.";
        if (Environment.GetEnvironmentVariable("QUICKLAUNCH_E2E") != "1")
            return "Real input is opt-in. Run scripts/E2E.ps1 on an unlocked desktop (QUICKLAUNCH_E2E=1).";
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
    {
        var windows = new List<nint>();
        // EnumWindows includes owned top-level windows regardless of their UIA parent.
        if (!EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == processId) windows.Add(window);
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
    private struct NativeRect { public int Left, Top, Right, Bottom; }
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
