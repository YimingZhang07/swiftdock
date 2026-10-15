using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuickLaunch.E2E;

// A hidden panel cannot own foreground. The show-hotkey uses this inert, test-owned HWND,
// never the editor/terminal/browser that happened to become foreground when the panel hid.
internal sealed class HotkeyInputWindow : IDisposable
{
    private readonly Thread _thread;
    public nint Handle { get; }

    public HotkeyInputWindow()
    {
        var ready = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            nint window = 0;
            try
            {
                // Predefined STATIC class: no text editing, commands, or external side effects.
                window = CreateWindowEx(0x88, "STATIC", "QuickLaunch E2E hotkey input", 0x10C00000,
                    40, 40, 320, 80, 0, 0, 0, 0); // topmost tool window, visible caption
                if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!ready.TrySetResult(window)) return;
                while (IsWindow(window) && GetMessage(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { if (window != 0 && IsWindow(window)) DestroyWindow(window); }
        }) { IsBackground = true, Name = "E2E hotkey input window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        try { Handle = ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        catch { ready.TrySetCanceled(); throw; }
    }

    public void Dispose()
    {
        PostMessage(Handle, 0x0010, 0, 0); // WM_CLOSE; message loop then observes the destroyed HWND.
        if (!_thread.Join(2000)) throw new TimeoutException("The E2E hotkey input window did not close.");
    }

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
