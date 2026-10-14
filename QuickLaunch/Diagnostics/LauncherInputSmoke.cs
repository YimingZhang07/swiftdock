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
            probe.ShowPanel("input-smoke", forDrop: true);
            await probe.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await probe.LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            CheckInput(probe, () => launches, check, directory);
            CheckFocusRouting(probe, check);
            var dialogButton = new Button { Style = (Style)Application.Current.FindResource(typeof(Button)) };
            check("cues-dialog-focus-style-unchanged", dialogButton.FocusVisualStyle == Application.Current.FindResource("DialogFocus"));
            var tile = Tiles(probe).First();
            await CheckMenu(tile.ContextMenu, tile, "item", directory, check);
            var tab = probe.Tabs.Children.OfType<Button>().First();
            // The current group menu has no separator; verify its resource scope with a temporary one.
            var separator = new Separator();
            tab.ContextMenu.Items.Add(separator);
            try { await CheckMenu(tab.ContextMenu, tab, "group", directory, check); }
            finally { tab.ContextMenu.Items.Remove(separator); }
            if (trayMenu is not null) await CheckMenu(trayMenu, tile, "tray", directory, check);
        }
        finally { model.Suspend(true); probe.PrepareExit(); probe.Close(); }
    }

    internal static void CheckInput(LauncherWindow probe, Func<int> launches, Action<string, bool> check, string? directory = null)
    {
        void Layout()
        {
            probe.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            probe.UpdateLayout();
        }
        probe.UpdateLayout();
        var tile = Tiles(probe).First();
        var keyboard = new TestKeyboard();
        KeyEventArgs KeyDown(Key key, ModifierKeys modifiers = ModifierKeys.None, UIElement? target = null, bool repeat = false)
        {
            keyboard.Held = modifiers;
            var e = new KeyEventArgs(keyboard, PresentationSource.FromVisual(probe), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            if (repeat) typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e, [true]);
            (target ?? tile).RaiseEvent(e);
            Layout();
            return e;
        }
        void MouseDown(MouseButton button, UIElement? target = null, bool handled = false) =>
            (target ?? probe.Root).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Handled = handled });
        Border Ring(Button button) => (Border)button.Template.FindName("FocusRing", button);

        // WPF owns this read-only DP. Set only its visual input in this isolated fixture, then
        // restore it; acquiring real keyboard focus would make the test depend on the desktop.
        var focusKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsKeyboardFocusedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var controls = new[] { tile, probe.Tabs.Children.OfType<Button>().First(), probe.SettingsButton, probe.CancelImportButton };
        var focusBefore = controls.Select(b => b.IsKeyboardFocused).ToArray();
        var focusButton = probe.FocusButton;
        probe.FocusButton = _ => false; // This fixture checks cue state; CheckFocusRouting checks focus transfers.
        try
        {
            foreach (var control in controls) { control.ApplyTemplate(); control.SetValue(focusKey, false); }
            tile.SetValue(focusKey, true);
            Layout();
            check("cues-hidden-on-show-with-focused-tile", !probe.KeyboardCues && Ring(tile).Visibility == Visibility.Collapsed);
            foreach (var key in new[] { Key.LeftAlt, Key.RightAlt, Key.System, Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin, Key.Space })
            {
                KeyDown(key, key == Key.Space ? ModifierKeys.Alt : ModifierKeys.None);
                check($"cues-ignore-{key}", !probe.KeyboardCues && Ring(tile).Visibility == Visibility.Collapsed);
            }
            foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Tab })
            {
                KeyDown(key);
                check($"cues-navigation-{key}", probe.KeyboardCues && Ring(tile).Visibility == Visibility.Visible);
                MouseDown(MouseButton.Left);
            }
            KeyDown(Key.Tab, ModifierKeys.Shift);
            check("cues-shift-tab", probe.KeyboardCues);
            foreach (var button in Enum.GetValues<MouseButton>())
            {
                KeyDown(Key.Right);
                MouseDown(button, probe.SettingsButton, handled: true);
                check($"cues-mouse-{button}-hides-even-handled", !probe.KeyboardCues && Ring(tile).Visibility == Visibility.Collapsed);
            }

            int count = probe.SelectedGroup.Items.Count;
            var delete = KeyDown(Key.Delete);
            var rename = KeyDown(Key.F2);
            check("cues-hidden-delete-f2-do-nothing", delete.Handled && rename.Handled && !probe.KeyboardCues
                && !probe.HasOpenEditor && probe.SelectedGroup.Items.Count == count);
            int before = launches();
            check("cues-first-enter-reveals-without-launch", KeyDown(Key.Enter).Handled && probe.KeyboardCues
                && Ring(tile).Visibility == Visibility.Visible && launches() == before);
            KeyDown(Key.Enter, repeat: true);
            check("cues-held-enter-does-not-launch", launches() == before);
            check("cues-second-enter-launches", KeyDown(Key.Enter).Handled && launches() == before + 1);

            foreach (var (control, name) in controls.Zip(new[] { "tile", "tab", "icon-button", "toast-link" }))
            {
                tile.SetValue(focusKey, false);
                control.SetValue(focusKey, true);
                Layout();
                var ring = Ring(control);
                check($"cues-style-{name}",
                    control.FocusVisualStyle is null && ring.Visibility == Visibility.Visible && ring.BorderThickness == new Thickness(2)
                    && ring.CornerRadius == new CornerRadius(6) && ring.BorderBrush == probe.FindResource("AccentBrush"));
                control.SetValue(focusKey, false);
                Layout();
                check($"cues-unfocused-{name}-has-no-ring", ring.Visibility == Visibility.Collapsed);
            }
            tile.SetValue(focusKey, true);
            Layout();
            if (directory is not null) PrototypeSmoke.SaveSnapshot(tile, Path.Combine(directory, "tile-focus-ring.png"));

            var first = probe.SelectedGroup;
            MouseDown(MouseButton.Left);
            KeyDown(Key.Tab, ModifierKeys.Control);
            check("cues-ctrl-tab-switches-group", probe.KeyboardCues && probe.SelectedGroup != first);
            MouseDown(MouseButton.Left);
            KeyDown(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, probe.Root);
            check("cues-ctrl-shift-tab-switches-back", probe.KeyboardCues && probe.SelectedGroup == first);
            MouseDown(MouseButton.Left);
            KeyDown(Key.D2, ModifierKeys.Control);
            check("cues-ctrl-digit-switches-group", probe.KeyboardCues && probe.SelectedGroup == probe.Groups[1]);
            KeyDown(Key.D1, ModifierKeys.Control, probe.Root);
            probe.ShowPanel("input-smoke-reset-visible", forDrop: true);
            check("cues-show-resets-visible-panel", !probe.KeyboardCues && Ring(tile).Visibility == Visibility.Collapsed);
            KeyDown(Key.Right);
            KeyDown(Key.Escape);
            check("cues-escape-hides-panel", !probe.IsVisible);
            probe.ShowPanel("input-smoke-reshow", forDrop: true);
            check("cues-reshow-resets-hidden-panel", !probe.KeyboardCues);
        }
        finally
        {
            probe.FocusButton = focusButton;
            for (int i = 0; i < controls.Length; i++) controls[i].SetValue(focusKey, focusBefore[i]);
        }
    }

    internal static void CheckFocusRouting(LauncherWindow probe, Action<string, bool> check)
    {
        var group = probe.SelectedGroup;
        var added = Enumerable.Range(0, 48).Select(i => new LaunchItem($"键盘导航 {i}", $"https://example.com/navigation/{i}", LaunchKind.Url)).ToArray();
        var size = new Size(probe.Width, probe.Height);
        var focusButton = probe.FocusButton;
        var keyboard = new TestKeyboard();
        var focusKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsKeyboardFocusedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var priorFocus = new Dictionary<UIElement, object>();
        UIElement? focused = null;
        int transfers = 0;
        void Layout()
        {
            probe.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            probe.UpdateLayout();
        }
        void SetFocus(UIElement? element)
        {
            foreach (var candidate in new[] { focused, element }.OfType<UIElement>())
                priorFocus.TryAdd(candidate, candidate.GetValue(UIElement.IsKeyboardFocusedProperty));
            focused?.SetValue(focusKey, false);
            focused = element;
            focused?.SetValue(focusKey, true);
            Layout();
        }
        KeyEventArgs KeyDown(Key key, ModifierKeys modifiers = ModifierKeys.None)
        {
            keyboard.Held = modifiers;
            var e = new KeyEventArgs(keyboard, PresentationSource.FromVisual(probe), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            (focused ?? probe).RaiseEvent(e);
            Layout();
            return e;
        }
        void MouseDown(UIElement target)
        {
            var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            target.RaiseEvent(e);
            e.RoutedEvent = Mouse.MouseDownEvent;
            target.RaiseEvent(e);
            Layout();
        }
        void CheckTree(string phase)
        {
            var styles = new[] { "TileButton", "TabButton", "IconButton", "ToastLink" }.Select(k => probe.FindResource(k)).ToArray();
            var focusable = LauncherWindow.Descendants<UIElement>(probe).Prepend(probe).Where(e => e.Focusable).ToArray();
            check($"focus-tree-{phase}-only-launcher-buttons", focusable.Length > 0
                && focusable.All(e => e is Button b && styles.Contains(b.Style) && b.FocusVisualStyle is null));
        }
        try
        {
            // Replace only the native focus acquisition boundary. Input routing, target selection,
            // remembered identity, template bindings and BringIntoView all run through production code.
            probe.FocusButton = button => { transfers++; SetFocus(button); return true; };
            foreach (var item in added) group.Items.Add(item);
            probe.Height = probe.MinHeight;
            Layout();
            CheckTree("overflow");
            var grid = LauncherWindow.Ancestor<ItemsControl>(Tiles(probe).First())!;
            var tiles = Tiles(probe).ToArray();
            var remembered = tiles[1];
            SetFocus(null);
            check("focus-navigation-from-none-selects-first", KeyDown(Key.Right).Handled && focused == tiles[0] && probe.KeyboardCues);
            check("focus-second-arrow-moves-to-next-tile", KeyDown(Key.Right).Handled && focused == remembered);
            foreach (var source in new UIElement?[] { null, probe.Viewer, grid, probe.GroupHost, probe.SettingsButton })
            foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown })
            {
                SetFocus(source);
                MouseDown(probe.GroupHost);
                int before = transfers;
                var e = KeyDown(key);
                var ring = (Border)remembered.Template.FindName("FocusRing", remembered);
                check($"focus-recovery-{source?.GetType().Name ?? "none"}-{key}", e.Handled && transfers == before + 1
                    && focused == remembered && probe.KeyboardCues && ring.Visibility == Visibility.Visible);
            }
            foreach (var target in new UIElement[] { probe.Viewer, probe.GroupHost, grid }
                .Concat(LauncherWindow.Descendants<ScrollContentPresenter>(probe.Viewer)))
            {
                KeyDown(Key.Home);
                int before = transfers;
                MouseDown(target);
                check($"focus-empty-mouse-{target.GetType().Name}-keeps-tile", focused == tiles[0] && transfers == before
                    && !probe.KeyboardCues && !target.Focusable);
            }
            foreach (bool cues in new[] { false, true })
            {
                if (cues) KeyDown(Key.Home); else MouseDown(probe.GroupHost);
                foreach (int message in new[] { 0x0127, 0x0128 })
                {
                    bool handled = false;
                    nint result = probe.WindowMessage(0, message, 0x30002, 0, ref handled); // UIS_CLEAR focus + accelerator hiding.
                    check($"focus-ui-state-{message:X4}-cues-{cues}", handled && result == 0 && probe.KeyboardCues == cues);
                }
            }
            bool otherHandled = false;
            probe.WindowMessage(0, 0x0000, 0, 0, ref otherHandled);
            check("focus-unrelated-window-message-passes-through", !otherHandled);

            KeyDown(Key.Home);
            int columns = Math.Max(1, (int)(probe.Viewer.ViewportWidth / probe.CellSize));
            check("focus-down-moves-one-row", KeyDown(Key.Down).Handled && focused == tiles[columns]);
            check("focus-up-moves-one-row", KeyDown(Key.Up).Handled && focused == tiles[0]);
            foreach (var key in new[] { Key.PageDown, Key.End, Key.PageUp, Key.Home })
            {
                var previous = focused;
                var e = KeyDown(key);
                var tile = (Button)focused!;
                var bounds = new Rect(tile.TranslatePoint(new Point(), probe.Viewer), tile.RenderSize);
                check($"focus-{key}-moves-and-reveals-tile", e.Handled && focused != previous
                    && bounds.Top >= -.5 && bounds.Bottom <= probe.Viewer.ViewportHeight + .5);
            }
            int tabTransfers = transfers;
            check("focus-tab-retains-normal-navigation", !KeyDown(Key.Tab).Handled && !KeyDown(Key.Tab, ModifierKeys.Shift).Handled
                && transfers == tabTransfers);
            probe.Viewer.ScrollToTop(); Layout();
            double offset = probe.Viewer.VerticalOffset;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent };
            probe.Viewer.RaiseEvent(wheel); Layout();
            check("focus-nonfocusable-viewer-wheel-scrolls", !probe.Viewer.Focusable && wheel.Handled && probe.Viewer.VerticalOffset > offset);

            var empty = probe.Groups.First(g => g.Items.Count == 0);
            probe.SelectGroup(empty); Layout(); SetFocus(null);
            check("focus-empty-group-selects-tab-not-container", KeyDown(Key.Right).Handled
                && focused is Button { Tag: LaunchGroup selected } && selected == empty);
            CheckTree("empty-group");
        }
        finally
        {
            probe.FocusButton = focusButton;
            foreach (var (element, value) in priorFocus) element.SetValue(focusKey, value);
            foreach (var item in added) group.Items.Remove(item);
            probe.SelectGroup(group); probe.Width = size.Width; probe.Height = size.Height;
            probe.Viewer.ScrollToTop();
        }
    }

    private static IEnumerable<Button> Tiles(LauncherWindow window) => LauncherWindow.Descendants<Button>(window.GroupHost)
        .Where(b => b.DataContext is LaunchItem && b.IsVisible);

    private static async Task CheckMenu(ContextMenu menu, Button target, string name, string directory, Action<string, bool> check)
    {
        var oldTarget = menu.PlacementTarget;
        var oldPlacement = menu.Placement;
        bool oldStaysOpen = menu.StaysOpen;
        try
        {
            menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom; menu.StaysOpen = true;
            menu.IsOpen = true;
            await menu.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            menu.UpdateLayout();
            var style = (Style)menu.FindResource(MenuItem.SeparatorStyleKey);
            check($"{name}-menu-has-separator", menu.Items.OfType<Separator>().Count() == 1);
            foreach (var separator in menu.Items.OfType<Separator>())
            {
                separator.ApplyTemplate();
                var line = LauncherWindow.Descendants<Border>(separator).Single();
                var row = menu.Items.OfType<MenuItem>().First();
                check($"{name}-menu-separator-key-and-full-width", ReferenceEquals(separator.Style, style)
                    && style.BasedOn == menu.FindResource(typeof(Separator)) && line.ActualHeight == 1
                    && separator.ActualHeight == 9 && line.Margin == new Thickness(8, 0, 8, 0)
                    && Math.Abs(line.ActualWidth - (row.ActualWidth - 16)) < .5
                    && Math.Abs(line.TranslatePoint(new Point(), separator).Y - 4) < .5
                    && line.Background == menu.FindResource("BorderBrush"));
            }
            if (name == "item")
            {
                var popup = (Popup)menu.Parent;
                check("item-menu-popup-child-is-menu", ReferenceEquals(popup.Child, menu) && menu.IsOpen);
                PrototypeSmoke.SaveSnapshot((FrameworkElement)popup.Child, Path.Combine(directory, "item-menu.png"));
            }
        }
        finally { menu.IsOpen = false; menu.PlacementTarget = oldTarget; menu.Placement = oldPlacement; menu.StaysOpen = oldStaysOpen; }
    }
}
