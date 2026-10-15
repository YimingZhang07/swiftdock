using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

// Routed events, a controlled keyboard device, and template state only: no native input,
// foreground assertions, capture assertions, or pointer-position-dependent placement.
internal static class LauncherInputSmoke
{
    internal static WindowPlacement.Desktop Desktop { get; } = new(
        [new(new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96)], new(960, 520));

    private sealed class TestKeyboard() : KeyboardDevice(InputManager.Current)
    {
        public ModifierKeys Held { get; set; }
        protected override KeyStates GetKeyStatesFromSystem(Key key)
        {
            var modifier = key switch
            {
                Key.LeftCtrl => ModifierKeys.Control, Key.LeftShift => ModifierKeys.Shift,
                Key.LeftAlt => ModifierKeys.Alt, Key.LWin => ModifierKeys.Windows, _ => ModifierKeys.None
            };
            return modifier != ModifierKeys.None && Held.HasFlag(modifier) ? KeyStates.Down : KeyStates.None;
        }
    }

    public static async Task RunAsync(LauncherWindow main, string directory, string profileDirectory, ContextMenu? trayMenu, Action<string, bool> check)
    {
        await using var store = ConfigService.Open(Path.Combine(profileDirectory, "launcher-input"));
        var model = new MainViewModel(store);
        int launches = 0;
        var probe = new LauncherWindow(main.Log, model, main.Icons, (_, _) =>
        {
            launches++;
            return Task.FromResult(new LaunchResult(true, false, "模拟启动成功"));
        });
        probe.GetPlacementDesktop = _ => Desktop;
        try
        {
            probe.ShowPanel("input-smoke");
            await probe.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await probe.LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            CheckInput(probe, () => launches, check, directory);
            CheckSelectionNavigation(probe, check);
            CheckDragRouting(probe, () => launches, check);
            var dialogButton = new Button { Style = (Style)Application.Current.FindResource(typeof(Button)) };
            check("selection-dialog-uses-library-style", dialogButton.Style == Application.Current.FindResource(typeof(Button)));
            var tile = Tiles(probe).First();
            await CheckMenu(probe, tile.ContextMenu, tile, "item", directory, check);
            var tab = probe.Tabs.Children.OfType<Button>().First();
            // The current group menu has no separator; verify its resource scope with a temporary one.
            var separator = new Separator();
            tab.ContextMenu.Items.Add(separator);
            try { await CheckMenu(probe, tab.ContextMenu, tab, "group", directory, check); }
            finally { tab.ContextMenu.Items.Remove(separator); }
            if (trayMenu is not null) await CheckMenu(probe, trayMenu, tile, "tray", directory, check);
        }
        finally { model.Suspend(true); probe.PrepareExit(); probe.Close(); }
    }

    // Use the real show state and routed events; selection does not depend on acquiring native focus.
    // The only replaced boundaries are the OS pointer position/capture, never selection or focus.
    private sealed class Input(LauncherWindow window)
    {
        private readonly TestKeyboard _keyboard = new();
        public void Layout()
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
        }
        public KeyEventArgs Key(Key key, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false, bool up = false)
        {
            _keyboard.Held = modifiers;
            var e = new KeyEventArgs(_keyboard, PresentationSource.FromVisual(window), 0, key)
                { RoutedEvent = up ? Keyboard.PreviewKeyUpEvent : Keyboard.PreviewKeyDownEvent };
            if (repeat) typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e, [true]);
            var target = window.SelectedList;
            target.RaiseEvent(e);
            if (!e.Handled)
            {
                e.RoutedEvent = up ? Keyboard.KeyUpEvent : Keyboard.KeyDownEvent;
                target.RaiseEvent(e);
            }
            Layout();
            return e;
        }
        public void Mouse(UIElement target, MouseButton button = MouseButton.Left, bool up = false)
        {
            var e = new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, button)
                { RoutedEvent = up ? System.Windows.Input.Mouse.PreviewMouseUpEvent : System.Windows.Input.Mouse.PreviewMouseDownEvent };
            target.RaiseEvent(e);
            if (!e.Handled)
            {
                e.RoutedEvent = up ? System.Windows.Input.Mouse.MouseUpEvent : System.Windows.Input.Mouse.MouseDownEvent;
                target.RaiseEvent(e);
            }
            Layout();
        }
    }

    internal static void CheckInput(LauncherWindow probe, Func<int> launches, Action<string, bool> check, string? directory = null)
    {
        var input = new Input(probe);
        input.Layout();
        var first = probe.SelectedGroup;
        check("selection-fresh-show-is-empty-and-list-is-logical-focus", probe.SelectedList.SelectedItem is null
            && FocusManager.GetFocusedElement(probe) == probe.SelectedList);
        foreach (bool forDrop in new[] { false, true })
        foreach (var key in new[] { Key.Right, Key.Down, Key.Left, Key.Up, Key.Home, Key.End, Key.PageUp, Key.PageDown })
        {
            probe.ShowPanel("input-fresh-show", forDrop); input.Layout();
            check($"selection-show-drop-{forDrop}-{key}-clears", probe.SelectedList.SelectedItem is null);
            check($"selection-first-drop-{forDrop}-{key}-selects-zero", input.Key(key).Handled && probe.SelectedList.SelectedIndex == 0);
            check($"selection-second-right-drop-{forDrop}-after-{key}-selects-one", input.Key(Key.Right).Handled && probe.SelectedList.SelectedIndex == 1);
        }
        foreach (var target in new UIElement[] { probe.SelectedList, probe.Viewer, probe.GroupHost }
            .Concat(LauncherWindow.Descendants<ScrollContentPresenter>(probe.Viewer)))
        {
            input.Mouse(target);
            check($"selection-empty-click-{target.GetType().Name}-clears", probe.SelectedList.SelectedItem is null);
            check($"selection-after-empty-click-{target.GetType().Name}-starts-at-zero", input.Key(Key.Right).Handled && probe.SelectedList.SelectedIndex == 0);
            input.Key(Key.Right);
        }

        var tile = Tiles(probe).ElementAt(1);
        var drag = probe.DragSession;
        var oldPoint = drag.PointerPosition; var oldCapture = drag.CapturePointer;
        try
        {
            drag.PointerPosition = _ => tile.TranslatePoint(new Point(12, 12), probe.CaptureRoot);
            drag.CapturePointer = () => true;
            int before = launches();
            input.Mouse(tile);
            check("selection-click-down-clears-and-presses-container", probe.SelectedList.SelectedItem is null && tile.IsPressed && drag.Active
                && ((Border)tile.Template.FindName("Surface", tile)).Background == probe.FindResource("SubtleFillColorTertiaryBrush"));
            input.Mouse(probe.Root, up: true);
            check("selection-click-tile-two-launches-once-and-stays-clear", launches() == before + 1
                && probe.SelectedList.SelectedItem is null && !tile.IsPressed && !drag.Active);
            input.Mouse(probe.Root, up: true);
            check("selection-second-mouse-up-does-not-launch", launches() == before + 1);
        }
        finally { drag.Cancel(); drag.PointerPosition = oldPoint; drag.CapturePointer = oldCapture; }

        int noSelectionLaunches = launches();
        int count = first.Items.Count;
        check("selection-enter-without-selection-no-op", input.Key(Key.Enter).Handled && launches() == noSelectionLaunches);
        check("selection-delete-f2-without-selection-no-op", input.Key(Key.Delete).Handled && input.Key(Key.F2).Handled
            && !probe.HasOpenEditor && first.Items.Count == count);
        input.Key(Key.Right);
        var selected = probe.SelectedList.SelectedItem;
        foreach (var alt in new[] { Key.LeftAlt, Key.RightAlt })
        {
            input.Key(alt, ModifierKeys.Alt); input.Key(alt, up: true);
            foreach (long command in new long[] { 0xF100, 0xF10F })
            {
                bool handled = false;
                nint result = probe.WindowMessage(0, 0x0112, (nint)command, 0, ref handled);
                check($"selection-{alt}-system-menu-{command:X}-handled", handled && result == 0 && probe.SelectedList.SelectedItem == selected);
            }
        }
        check("selection-arrow-after-alt-still-moves", input.Key(Key.Right).Handled && probe.SelectedList.SelectedIndex == 1);
        input.Key(Key.Enter);
        input.Key(Key.Enter, repeat: true); input.Key(Key.Enter, repeat: true);
        check("selection-enter-launches-once-despite-repeat", launches() == noSelectionLaunches + 1);
        foreach (int message in new[] { 0x0127, 0x0128 })
        {
            bool handled = false;
            probe.WindowMessage(0, message, 0x30002, 0, ref handled);
            check($"selection-ui-state-{message:X}-suppressed", handled && probe.SelectedList.SelectedIndex == 1);
        }
        foreach (var (message, command) in new[] { (0x0112, 0xF060), (0x0312, 0), (0, 0) })
        {
            bool handled = false;
            probe.WindowMessage(0, message, command, 0, ref handled);
            check($"selection-unrelated-message-{message:X}-passes-through", !handled);
        }

        var tiles = Tiles(probe).ToArray();
        foreach (var entry in tiles)
        {
            var ring = (Border)entry.Template.FindName("SelectionRing", entry);
            check($"selection-ring-{probe.SelectedList.ItemContainerGenerator.IndexFromContainer(entry)}", ring.Visibility == (entry.IsSelected ? Visibility.Visible : Visibility.Collapsed)
                && ring.BorderThickness == new Thickness(2) && ring.CornerRadius == new CornerRadius(6)
                && ring.BorderBrush == probe.FindResource("AccentTextFillColorPrimaryBrush") && entry.FocusVisualStyle is null);
        }
        if (directory is not null)
        {
            using var scope = new ThemeSmokeScope();
            ThemeSmokeScope.Apply(true);
            probe.ApplyBackdrop(false, false); input.Layout();
            PrototypeSmoke.SaveSnapshot(probe, Path.Combine(directory, "tile-selected.png"));
        }
        input.Mouse(tiles[0], MouseButton.Right);
        check("selection-right-click-other-tile-keeps-ring", probe.SelectedList.SelectedIndex == 1);

        var menu = tiles[0].ContextMenu;
        using (var popup = new PopupSmokeScope(menu, probe))
        {
            menu.PlacementTarget = tiles[0]; popup.Open();
            check("selection-context-menu-target-is-container", menu.IsOpen && menu.PlacementTarget is ListBoxItem && probe.SelectedList.SelectedIndex == 1);
            popup.Close();
            check("selection-context-menu-closed-and-owner-reset", popup.Closed && !PopupSmokeScope.MenuIsOpen(probe));
            check("selection-context-menu-close-keeps-ring", probe.SelectedList.SelectedIndex == 1);
        }

        // Drive the actual modal rename dialog, including its initial value and item update.
        bool renameOpened = false;
        string id = ((LaunchItem)probe.SelectedList.SelectedItem!).Id;
        probe.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var dialog = probe.OwnedWindows.OfType<NameDialog>().SingleOrDefault();
            if (dialog is null) return;
            renameOpened = probe.HasOpenEditor && dialog.NameBox.Text == first.Items.Single(i => i.Id == id).Name;
            dialog.NameBox.Text = "键盘重命名"; dialog.DialogResult = true;
        });
        input.Key(Key.F2);
        check("selection-f2-renames-selected-item", renameOpened && first.Items.Single(i => i.Id == id).Name == "键盘重命名");
        input.Key(Key.Delete);
        check("selection-delete-removes-selected-item", first.Items.All(i => i.Id != id) && first.Items.Count == count - 1);
        input.Key(Key.Z, ModifierKeys.Control);
        check("selection-ctrl-z-restores-delete", first.Items.Any(i => i.Id == id) && first.Items.Count == count);

        var next = probe.Groups[1];
        var added = new LaunchItem("切组选择", "https://example.com/switch", LaunchKind.Url);
        next.Items.Add(added);
        try
        {
            input.Key(Key.Tab, ModifierKeys.Control);
            check("selection-ctrl-tab-selects-first-in-next-group", probe.SelectedGroup == next && probe.SelectedList.SelectedIndex == 0);
            input.Key(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
            check("selection-ctrl-shift-tab-selects-first-in-previous-group", probe.SelectedGroup == first && probe.SelectedList.SelectedIndex == 0);
            input.Key(Key.D2, ModifierKeys.Control);
            check("selection-ctrl-digit-selects-first", probe.SelectedGroup == next && probe.SelectedList.SelectedIndex == 0);
            probe.Tabs.Children.OfType<Button>().First(b => b.Tag == first).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            input.Layout();
            check("selection-mouse-tab-clears-and-focuses-list", probe.SelectedGroup == first && probe.SelectedList.SelectedItem is null
                && FocusManager.GetFocusedElement(probe) == probe.SelectedList);
            input.Key(Key.Down);
            check("selection-first-arrow-after-mouse-group-selects-zero", probe.SelectedList.SelectedIndex == 0);
        }
        finally { next.Items.Remove(added); }
        check("selection-menu-state-cleared-before-escape", !menu.IsOpen && !PopupSmokeScope.MenuIsOpen(probe));
        input.Key(Key.Escape);
        check("selection-escape-hides-panel", !probe.IsVisible);
        probe.ShowPanel("input-reshow", forDrop: true); input.Layout();
        check("selection-reshow-clears", probe.SelectedList.SelectedItem is null);
    }

    internal static void CheckSelectionNavigation(LauncherWindow probe, Action<string, bool> check)
    {
        var group = probe.SelectedGroup;
        var added = Enumerable.Range(0, 48).Select(i => new LaunchItem($"键盘导航 {i}", $"https://example.com/navigation/{i}", LaunchKind.Url)).ToArray();
        var size = new Size(probe.Width, probe.Height);
        var input = new Input(probe);
        void CheckTree(string phase)
        {
            var elements = LauncherWindow.Descendants<UIElement>(probe).Prepend(probe).ToArray();
            var focusable = elements.Where(e => e.Focusable).ToArray();
            check($"selection-tree-{phase}-only-lists-tabs-header-focusable", focusable.Length > 0
                && focusable.All(e => e is TileListBox || e is Button b && (b.Tag is LaunchGroup
                    || b == probe.AddGroupButton || b == probe.AddItemButton || b == probe.SettingsButton || b == probe.HidePanelButton)));
            check($"selection-tree-{phase}-no-focus-visual-styles", focusable.OfType<FrameworkElement>().All(e => e.FocusVisualStyle is null));
            check($"selection-tree-{phase}-no-focusable-tile-content", Tiles(probe).All(t => !t.Focusable
                && LauncherWindow.Descendants<UIElement>(t).All(e => !e.Focusable)));
            check($"selection-tree-{phase}-no-inner-scrollviewer", !LauncherWindow.Descendants<ScrollViewer>(probe.SelectedList).Any()
                && probe.SelectedList.SelectionMode == SelectionMode.Single && ReferenceEquals(probe.SelectedList.ItemsSource, probe.SelectedGroup.Items));
        }
        try
        {
            foreach (var item in added) group.Items.Add(item);
            probe.Height = probe.MinHeight; input.Layout();
            CheckTree("overflow");
            input.Key(Key.Right);
            int columns = Math.Max(1, (int)(probe.Viewer.ViewportWidth / probe.CellSize));
            int page = Math.Max(1, (int)(probe.Viewer.ViewportHeight / probe.CellSize)) * columns;
            check("selection-down-moves-one-row", input.Key(Key.Down).Handled && probe.SelectedList.SelectedIndex == columns);
            check("selection-up-moves-one-row", input.Key(Key.Up).Handled && probe.SelectedList.SelectedIndex == 0);
            check("selection-right-moves-one-column", input.Key(Key.Right).Handled && probe.SelectedList.SelectedIndex == 1);
            check("selection-up-at-top-keeps-column", input.Key(Key.Up).Handled && probe.SelectedList.SelectedIndex == 1);
            check("selection-left-moves-one-column", input.Key(Key.Left).Handled && probe.SelectedList.SelectedIndex == 0);
            foreach (var (key, expected) in new[] { (Key.PageDown, page), (Key.End, group.Items.Count - 1),
                (Key.PageUp, group.Items.Count - 1 - page), (Key.Home, 0) })
            {
                var e = input.Key(key);
                var tile = (ListBoxItem)probe.SelectedList.ItemContainerGenerator.ContainerFromItem(probe.SelectedList.SelectedItem);
                var bounds = new Rect(tile.TranslatePoint(new Point(), probe.Viewer), tile.RenderSize);
                check($"selection-{key}-moves-and-reveals", e.Handled && probe.SelectedList.SelectedIndex == expected
                    && bounds.Top >= -.5 && bounds.Bottom <= probe.Viewer.ViewportHeight + .5);
            }
            probe.Viewer.ScrollToTop(); input.Layout();
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent };
            probe.Viewer.RaiseEvent(wheel); input.Layout();
            check("selection-outer-viewer-wheel-scrolls", wheel.Handled && probe.Viewer.VerticalOffset > 0);
            var empty = probe.Groups.First(g => g.Items.Count == 0);
            probe.SelectGroup(empty); input.Layout();
            check("selection-empty-group-navigation-stays-empty", input.Key(Key.Right).Handled && probe.SelectedList.SelectedItem is null
                && FocusManager.GetFocusedElement(probe) == probe.SelectedList);
            CheckTree("empty-group");
        }
        finally
        {
            foreach (var item in added) group.Items.Remove(item);
            probe.SelectGroup(group); probe.Width = size.Width; probe.Height = size.Height;
            probe.Viewer.ScrollToTop();
        }
    }

    internal static void CheckDragRouting(LauncherWindow probe, Func<int> launches, Action<string, bool> check)
    {
        var input = new Input(probe);
        var source = probe.SelectedGroup;
        var order = source.Items.ToArray();
        var target = probe.Groups.First(g => g != source);
        var drag = probe.DragSession;
        var oldPoint = drag.PointerPosition; var oldCapture = drag.CapturePointer;
        Point pointer = default;
        int before = launches();
        Point Center(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), probe.Root);
        void Down(FrameworkElement tile)
        {
            pointer = Center(tile); input.Mouse(tile);
        }
        void Move(Point point)
        {
            pointer = point;
            probe.Root.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
        }
        try
        {
            drag.PointerPosition = _ => pointer; drag.CapturePointer = () => true;
            input.Layout();
            var tile = Tiles(probe).First();
            Down(tile);
            Move(probe.Viewer.TranslatePoint(new Point(probe.CellSize * 2.9, 12), probe.Root));
            check("selection-drag-preview-uses-item-containers", drag.Dragging && probe.Ghost.IsVisible && probe.Insertion.IsVisible
                && Math.Abs(tile.Opacity - .2) < .01 && probe.SelectedList.SelectedItem is null);
            input.Mouse(probe.Root, up: true);
            check("selection-drag-reorders-without-launch", source.Items[^1].Id == order[0].Id && launches() == before && !drag.Active);
            check("selection-drag-clears-preview-and-insertion", !probe.Insertion.IsVisible && Tiles(probe).All(t => Math.Abs(t.Opacity - 1) < .01));

            tile = Tiles(probe).First();
            var unchanged = source.Items.Select(i => i.Id).ToArray();
            Down(tile); Move(new Point(-20, -20)); input.Key(Key.Escape);
            check("selection-drag-escape-keeps-order", !drag.Active && source.Items.Select(i => i.Id).SequenceEqual(unchanged) && !probe.Ghost.IsVisible);

            tile = Tiles(probe).First();
            var moved = (LaunchItem)tile.DataContext;
            Down(tile); Move(Center(probe.Tabs.Children.OfType<Button>().Single(b => b.Tag == target)));
            input.Mouse(probe.Root, up: true);
            check("selection-drag-to-tab-moves-container-item", target.Items.Contains(moved) && !source.Items.Contains(moved) && launches() == before);
            probe.ViewModel.MoveItem(target, source, moved, 0); input.Layout();

            tile = Tiles(probe).First(); moved = (LaunchItem)tile.DataContext;
            Down(tile); Move(Center(probe.AddGroupButton)); input.Mouse(probe.Root, up: true);
            var created = probe.SelectedGroup;
            check("selection-drag-to-plus-creates-group", created != source && created != target && created.Items.Contains(moved)
                && probe.SelectedList.SelectedItem is null && launches() == before);
            probe.ViewModel.MoveItem(created, source, moved, 0);
            probe.ViewModel.RemoveGroup(created); probe.SelectGroup(source); input.Layout();

            var tab = probe.Tabs.Children.OfType<Button>().Single(b => b.Tag == target);
            input.Key(Key.Right); Down(tab); input.Mouse(probe.Root, up: true);
            check("selection-tab-click-through-drag-path-clears", probe.SelectedGroup == target && probe.SelectedList.SelectedItem is null
                && FocusManager.GetFocusedElement(probe) == probe.SelectedList);
            probe.SelectGroup(source); input.Layout();

            // Read-only mode still launches on release, but never turns the gesture into a drag.
            probe.ViewModel.Suspend(true);
            tile = Tiles(probe).First(); Down(tile); Move(Center(tile) + new Vector(20, 0));
            check("selection-read-only-tile-does-not-drag", drag.Active && !drag.Dragging);
            input.Mouse(probe.Root, up: true);
            check("selection-read-only-tile-click-launches-once", launches() == before + 1 && probe.SelectedList.SelectedItem is null);
        }
        finally
        {
            drag.Cancel(); drag.PointerPosition = oldPoint; drag.CapturePointer = oldCapture;
            probe.ViewModel.Suspend(false);
            probe.SelectGroup(source);
            for (int i = 0; i < order.Length; i++)
            {
                int index = source.Items.ToList().FindIndex(item => item.Id == order[i].Id);
                if (index >= 0 && index != i) source.Items.Move(index, i);
            }
        }
    }

    private static IEnumerable<TileListBoxItem> Tiles(LauncherWindow window) => LauncherWindow.Descendants<TileListBoxItem>(window.GroupHost)
        .Where(b => b.IsVisible);

    internal static async Task CheckMenu(LauncherWindow probe, ContextMenu menu, FrameworkElement target, string name, string directory, Action<string, bool> check)
    {
        var oldTarget = menu.PlacementTarget;
        var oldPlacement = menu.Placement;
        bool oldStaysOpen = menu.StaysOpen;
        using var lifetime = new PopupSmokeScope(menu, name == "tray" ? null : probe);
        try
        {
            menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom; menu.StaysOpen = true;
            lifetime.Open();
            await menu.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            PopupSmokeScope.Layout(menu);
            check($"{name}-menu-uses-wpf-ui-style", ReferenceEquals(menu.Style, menu.FindResource(typeof(ContextMenu)))
                && menu.Style.BasedOn == menu.FindResource("UiContextMenu"));
            var style = (Style)menu.FindResource(MenuItem.SeparatorStyleKey);
            check($"{name}-menu-has-separator", menu.Items.OfType<Separator>().Count() == 1);
            foreach (var separator in menu.Items.OfType<Separator>())
            {
                separator.ApplyTemplate();
                var line = LauncherWindow.Descendants<Border>(separator).Single();
                var row = menu.Items.OfType<MenuItem>().First();
                check($"{name}-menu-separator-key-and-full-width", ReferenceEquals(separator.Style, style)
                    && line.ActualHeight > 0 && line.ActualHeight <= 2
                    && separator.ActualWidth >= row.ActualWidth - 1
                    && line.ActualWidth >= row.ActualWidth - 32);
            }
            foreach (var delete in menu.Items.OfType<MenuItem>().Where(item => item.Header as string == "删除"))
                check($"{name}-delete-uses-critical-brush", delete.Foreground == menu.FindResource("SystemFillColorCriticalBrush"));
            if (name == "item")
            {
                check("item-menu-popup-child-is-menu-when-created", menu.Parent is not Popup popup || ReferenceEquals(popup.Child, menu));
                await PopupSmokeScope.SettleAnimationAsync(menu);
                PrototypeSmoke.SaveSnapshot(PopupSmokeScope.SnapshotRoot(menu), Path.Combine(directory, "item-menu.png"), includeOverflow: true);
            }
        }
        finally
        {
            lifetime.Close();
            check($"{name}-menu-closed-and-owner-reset", lifetime.Closed && !PopupSmokeScope.MenuIsOpen(probe));
            menu.PlacementTarget = oldTarget; menu.Placement = oldPlacement; menu.StaysOpen = oldStaysOpen;
        }
    }
}
