using System.Runtime.InteropServices;
using System.Windows.Interop;
using QuickLaunch.Interop;
using QuickLaunch.Models;

namespace QuickLaunch.Services;

public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly HwndSource _source;
    private readonly Action _pressed;
    private readonly HotkeyBinding _binding;
    private bool _disposed;
    public bool Registered => _binding.Registered;
    public int Error => _binding.Error;
    public HotkeyConfig? Current => _binding.Current;

    public HotkeyService(nint handle, Action pressed, HotkeyConfig? config = null)
    {
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Missing HWND.");
        _pressed = pressed;
        _binding = new(new NativeBackend(handle));
        _source.AddHook(HandleMessage);
        _binding.TryChange(config ?? new());
    }

    public bool TryChange(HotkeyConfig candidate) => _binding.TryChange(candidate);

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && Registered && wParam == _binding.ActiveId)
        {
            handled = true;
            _pressed();
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _binding.Dispose();
        _source.RemoveHook(HandleMessage);
    }

    private sealed class NativeBackend(nint handle) : IHotkeyBackend
    {
        public bool Register(int id, uint modifiers, uint key, out int error)
        {
            bool result = NativeMethods.RegisterHotKey(handle, id, modifiers, key);
            error = result ? 0 : Marshal.GetLastPInvokeError();
            return result;
        }
        public void Unregister(int id) => NativeMethods.UnregisterHotKey(handle, id);
    }
}
