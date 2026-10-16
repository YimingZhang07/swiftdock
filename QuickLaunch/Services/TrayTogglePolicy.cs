using QuickLaunch.Views;
using ToggleAction = QuickLaunch.Views.LauncherWindow.ToggleAction;

namespace QuickLaunch.Services;

// Deterministic input policy only: no timers, HWNDs, focus changes or UI calls.
internal sealed class TrayTogglePolicy(Func<long> milliseconds, Func<int> doubleClickMilliseconds)
{
    internal const int RecentActivationMilliseconds = 250;
    private long? _lastAccepted, _deactivated;
    private bool _activeAtMouseDown;

    internal void Deactivated() => _deactivated = milliseconds();

    internal void MouseDown(bool active)
    {
        long now = milliseconds();
        // Explorer normally takes activation just before delivering tray mouse-down.
        // Latch here so holding the button does not age out the pre-click state.
        _activeAtMouseDown = active || _deactivated is { } lost && now - lost is >= 0 and <= RecentActivationMilliseconds;
        _deactivated = null;
    }

    internal ToggleAction Click(bool visible, bool active, bool topmost, bool ownedDialog = false,
        bool drag = false, bool menu = false, bool moving = false, bool sizeMove = false)
    {
        long now = milliseconds();
        bool wasActive = active || _activeAtMouseDown;
        _activeAtMouseDown = false;
        var action = LauncherWindow.ToggleDecision(visible, wasActive, topmost, ownedDialog, drag, menu, moving, sizeMove);
        // Owned dialogs retain the hotkey's first-priority activation guard. Only an
        // actual show/hide starts suppression; ignored clicks do not extend it.
        if (action is ToggleAction.Ignore or ToggleAction.ActivateOwnedDialog) return action;
        if (_lastAccepted is { } last && now - last < doubleClickMilliseconds()) return ToggleAction.Ignore;
        _lastAccepted = now;
        return action;
    }
}
