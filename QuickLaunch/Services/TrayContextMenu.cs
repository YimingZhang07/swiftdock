using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using QuickLaunch.Diagnostics;
using QuickLaunch.Interop;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace QuickLaunch.Services;

internal sealed class TrayContextMenu : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly ContextMenu _menu;
    private readonly ILogger _log;
    private readonly TrayMenuGesture _gesture = new(() => Environment.TickCount64, CursorUtilities.GetDoubleClickTime);
    private OpenRequest? _request;
    private nint _menuHwnd;
    private long _openedAt;

    private sealed record OpenRequest(bool Mouse, Point? Cursor, Point Anchor, string Overflow, string Root);

    internal TrayContextMenu(TaskbarIcon tray, ContextMenu menu, ILogger log)
    {
        _tray = tray; _menu = menu; _log = log;
        // H.NotifyIcon 2.4.1's stale cached DPI factor (fallback 1) lets WPF's real 2x scale double coordinates and clamp the menu to the screen corner.
        // Leave TaskbarIcon.ContextMenu unset so that path cannot open a second menu.
        tray.TrayRightMouseDown += OnMouseDown;
        tray.PreviewTrayContextMenuOpen += OnPreviewOpen;
        tray.TrayKeyboardContextMenu += OnKeyboardOpen;
        // Observe raw points for diagnostics only, after the library's routed hook.
        tray.TrayIcon.MessageWindow.MouseEventReceived += ObserveMouse;
        tray.TrayIcon.MessageWindow.KeyboardEventReceived += ObserveKeyboard;
        menu.Opened += OnOpened;
        menu.Closed += OnClosed;
    }

    private void OnMouseDown(object sender, RoutedEventArgs e) => _gesture.MouseDown();

    private void OnPreviewOpen(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_gesture.MouseUp()) Open(mouse: true);
    }

    private void OnKeyboardOpen(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_gesture.ContextMenu()) Open(mouse: false);
    }

    private void ObserveMouse(object? sender, MessageWindow.MouseEventReceivedEventArgs e)
    {
        if (e.MouseEvent == MouseEvent.IconRightMouseUp && _request is { Mouse: true }) LogOpened(e.Point);
    }

    private void ObserveKeyboard(object? sender, MessageWindow.KeyboardEventReceivedEventArgs e)
    {
        if (e.KeyboardEvent == KeyboardEvent.ContextMenu && _request is { Mouse: false }) LogOpened(e.Point);
    }

    private void Open(bool mouse)
    {
        if (_menu.IsOpen) return;
        Point? cursor = NativeMethods.GetCursorPos(out var nativeCursor) ? new(nativeCursor.X, nativeCursor.Y) : null;
        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(),
            Window = _tray.TrayIcon.WindowHandle, Guid = _tray.TrayIcon.Id
        };
        Rectangle? icon = NativeMethods.Shell_NotifyIconGetRect(in identifier, out var rect) == 0
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;
        var anchor = SelectAnchor(mouse, cursor, icon);
        if (anchor is null) return;

        string rootClass = "unavailable";
        if (cursor is not null)
        {
            nint root = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(nativeCursor), 2); // GA_ROOT
            var name = new char[256];
            int length = NativeMethods.GetClassName(root, name, name.Length);
            if (length > 0) rootClass = new string(name, 0, length);
        }
        string overflow = !mouse ? "unknown" : rootClass switch
        {
            "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" => "false",
            "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" => "true",
            _ => "unknown" // New ShellHost flyouts have no stable distinguishing class.
        };
        _request = new(mouse, cursor, anchor.Value, overflow, rootClass);

        // In WPF AbsolutePoint, PlacementRectangle is already in screen pixels;
        // only Horizontal/VerticalOffset are transformed from DIPs (Popup's
        // GetPlacementTargetInterestPoints). A zero-size physical rectangle keeps
        // negative monitor origins and avoids a cached/system-DPI conversion.
        // WPF handles corner selection and screen-edge clamping for both inputs.
        _menu.Placement = PlacementMode.AbsolutePoint;
        _menu.PlacementRectangle = new Rect(anchor.Value.X, anchor.Value.Y, 0, 0);
        _menu.HorizontalOffset = _menu.VerticalOffset = 0;
        // H.NotifyIcon 2.4.1 ShowContextMenu sequence: finish opening FIRST,
        // then foreground the popup (message HWND only if no popup HWND exists),
        // then raise TrayContextMenuOpen. Never activate an app window before open
        // or inside Opened: that interrupts WPF's menu-mode/foreground setup.
        _menu.IsOpen = true;
        nint hwnd = (PresentationSource.FromVisual(_menu) as HwndSource)?.Handle ?? 0;
        _ = WindowUtilities.SetForegroundWindow(hwnd != 0 ? hwnd : _tray.TrayIcon.WindowHandle);
        _tray.RaiseEvent(new RoutedEventArgs(TaskbarIcon.TrayContextMenuOpenEvent));
        // ContextMenu's initial menu-mode Focus() runs before the native foreground
        // transfer and can fail. Retry through WPF now, without activating the panel.
        if (_menu.IsOpen) _menu.Focus();
    }

    // Keyboard WM_CONTEXTMENU coordinates are not guaranteed by NOTIFYICONDATA.
    // Use the Shell's signed physical rectangle, never an undefined callback point
    // or the unrelated cursor (including if the native lookup fails).
    internal static Point? SelectAnchor(bool mouse, Point? cursor, Rectangle? icon)
    {
        if (mouse) return cursor;
        if (icon is { Width: > 0, Height: > 0 } bounds) return bounds.Location;
        return null;
    }

    private void OnOpened(object sender, RoutedEventArgs e)
    {
        _openedAt = Stopwatch.GetTimestamp();
        _menuHwnd = (PresentationSource.FromVisual(_menu) as HwndSource)?.Handle ?? 0;
    }

    private void LogOpened(Point callback)
    {
        if (_request is not { } request) return;
        _request = null;
        string bounds = NativeMethods.GetWindowRect(_menuHwnd, out var rect)
            ? $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})" : "unavailable";
        _log.TrayMenuOpened(request.Mouse ? "mouse" : "keyboard", request.Cursor?.ToString() ?? "unavailable",
            callback.ToString(), _tray.TrayIcon.Version.ToString(), request.Anchor.ToString(),
            request.Overflow, request.Root, bounds, NativeMethods.GetDpiForWindow(_menuHwnd), _menu.IsKeyboardFocusWithin);
    }

    private void OnClosed(object sender, RoutedEventArgs e)
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        var name = new char[256];
        int length = NativeMethods.GetClassName(foreground, name, name.Length);
        _log.TrayMenuClosed(Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds, _menuHwnd.ToInt64(),
            foreground.ToInt64(), length > 0 ? new string(name, 0, length) : "unavailable");
    }

    public void Dispose()
    {
        _tray.TrayRightMouseDown -= OnMouseDown;
        _tray.PreviewTrayContextMenuOpen -= OnPreviewOpen;
        _tray.TrayKeyboardContextMenu -= OnKeyboardOpen;
        _tray.TrayIcon.MessageWindow.MouseEventReceived -= ObserveMouse;
        _tray.TrayIcon.MessageWindow.KeyboardEventReceived -= ObserveKeyboard;
        _menu.IsOpen = false;
        _menu.Opened -= OnOpened;
        _menu.Closed -= OnClosed;
    }
}
