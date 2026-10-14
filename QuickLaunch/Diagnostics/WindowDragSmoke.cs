using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using QuickLaunch.Models;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class WindowDragSmoke
{
    public static void Run(LauncherWindow window, Action<string, bool> check)
    {
        window.UpdateLayout();
        var tab = window.Tabs.Children.OfType<Button>().First();
        Point tabTop = tab.TranslatePoint(new Point(), window);
        Point tabBottom = tab.TranslatePoint(new Point(tab.ActualWidth, tab.ActualHeight), window);
        double resize = WindowChrome.GetWindowChrome(window).ResizeBorderThickness.Top;
        double tabsRight = window.AddGroupButton.TranslatePoint(new Point(window.AddGroupButton.ActualWidth, 0), window).X;
        double buttonsLeft = window.AddItemButton.TranslatePoint(new Point(), window).X;
        double middle = window.WindowDragArea.ActualHeight / 2;
        var points = new[]
        {
            ("empty-middle", new Point(window.ActualWidth / 2, middle)),
            ("right-of-tabs", new Point(tabsRight + 2, middle)),
            ("before-buttons", new Point(buttonsLeft - 4, middle)),
            ("between-tabs-and-buttons", new Point((tabsRight + buttonsLeft) / 2, middle)),
            ("top-strip", tabTop.Y > resize + .5
                ? new Point((tabTop.X + tabBottom.X) / 2, (resize + tabTop.Y) / 2)
                : new Point(tabsRight + 2, resize + 1)),
            ("below-tabs", new Point((tabTop.X + tabBottom.X) / 2, (tabBottom.Y + window.WindowDragArea.ActualHeight) / 2)),
            ("left-padding", new Point(9, middle)),
            ("right-padding", new Point(window.ActualWidth - 9, middle))
        };
        foreach (var (name, point) in points)
            check($"header-routed-move-{name}", point.Y > resize && RequestsMove(window, point));
        check("header-routed-move-already-handled", RequestsMove(window, points[0].Item2, handled: true));
        check("header-empty-space-grid-drags", EmptyHeaderDrags(window));
        check("header-tabs-leave-empty-space", !window.TabsViewport.Focusable && window.TabsViewport.ActualWidth < buttonsLeft - 12
            && Math.Abs(window.TabsViewport.ExtentWidth - window.TabsViewport.ViewportWidth) < .5);

        var controls = new[] { window.AddGroupButton, window.AddItemButton, window.SettingsButton, window.HidePanelButton };
        foreach (var button in controls)
        {
            Point point = Center(window, button);
            check($"header-routed-no-move-{button.Name}", LauncherWindow.Ancestor<Button>(window.InputHitTest(point) as DependencyObject) == button
                && !RequestsMove(window, point));
        }

        var move = window.StartWindowMove;
        int requests = 0;
        bool previewHandled = false;
        MouseButtonEventHandler preview = (_, e) => previewHandled = e.Handled
            && LauncherWindow.Ancestor<Button>(e.OriginalSource as DependencyObject) == tab;
        window.CaptureRoot.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, preview, handledEventsToo: true);
        window.StartWindowMove = _ => requests++;
        try
        {
            var down = RaiseMouseDown(window, Center(window, tab));
            check("header-tab-down-handled-by-drag-coordinator", previewHandled && down?.Handled == true && requests == 0);
        }
        finally
        {
            window.CaptureRoot.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, preview);
            window.DragSession.Cancel(); window.StartWindowMove = move;
        }

        window.DragSession.BeginPendingItemForDiagnostics(window.SelectedGroup.Items.First(i => !i.IsPending));
        try { check("header-no-move-during-pending-drag", !RequestsMove(window, points[0].Item2)); }
        finally { window.DragSession.Cancel(); }

        var selected = window.SelectedGroup;
        var groups = new List<LaunchGroup>();
        try
        {
            for (int i = 1; i <= 15; i++) groups.Add(window.ViewModel.AddGroup($"拖动校验分组 {i}"));
            window.UpdateLayout();
            var viewport = window.TabsViewport;
            viewport.ScrollToLeftEnd(); window.UpdateLayout();
            double before = viewport.HorizontalOffset;
            check("header-tabs-overflow-and-shrink", viewport.ExtentWidth > viewport.ViewportWidth && viewport.ViewportWidth > 0
                && viewport.TranslatePoint(new Point(viewport.ActualWidth, 0), window).X <= buttonsLeft);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = viewport };
            viewport.RaiseEvent(wheel); window.UpdateLayout();
            check("header-tabs-wheel-changes-offset", wheel.Handled && viewport.HorizontalOffset > before);
            check("header-overflow-empty-space-still-drags", EmptyHeaderDrags(window));
            window.AddGroupButton.BringIntoView(); window.UpdateLayout();
            Point add = Center(window, window.AddGroupButton);
            check("header-overflow-add-group-stays-drop-target", window.IsAddGroupAt(window.TranslatePoint(add, window.CaptureRoot))
                && !RequestsMove(window, add));
        }
        finally
        {
            foreach (var group in groups) window.ViewModel.RemoveGroup(group);
            window.SelectGroup(selected); window.UpdateLayout();
        }
    }

    internal static bool RequestsMove(LauncherWindow window, Point point, bool handled = false)
    {
        var move = window.StartWindowMove;
        int requests = 0;
        window.StartWindowMove = e =>
        {
            if (e.RoutedEvent == UIElement.MouseLeftButtonDownEvent && e.ChangedButton == MouseButton.Left
                && ReferenceEquals(e.OriginalSource, window.InputHitTest(point))) requests++;
        };
        try { return RaiseMouseDown(window, point, handled)?.Handled == true && requests == 1; }
        finally { window.StartWindowMove = move; Mouse.Capture(null); }
    }

    private static MouseButtonEventArgs? RaiseMouseDown(LauncherWindow window, Point point, bool handled = false)
    {
        if (window.InputHitTest(point) is not UIElement hit) return null;
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = hit, Handled = handled };
        hit.RaiseEvent(e);
        // WPF re-raises the direct MouseLeftButtonDown event along the MouseDown route.
        e.RoutedEvent = UIElement.MouseDownEvent;
        hit.RaiseEvent(e);
        return e;
    }

    private static bool EmptyHeaderDrags(LauncherWindow window)
    {
        int count = 0;
        bool drags = true;
        for (double x = 9; x < window.ActualWidth - 6; x += 16)
            for (double y = 7; y < window.WindowDragArea.ActualHeight; y += 5)
            {
                var point = new Point(x, y);
                if (window.InputHitTest(point) is not DependencyObject hit
                    || LauncherWindow.Ancestor<System.Windows.Controls.Primitives.ButtonBase>(hit) is not null) continue;
                count++;
                drags &= RequestsMove(window, point);
            }
        return count > 0 && drags;
    }

    private static Point Center(LauncherWindow window, FrameworkElement element) =>
        element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), window);
}
