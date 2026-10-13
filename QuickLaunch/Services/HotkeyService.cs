using System.Runtime.InteropServices;
using System.Windows.Interop;
using QuickLaunch.Interop;

namespace QuickLaunch.Services;

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action _pressed;
    private const int Id = 0x514C;
    public bool Registered { get; }
    public int Error { get; }

    public HotkeyService(nint handle, Action pressed)
    {
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Missing HWND.");
        _pressed = pressed;
        _source.AddHook(HandleMessage);
        Registered = NativeMethods.RegisterHotKey(handle, Id,
            NativeMethods.ModAlt | NativeMethods.ModNoRepeat, 0x20);
        Error = Registered ? 0 : Marshal.GetLastPInvokeError();
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam == Id)
        {
            handled = true;
            _pressed();
        }
        return 0;
    }

    public void Dispose()
    {
        if (Registered) NativeMethods.UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(HandleMessage);
    }
}
