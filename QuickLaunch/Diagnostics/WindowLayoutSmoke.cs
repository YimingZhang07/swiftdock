using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Shell;
using QuickLaunch.Interop;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class WindowLayoutSmoke
{
    public static void Run(LauncherWindow window, Action<string, bool> check)
    {
        window.UpdateLayout();
        var chrome = WindowChrome.GetWindowChrome(window);
        check("window-has-resize-chrome", window.ResizeMode == ResizeMode.CanResize && chrome is not null
            && chrome.ResizeBorderThickness.Left > 0 && chrome.ResizeBorderThickness.Top > 0
            && chrome.ResizeBorderThickness.Right > 0 && chrome.ResizeBorderThickness.Bottom > 0);
        check("window-has-minimum-size", window.MinWidth > 0 && window.MinHeight > 0);
        check("single-header-no-native-caption", window.CaptureRoot.RowDefinitions.Count == 2
            && window.WindowDragArea.ActualHeight == WindowPlacement.HeaderHeight && chrome?.CaptionHeight == 0
            && chrome.GlassFrameThickness == new Thickness(-1) && window.WindowStyle == WindowStyle.None && !window.AllowsTransparency);
        double width = window.ActualWidth;
        var edges = new[]
        {
            new Point(9, 22), new Point(width - 9, 22), new Point(width / 2, 40)
        };
        check("titlebar-draggable-beyond-resize-band", edges.All(point =>
            window.IsWindowDragSource(window.InputHitTest(point) as DependencyObject)));

        Point Center(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), window.CaptureRoot);
        var controls = new[] { window.HidePanelButton, window.AddItemButton, window.SettingsButton, window.AddGroupButton };
        check("header-buttons-excluded-from-window-drag", controls.All(button =>
        {
            var hit = window.CaptureRoot.InputHitTest(Center(button)) as DependencyObject;
            return LauncherWindow.Ancestor<Button>(hit) == button && !window.IsWindowDragSource(hit);
        }));

        var tab = window.Tabs.Children.OfType<Button>().First();
        Point tabCenter = Center(tab);
        check("group-hit-test-survives-header-layout", window.GroupAt(tabCenter) == (LaunchGroup)tab.Tag
            && !window.IsWindowDragSource(window.CaptureRoot.InputHitTest(tabCenter) as DependencyObject));
        check("selected-tab-uses-attached-state", TabState.GetIsSelected(tab));
        window.HighlightDropTab((LaunchGroup)tab.Tag);
        check("tab-drop-highlight", tab.Background == window.FindResource("SelectedFillBrush"));
        window.HighlightDropTab(null);
        check("tab-drop-highlight-clears-local-value", tab.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue);
        CheckResizeDirections(window, check);

        Point content = window.Viewer.TranslatePoint(new Point(10, 10), window.CaptureRoot);
        check("icon-area-excluded-from-window-drag", window.ItemAt(content)?.Id == window.SelectedGroup.Items[0].Id
            && window.SlotAt(content) == 0 && !window.IsWindowDragSource(window.CaptureRoot.InputHitTest(content) as DependencyObject));
        check("titlebar-excluded-from-item-drop", window.SlotAt(new Point(width / 2, 2)) is null && window.GroupAt(new Point(width / 2, 2)) is null);

        window.ShowInsertion(0);
        window.UpdateLayout();
        Point marker = window.Insertion.TranslatePoint(new Point(), window.CaptureRoot);
        Point expected = window.Viewer.TranslatePoint(new Point(0, 4), window.CaptureRoot);
        check("insertion-marker-aligned-with-icon-viewport", (marker - expected).Length < .5);
        window.ShowInsertion(null);

        var size = new Size(window.Width, window.Height);
        long revision = window.ViewModel.Revision;
        var saved = window.ViewModel.WindowSize;
        window.WindowState = WindowState.Maximized;
        check("window-refuses-maximize", window.WindowState == WindowState.Normal);
        var pending = window.ViewModel.BeginImport(window.SelectedGroup, window.SelectedGroup.Items.Count, 6);
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        window.UpdateLayout();
        check("minimum-size-fits-three-columns-and-one-row", window.Viewer.ViewportWidth >= 3 * window.CellSize
            && window.Viewer.ViewportHeight >= window.CellSize - .5);
        check("thin-scrollbar-visible-with-overflow", window.Viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible
            && LauncherWindow.Descendants<System.Windows.Controls.Primitives.ScrollBar>(window.Viewer).Any(b => b.IsVisible && b.ActualWidth == WindowPlacement.ScrollBarWidth));
        Point lastColumn = window.Viewer.TranslatePoint(new Point(window.CellSize * 2.75, 10), window.CaptureRoot);
        check("resized-viewport-hit-test-uses-live-columns", window.SlotAt(lastColumn) == 3);
        window.ShowInsertion(3);
        window.UpdateLayout();
        var offset = (TranslateTransform)window.Insertion.RenderTransform;
        int columns = Math.Max(1, (int)(window.Viewer.ViewportWidth / window.CellSize));
        check("resized-insertion-marker-wraps-with-viewport", Math.Abs(offset.X - 3 % columns * window.CellSize) < .5
            && Math.Abs(offset.Y - (3 / columns * window.CellSize + 4)) < .5);
        window.ShowInsertion(null);
        window.ViewModel.CancelImport(window.SelectedGroup, pending);
        check("programmatic-size-does-not-save", window.ViewModel.Revision == revision && window.ViewModel.WindowSize == saved);
        window.Width = size.Width; window.Height = size.Height;
        window.UpdateLayout();
    }

    internal static void CheckResizeDirections(LauncherWindow window, Action<string, bool> check)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) { check("native-resize-bounds", false); return; }
        int middleX = (rect.Left + rect.Right) / 2, middleY = (rect.Top + rect.Bottom) / 2;
        var points = new[]
        {
            ("left", rect.Left + 2, middleY, 10), ("right", rect.Right - 3, middleY, 11),
            ("top", middleX, rect.Top + 2, 12), ("bottom", middleX, rect.Bottom - 3, 15),
            ("top-left", rect.Left + 2, rect.Top + 2, 13), ("top-right", rect.Right - 3, rect.Top + 2, 14),
            ("bottom-left", rect.Left + 2, rect.Bottom - 3, 16), ("bottom-right", rect.Right - 3, rect.Bottom - 3, 17)
        };
        foreach (var (name, x, y, expected) in points)
        {
            nint point = unchecked((nint)((y << 16) | (x & 0xffff)));
            check($"{window.BackdropMode}-native-resize-{name}", NativeMethods.SendMessage(hwnd, NativeMethods.WmNcHitTest, 0, point) == expected);
        }
    }
}
