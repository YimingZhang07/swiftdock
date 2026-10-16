namespace QuickLaunch.Services;

// Shell can send WM_CONTEXTMENU before or after the right-button-up callback.
// Remember the gesture across menu closure; IsOpen alone cannot reject a delayed pair.
internal sealed class TrayMenuGesture(Func<long> milliseconds, Func<int> doubleClickMilliseconds)
{
    private bool _down, _opened;
    private long? _released;

    internal void MouseDown() { _down = true; _opened = false; }

    internal bool MouseUp()
    {
        _down = false;
        _released = milliseconds();
        if (_opened) return false;
        _opened = true;
        return true;
    }

    internal bool ContextMenu() => !_down
        && (_released is not { } released || milliseconds() - released > doubleClickMilliseconds());
}
