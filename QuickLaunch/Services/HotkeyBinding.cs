using System.Windows.Input;
using QuickLaunch.Models;

namespace QuickLaunch.Services;

public interface IHotkeyBackend
{
    bool Register(int id, uint modifiers, uint key, out int error);
    void Unregister(int id);
}

// Registration is transactional: never release the old binding before the candidate succeeds.
public sealed class HotkeyBinding(IHotkeyBackend backend) : IDisposable
{
    public int ActiveId { get; private set; } = 0x514C;
    public HotkeyConfig? Current { get; private set; }
    public int Error { get; private set; }
    public bool Registered => Current is not null;

    public bool TryChange(HotkeyConfig candidate)
    {
        ConfigValidation.ValidateHotkey(candidate);
        if (Current?.SameAs(candidate) == true) return true;
        int newId = ActiveId == 0x514C ? 0x514D : 0x514C;
        uint flags = 0x4000;
        foreach (var modifier in candidate.Modifiers)
            flags |= modifier switch { "Alt" => 1u, "Control" => 2u, "Shift" => 4u, "Windows" => 8u, _ => 0 };
        uint key = (uint)KeyInterop.VirtualKeyFromKey(Enum.Parse<Key>(candidate.Key));
        if (!backend.Register(newId, flags, key, out int error)) { Error = error; return false; }
        if (Registered) backend.Unregister(ActiveId);
        ActiveId = newId;
        Current = candidate.Copy();
        Error = 0;
        return true;
    }
    public void Dispose()
    {
        if (Registered) backend.Unregister(ActiveId);
        Current = null;
    }
}
