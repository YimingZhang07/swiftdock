using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Drawing;

namespace QuickLaunch.E2E;

// A hidden panel cannot own foreground. The show-hotkey uses this inert, test-owned HWND,
// never the editor/terminal/browser that happened to become foreground when the panel hid.
internal sealed class HotkeyInputWindow : IDisposable
{
    private readonly Thread _thread;
    private readonly WindowProc _windowProc;
    public nint Handle { get; }
    public nint EditHandle { get; private set; }

    public HotkeyInputWindow(Rectangle? backdropBounds = null)
    {
        _windowProc = (hwnd, message, wParam, lParam) =>
        {
            // This is a real top-level window, not a STATIC control masquerading as one.
            // Focus belongs to its own thread; no AttachThreadInput/remote SetFocus.
            if (message == 0x0006 && (wParam.ToInt64() & 0xffff) != 0 && EditHandle != 0) SetFocus(EditHandle);
            if (message == 0x0007 && EditHandle != 0) SetFocus(EditHandle);
            if (message == 0x0002) { PostQuitMessage(0); return 0; }
            return DefWindowProc(hwnd, message, wParam, lParam);
        };
        var ready = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            nint window = 0;
            string className = "QuickLaunch.E2E.Input." + Guid.NewGuid().ToString("N");
            nint module = GetModuleHandle(null);
            try
            {
                var windowClass = new WindowClass { WindowProc = _windowProc, Instance = module,
                    Background = 6, ClassName = className }; // COLOR_WINDOW + 1
                if (RegisterClass(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var bounds = backdropBounds ?? new Rectangle(40, 40, 320, 80);
                // Probe: normal z-order white surface behind the topmost launcher,
                // plus a real EDIT below it. Ordinary hotkey guards remain inert.
                window = CreateWindowEx(backdropBounds is null ? 0x88u : 0x80u, className, "QuickLaunch E2E hotkey input",
                    0x10C00000u,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, module, 0);
                if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (backdropBounds is not null)
                {
                    EditHandle = CreateWindowEx(0, "EDIT", "", 0x50810000, 16, bounds.Height - 80, 240, 32, window, 0, 0, 0);
                    if (EditHandle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    // Detach IME contexts on the HWND-owning thread, after the EDIT
                    // exists and before any test input. IACE_CHILDREN affects only
                    // existing children, so detach the top-level HWND separately.
                    // This changes only our disposable fixture, never the user's IME.
                    if (!ImmAssociateContextEx(window, 0, 0) || !ImmAssociateContextEx(window, 0, 0x0001))
                        throw new InvalidOperationException("Could not disable IME for the test-owned input fixture.");
                    foreach (nint hwnd in new[] { window, EditHandle })
                    {
                        nint context = ImmGetContext(hwnd);
                        if (context == 0) continue;
                        ImmReleaseContext(hwnd, context);
                        throw new InvalidOperationException("The test-owned input fixture still has an IME context.");
                    }
                }
                if (!ready.TrySetResult(window)) return;
                while (IsWindow(window) && GetMessage(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { if (window != 0 && IsWindow(window)) DestroyWindow(window); UnregisterClass(className, module); }
        }) { IsBackground = true, Name = "E2E hotkey input window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        try { Handle = ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        catch { ready.TrySetCanceled(); throw; }
    }

    public void ClearEditText()
    {
        if (EditHandle == 0) throw new InvalidOperationException("This fixture has no EDIT control.");
        // Reset only our own fixture, independently of EDIT shortcut/manifest support.
        // The fixture's UI thread processes WM_SETTEXT before this bounded call returns.
        if (SendTextMessageTimeout(EditHandle, 0x000C, 0, "", 0x0002, 500, out nuint result) == 0)
            throw new TimeoutException("Test-owned EDIT WM_SETTEXT failed or exceeded 500 ms.");
        if (result == 0) throw new InvalidOperationException("Test-owned EDIT rejected WM_SETTEXT.");
    }

    public void Dispose()
    {
        PostMessage(Handle, 0x0010, 0, 0); // WM_CLOSE; message loop then observes the destroyed HWND.
        if (!_thread.Join(2000)) throw new TimeoutException("The E2E hotkey input window did not close.");
    }

    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProc WindowProc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass value);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SendTextMessageTimeout(nint window, uint message, nint wParam, string text,
        uint flags, uint timeout, out nuint result);
    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmAssociateContextEx(nint window, nint context, uint flags);
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(nint window, nint context);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
}
