using System.Windows;
using System.Windows.Controls;
using QuickLaunch.Models;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class WindowLayoutSmoke
{
    public static void Run(LauncherWindow window, Action<string, bool> check)
    {
        window.UpdateLayout();
        double width = window.ActualWidth;
        var edges = new[]
        {
            new Point(24, .25), new Point(width / 2, .25), new Point(width - 24, .25),
            new Point(.25, 26), new Point(width - .25, 26)
        };
        check("titlebar-draggable-at-top-and-side-edges", edges.All(point =>
            window.IsWindowDragSource(window.InputHitTest(point) as DependencyObject)));

        Point Center(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), window.CaptureRoot);
        var controls = new[] { window.HidePanelButton, window.ExitPanelButton, window.AddItemButton, window.SettingsButton, window.AddGroupButton };
        check("titlebar-and-toolbar-buttons-excluded-from-window-drag", controls.All(button =>
        {
            var hit = window.CaptureRoot.InputHitTest(Center(button)) as DependencyObject;
            return LauncherWindow.Ancestor<Button>(hit) == button && !window.IsWindowDragSource(hit);
        }));

        var tab = window.Tabs.Children.OfType<Button>().First();
        Point tabCenter = Center(tab);
        check("group-hit-test-survives-toolbar-layout", window.GroupAt(tabCenter) == (LaunchGroup)tab.Tag
            && !window.IsWindowDragSource(window.CaptureRoot.InputHitTest(tabCenter) as DependencyObject));

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
    }
}
