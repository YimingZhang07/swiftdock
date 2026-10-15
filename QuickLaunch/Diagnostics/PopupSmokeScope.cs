using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

// Observe before requesting a popup. IsOpen=false precedes Closed (including its animation
// timer), while a rejected native popup may never raise Opened or Closed at all.
internal sealed class PopupSmokeScope : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isOpen, _settled;
    private readonly Action<bool> _setOpen;
    private readonly Action _unsubscribe;
    private bool _awaitingClosed;
    internal bool Opened { get; private set; }
    internal bool Closed => !_isOpen() && !_awaitingClosed && _settled();

    internal PopupSmokeScope(ContextMenu menu, LauncherWindow? owner = null)
    {
        _dispatcher = menu.Dispatcher; _isOpen = () => menu.IsOpen; _setOpen = value => menu.IsOpen = value;
        _settled = () => owner is null || !MenuIsOpen(owner);
        RoutedEventHandler opened = (_, _) => OnOpened(), closed = (_, _) => _awaitingClosed = false;
        menu.Opened += opened; menu.Closed += closed;
        _unsubscribe = () => { menu.Opened -= opened; menu.Closed -= closed; };
    }

    internal PopupSmokeScope(ToolTip tooltip)
    {
        _dispatcher = tooltip.Dispatcher; _isOpen = () => tooltip.IsOpen; _setOpen = value => tooltip.IsOpen = value;
        _settled = () => true;
        RoutedEventHandler opened = (_, _) => OnOpened(), closed = (_, _) => _awaitingClosed = false;
        tooltip.Opened += opened; tooltip.Closed += closed;
        _unsubscribe = () => { tooltip.Opened -= opened; tooltip.Closed -= closed; };
    }

    internal PopupSmokeScope(Popup popup)
    {
        _dispatcher = popup.Dispatcher; _isOpen = () => popup.IsOpen; _setOpen = value => popup.IsOpen = value;
        _settled = () => true;
        EventHandler opened = (_, _) => OnOpened(), closed = (_, _) => _awaitingClosed = false;
        popup.Opened += opened; popup.Closed += closed;
        _unsubscribe = () => { popup.Opened -= opened; popup.Closed -= closed; };
    }

    private void OnOpened() { Opened = true; _awaitingClosed = true; }

    internal void Open(Action? request = null)
    {
        Opened = false;
        if (request is null) _setOpen(true); else request();
        PumpUntil(() => Opened || !_isOpen(), TimeSpan.FromSeconds(3));
    }

    internal void Close(Action? request = null, TimeSpan? timeout = null)
    {
        if (request is not null) request();
        else if (_isOpen()) _setOpen(false);
        PumpUntil(() => Closed, timeout ?? TimeSpan.FromSeconds(3));
    }

    private void PumpUntil(Func<bool> complete, TimeSpan timeout)
    {
        _dispatcher.VerifyAccess();
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        bool finished = false;
        void Poll()
        {
            finished = complete();
            if (finished || watch.Elapsed >= timeout) frame.Continue = false;
        }
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => Poll();
        // Always pump at least one turn, even when native popup creation was unavailable.
        var pending = _dispatcher.BeginInvoke(DispatcherPriority.Background, Poll);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); pending.Abort(); }
        if (!finished) throw new TimeoutException("Popup did not finish opening/closing or its owner retained menu state.");
    }

    internal static bool MenuIsOpen(LauncherWindow window) =>
        (bool)typeof(LauncherWindow).GetField("_menuOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    internal static void Layout(FrameworkElement surface)
    {
        surface.ApplyTemplate();
        if (PresentationSource.FromVisual(surface) is null)
        {
            // Template/layout checks and snapshots also work without a native popup HWND.
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            surface.Arrange(new Rect(surface.DesiredSize));
        }
        surface.UpdateLayout();
    }

    internal static async Task SettleAnimationAsync(FrameworkElement surface)
    {
        // WPF-UI's popup entrance/decorator animation lasts 167ms. Pump the dispatcher
        // beyond it before capturing; foreground/capture are irrelevant to this clock.
        await Task.Delay(220);
        await surface.Dispatcher.InvokeAsync(() => Layout(surface), DispatcherPriority.Render);
    }

    internal static FrameworkElement SnapshotRoot(FrameworkElement surface) =>
        PresentationSource.FromVisual(surface)?.RootVisual as FrameworkElement ?? surface;

    public void Dispose()
    {
        try { Close(); }
        finally { _unsubscribe(); }
    }
}
