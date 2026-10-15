using System.Windows.Threading;

namespace QuickLaunch.Diagnostics;

internal static class WindowSmoke
{
    // Drain layout, binding, Loaded/Unloaded and queued focus work even on desktops
    // that cannot grant activation or deliver a CompositionTarget.Rendering event.
    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
