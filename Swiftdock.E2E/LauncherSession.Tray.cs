using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Sdk;

namespace Swiftdock.E2E;

internal sealed partial class LauncherSession
{
    private const string TestTrayHotkeySuffix = "· Control+Alt+Shift+F11";
    private string TestTrayTooltip => (_englishUi ? "Swiftdock" : "迅启") + " " + TestTrayHotkeySuffix;
    private static readonly string[] ShellProcesses = ["explorer", "ShellHost", "ShellExperienceHost"];
    private sealed class TrayUnavailableException(string message) : Exception(message);
    private sealed record TrayWindow(nint Handle, uint Pid, string Process, long Started, string Class, bool Visible, int? Cloaked)
    {
        public bool IsShell => ShellProcesses.Contains(Process, StringComparer.OrdinalIgnoreCase);
        public bool Available => IsShell && Visible && Cloaked == 0;
        public bool SameWindow(TrayWindow other) => (Handle, Pid, Started, Class) == (other.Handle, other.Pid, other.Started, other.Class);
        public override string ToString() => $"HWND=0x{Handle:X} class={JsonSerializer.Serialize(Class)} PID={Pid} process={Process} visible={Visible} cloaked={Cloaked?.ToString() ?? "unknown"}";
    }
    private sealed record TrayTarget(AutomationElement Element, TrayWindow Root, string Step);

    private static bool TryReadTrayProperty<T>(Func<T> read, out T value)
    {
        try { value = read(); return true; }
        catch (Exception error) when (error is PropertyNotSupportedException || TransientUiaFailure(error))
        { value = default!; return false; }
    }

    private static TrayWindow ReadTrayWindow(nint handle)
    {
        Desktop.GetWindowThreadProcessId(handle, out uint pid);
        string name = "<unavailable>"; long started = 0;
        try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; started = process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return new(handle, pid, name, started, Desktop.WindowClass(handle), Desktop.IsWindowVisible(handle), Desktop.WindowCloaking(handle));
    }

    private static Dictionary<nint, TrayWindow> ShellWindowSnapshot()
    {
        // Include hidden/cloaked HWNDs in the baseline to detect visibility changes.
        var pids = new HashSet<int>();
        foreach (string name in ShellProcesses)
        foreach (var process in Process.GetProcessesByName(name))
        { using (process) pids.Add(process.Id); }
        return Desktop.TopLevelWindowHandles().Where(handle =>
        {
            Desktop.GetWindowThreadProcessId(handle, out uint pid);
            return pids.Contains((int)pid);
        }).Select(ReadTrayWindow).Where(window => window.IsShell).ToDictionary(window => window.Handle);
    }

    public void AssertTrayToggleIfReachable(bool languageOnly = false)
    {
        var taskbar = ReadTrayWindow(Desktop.FindWindow("Shell_TrayWnd", null));
        Dictionary<nint, TrayWindow>? beforeChevron = null;
        TrayTarget? flyoutIcon = null;
        int interval = checked((int)Desktop.GetDoubleClickTime());
        nint panel = PanelHandle;
        string step = "taskbar icon";
        bool menuAttempted = false, menuInputSent = false;
        var observedNames = new Dictionary<nint, string>();

        TrayWindow[] NewVisibleShellWindows(bool cleanup = false) => beforeChevron is null ? [] : ShellWindowSnapshot().Values.Where(window =>
            window.Visible && (window.Cloaked == 0 || cleanup && window.Cloaked is null)
                && (!beforeChevron.TryGetValue(window.Handle, out var before)
                || !window.SameWindow(before) || !before.Visible || before.Cloaked is > 0)).ToArray();

        TrayTarget[] IconsIn(AutomationElement root, TrayWindow native, string location)
        {
            // Capture every test-hotkey name before filtering. A stale Chinese name
            // is useful evidence, but must never become a target in an English step.
            var buttons = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            var readable = new List<(AutomationElement Element, string Name)>();
            int unreadableNames = 0;
            foreach (var element in buttons)
            {
                // Optional properties and individual stale providers must not
                // prevent the remaining buttons from being inspected and logged.
                if (!TryReadTrayProperty(() => element.Properties.Name.ValueOrDefault, out var name) || name is null)
                { unreadableNames++; continue; }
                readable.Add((element, name));
            }
            var names = readable.Where(button => button.Name.Contains(TestTrayHotkeySuffix, StringComparison.Ordinal))
                .Select(button => button.Name).ToArray();
            string snapshot = $"{location}; root=[{native}]; buttons={buttons.Length}; unreadable names={unreadableNames}; matching names={JsonSerializer.Serialize(names)}";
            if (!observedNames.TryGetValue(native.Handle, out string? previous) || previous != snapshot)
            {
                observedNames[native.Handle] = snapshot;
                Diagnostic($"Tray button names: expected={JsonSerializer.Serialize(TestTrayTooltip)}; {snapshot}");
            }
            return readable.Where(button => TrayIconNameMatcher.Matches(button.Name, TestTrayTooltip))
                .Select(button => new TrayTarget(button.Element, native, location)).ToArray();
        }

        TrayTarget? FindTaskbarIcon() => Unique(QueryVisibleWindow(taskbar.Handle,
            () => IconsIn(_automation.FromHandle(taskbar.Handle), taskbar, "taskbar icon"),
            Array.Empty<TrayTarget>(), "tray taskbar/FromHandle"));

        TrayTarget? Unique(TrayTarget[] matches, bool verify = true)
        {
            if (matches.Length > 1)
            {
                foreach (var match in matches) VerifyTrayPoint(match, out _, $"ambiguous: {matches.Length} matching test-profile buttons");
                throw new TrayUnavailableException($"{step}: multiple icons match the isolated profile's tooltip.");
            }
            if (matches.Length == 0) return null;
            return !verify || VerifyTrayPoint(matches[0], out _) ? matches[0] : null;
        }

        TrayTarget? FindOverflowIcon()
        {
            var candidates = NewVisibleShellWindows();
            if (candidates.Length == 0) return null;
            var matches = new List<TrayTarget>();
            // Desktop CHILDREN only. Descend only into HWNDs which passed native
            // process, visibility, cloaking and pre/post-click identity checks.
            foreach (var native in candidates)
            {
                matches.AddRange(QueryVisibleWindow(native.Handle, () =>
                {
                    AutomationElement? root = null;
                    int skippedHandles = 0;
                    // Desktop children can include non-HWND elements. Read each
                    // handle separately instead of letting one unsupported property
                    // abort the condition-based search. Descend only into our known
                    // visible shell HWND after it has been matched exactly.
                    foreach (var child in _automation.GetDesktop().FindAllChildren())
                    {
                        if (!TryReadTrayProperty(() => child.Properties.NativeWindowHandle.ValueOrDefault, out var handle) || handle == 0)
                        { skippedHandles++; continue; }
                        if (handle != native.Handle) continue;
                        root = child; break;
                    }
                    if (root is not null) return IconsIn(root, native, "overflow icon");
                    string snapshot = $"overflow icon; root=[{native}]; UIA desktop child unavailable; skipped unreadable/non-HWND children={skippedHandles}; button names unreadable";
                    if (!observedNames.TryGetValue(native.Handle, out string? previous) || previous != snapshot)
                    {
                        observedNames[native.Handle] = snapshot;
                        Diagnostic("Tray button names: " + snapshot);
                    }
                    return [];
                }, Array.Empty<TrayTarget>(), "tray overflow/desktop child"));
            }
            // A newly opened flyout can expose its button before layout/animation
            // completes. Geometry is settled and verified immediately before input.
            return Unique(matches.ToArray(), verify: false);
        }

        TrayTarget FindChevron()
        {
            var matches = QueryVisibleWindow(taskbar.Handle, () => _automation.FromHandle(taskbar.Handle)
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button).And(
                    cf.ByClassName("SystemTray.OverflowChevron").Or(cf.ByAutomationId("SystemTrayChevron"))
                    .Or(cf.ByName("Show hidden icons")).Or(cf.ByName("显示隐藏的图标")).Or(cf.ByName("显示隐藏图标"))))
                .Select(element => new TrayTarget(element, taskbar, "chevron")).ToArray(), Array.Empty<TrayTarget>(), "tray chevron/FromHandle");
            return Unique(matches) ?? throw new TrayUnavailableException("chevron: no uniquely reachable UIA button.");
        }

        TrayTarget ReachIcon()
        {
            step = "taskbar icon";
            if (!taskbar.Available) throw new TrayUnavailableException("taskbar icon: unavailable shell root: " + taskbar);
            var icon = FindTaskbarIcon();
            if (icon is not null) return icon;
            if (beforeChevron is not null)
            {
                step = "overflow icon";
                icon = FindOverflowIcon();
                if (icon is not null) return flyoutIcon = icon;
                CloseOverflow();
            }
            // Only language checks need to account for a shell UIA name left over
            // from NIM_MODIFY. Two 3-second waits, with one verified close/reopen.
            int attempts = languageOnly ? 2 : 1;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                icon = FindTaskbarIcon();
                if (icon is not null) return icon;
                step = "chevron";
                var chevron = FindChevron();
                beforeChevron = ShellWindowSnapshot(); // Before real input, including a partially failed click.
                try { ClickTrayTarget(chevron); }
                catch (TrayUnavailableException) { beforeChevron = null; throw; } // Verification failed before any mouse press.
                step = "overflow icon";
                try
                {
                    Eventually(() =>
                    {
                        CheckAbort();
                        icon = FindTaskbarIcon() ?? FindOverflowIcon();
                        return icon is not null;
                    }, $"uniquely reachable test-profile icon named {JsonSerializer.Serialize(TestTrayTooltip)} in taskbar or new shell flyout (attempt {attempt}/{attempts})", 3000);
                    if (icon!.Step == "overflow icon") flyoutIcon = icon;
                    return icon;
                }
                catch (XunitException error)
                {
                    string evidence = $"expected={JsonSerializer.Serialize(TestTrayTooltip)}; observed: "
                        + string.Join("; ", observedNames.Values) + "; candidates: "
                        + string.Join("; ", NewVisibleShellWindows().Select(window => window.ToString()));
                    Diagnostic($"Tray name wait failed ({attempt}/{attempts}): {evidence}");
                    if (attempt == attempts) throw new TrayUnavailableException($"overflow icon: {error.Message}; {evidence}");
                    CloseOverflow();
                    Diagnostic("Reopening the verified tray flyout once to refresh its UIA name; the old language remains unacceptable.");
                }
            }
            throw new TrayUnavailableException("Tray name attempts exhausted.");
        }

        bool WaitUntilClosed(int milliseconds)
        {
            var wait = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            do
            {
                if (NewVisibleShellWindows(cleanup: true).Length > 0) quiet.Restart();
                else if (quiet.ElapsedMilliseconds >= 150) return true;
                Thread.Sleep(40);
            }
            while (wait.ElapsedMilliseconds < milliseconds);
            return false;
        }

        void CloseOverflow()
        {
            if (beforeChevron is null) return;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var visible = NewVisibleShellWindows(cleanup: true);
                if (visible.Length == 0) break;
                // Escape only to the confirmed flyout, or a new shell host which
                // already owns foreground. Unidentified candidates use the chevron.
                var target = visible.FirstOrDefault(window => flyoutIcon is not null && window.SameWindow(flyoutIcon.Root))
                    ?? visible.FirstOrDefault(window => window.Handle == Desktop.GetForegroundWindow());
                if (target is not null)
                {
                    Desktop.SetForegroundWindow(target.Handle);
                    var wait = Stopwatch.StartNew();
                    while (Desktop.GetForegroundWindow() != target.Handle && wait.ElapsedMilliseconds < 750) Thread.Sleep(40);
                    if (Desktop.GetForegroundWindow() == target.Handle && ReadTrayWindow(target.Handle).SameWindow(target))
                    {
                        try { PressKey(VirtualKeyShort.ESCAPE, "close tray flyout"); }
                        finally { ReleaseKey(VirtualKeyShort.ESCAPE, "close tray flyout finally"); }
                    }
                }
                if (_cursor is { } cursor) MoveMouse(cursor, "move away from tray tooltips");
                if (!WaitUntilClosed(500))
                {
                    step = "chevron";
                    var closeButton = FindChevron(); // Opening can replace the chevron's UIA element.
                    // The Escape animation may finish during the UIA lookup; do not reopen it.
                    if (NewVisibleShellWindows(cleanup: true).Length > 0) ClickTrayTarget(closeButton);
                    if (_cursor is { } saved) MoveMouse(saved, "move away after chevron cleanup");
                }
                if (WaitUntilClosed(500)) break;
            }
            Assert.True(WaitUntilClosed(2000), "Tray flyout cleanup left visible shell windows: "
                + string.Join("; ", NewVisibleShellWindows(cleanup: true).Select(window => window.ToString())));
            beforeChevron = null; flyoutIcon = null;
        }

        try
        {
            if (languageOnly)
            {
                var target = ReachIcon();
                menuAttempted = true;
                Point click = ClickTrayTarget(target, button: MouseButton.Right);
                menuInputSent = true;
                var menu = AssertStableTrayMenu(click, "language");
                foreach (string text in _englishUi
                    ? new[] { "Show Swiftdock", "Settings", "Open configuration folder", "Start at sign-in", "Exit Swiftdock" }
                    : new[] { "显示迅启", "设置", "打开配置目录", "登录时自动启动", "退出迅启" })
                    Named(text, ControlType.MenuItem, menu.Element);
                CloseTrayMenu(); CloseOverflow(); return;
            }
            ReachIcon(); CloseOverflow();
            Hotkey(); AssertVisible(false);
            ClickTrayTarget(ReachIcon());
            AssertVisible(true); AssertForeground(); AssertTileListFocused();
            Thread.Sleep(interval + 150);
            ClickTrayTarget(ReachIcon()); AssertVisible(false);
            CloseOverflow(); Hotkey(panelHidden: true);
            Thread.Sleep(interval + 150);
            // From visible, hiding leaves overflow open for the second real press.
            ClickTrayTarget(ReachIcon(), doubleClick: true);
            AssertVisible(false);
            var settled = Stopwatch.StartNew();
            Eventually(() =>
            {
                Assert.False(Desktop.IsWindowVisible(panel), "A delayed second tray toggle re-opened the panel.");
                return settled.ElapsedMilliseconds >= interval + 150;
            }, "one toggle for a real tray double-click", interval + 1200);
            Diagnostic("Tray input PASSED: single click showed, later click hid, real double-click toggled once.");

            WindowScope OpenStableMenu(string phase)
            {
                menuInputSent = false; // A newly covered icon may skip before this gesture sends input.
                var target = ReachIcon();
                menuAttempted = true;
                Point click = ClickTrayTarget(target, button: MouseButton.Right);
                menuInputSent = true; // After input, a missing/flashing menu fails instead of skipping.
                return AssertStableTrayMenu(click, phase + "/" + target.Step);
            }

            foreach (bool visible in new[] { true, false })
            {
                CloseOverflow();
                if (Desktop.IsWindowVisible(panel) != visible) Hotkey(panelHidden: visible);
                AssertVisible(visible);
                Assert.NotEqual(0, Desktop.GetWindowLong(panel, -20) & 0x00000008); // WS_EX_TOPMOST
                string phase = visible ? "visible-pinned" : "hidden";
                var menu = OpenStableMenu(phase);
                var item = Named("设置", ControlType.MenuItem, menu.Element);
                Point point = Center(item.BoundingRectangle);
                SaveCursor(); MoveMouse(point, "real tray Settings menu click");
                AssertAppAt(point);
                Assert.Equal(menu.Handle, Desktop.GetAncestor(Desktop.WindowFromPoint(point), 2));
                try { PressMouse(MouseButton.Left); } finally { ReleaseMouse(MouseButton.Left); }
                var settings = Dialog("设置");
                Click(Named("关闭", ControlType.Button, settings), settings);
                Eventually(() => !HasWindow("设置"), "tray Settings command window to close");
                Diagnostic($"Tray menu interactive PASSED: {phase}; real Settings item click opened Settings.");
            }

            CloseOverflow();
            var escapeMenu = OpenStableMenu("escape");
            // No foreground repair: Escape must reach the menu that just survived 1.5 s.
            Assert.Equal(escapeMenu.Handle, Desktop.GetForegroundWindow());
            Assert.True(Desktop.IsDiscoverableWindow(escapeMenu.Handle));
            string? FocusedItem() => QueryVisibleWindow(escapeMenu.Handle, () => escapeMenu.Element
                .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .FirstOrDefault(item => IsVisible(item) && item.Properties.HasKeyboardFocus.Value)?.Name,
                null, "tray menu/keyboard focus");
            string? firstFocused = null;
            try { PressKey(VirtualKeyShort.DOWN, "navigate tray menu"); }
            finally { ReleaseKey(VirtualKeyShort.DOWN, "navigate tray menu finally"); }
            Eventually(() => (firstFocused = FocusedItem()) is not null, "Down to focus a tray menu item");
            Assert.Equal(escapeMenu.Handle, Desktop.GetForegroundWindow());
            try { PressKey(VirtualKeyShort.DOWN, "next tray menu item"); }
            finally { ReleaseKey(VirtualKeyShort.DOWN, "next tray menu item finally"); }
            Eventually(() => FocusedItem() is { } next && next != firstFocused, "Down to move WPF focus to the next tray menu item");
            try { PressKey(VirtualKeyShort.ESCAPE, "dismiss proven-open tray menu"); }
            finally { ReleaseKey(VirtualKeyShort.ESCAPE, "dismiss proven-open tray menu finally"); }
            Eventually(() => !Desktop.IsWindowVisible(escapeMenu.Handle) && FindTrayMenu() is null,
                "proven-open tray menu to close after Escape");
            Diagnostic("Tray menu keyboard PASSED: real Down moved item focus; Escape closed the stable menu.");

            CloseOverflow();
            using (var probe = new HotkeyInputWindow())
            {
                // Prepare the inert test-owned window before opening the menu. There
                // is no foreground repair between opening and the outside click.
                Foreground(RequireVisibleWindowElement(probe.Handle, "tray outside-click fixture").AsWindow());
                var outsideMenu = OpenStableMenu("outside-click");
                Point outside = probe.ClientCenter();
                Assert.True(Desktop.GetWindowRect(outsideMenu.Handle, out var menuBounds));
                Assert.False(menuBounds.Rectangle.Contains(outside));
                SaveCursor(); MoveMouse(outside, "dismiss tray menu by real probe click");
                nint hit = Desktop.WindowFromPoint(outside);
                Desktop.GetWindowThreadProcessId(hit, out uint pid);
                Assert.Equal((uint)Environment.ProcessId, pid);
                Assert.Equal(probe.Handle, hit); // Client area of our HWND, never desktop/another app.
                Assert.Equal(outsideMenu.Handle, Desktop.GetForegroundWindow());
                Assert.True(Desktop.IsDiscoverableWindow(outsideMenu.Handle));
                int clicks = probe.MouseDownCount;
                try { PressMouse(MouseButton.Left); } finally { ReleaseMouse(MouseButton.Left); }
                Eventually(() => !Desktop.IsWindowVisible(outsideMenu.Handle) && FindTrayMenu() is null,
                    "tray context menu to close on a real outside click");
                Eventually(() => probe.MouseDownCount == clicks + 1 && Desktop.GetForegroundWindow() == probe.Handle,
                    "the test-owned probe to receive the outside click and foreground");
                Diagnostic($"Tray menu outside-click PASSED: probe HWND=0x{probe.Handle:X}, received WM_LBUTTONDOWN={probe.MouseDownCount}.");
                CloseOverflow();
            }
        }
        catch (Exception error) when (!languageOnly && !menuInputSent && (error is TrayUnavailableException or COMException or TimeoutException or InvalidOperationException))
        { Diagnostic($"SKIP tray input (remaining steps), step={step}: {error.Message.ReplaceLineEndings(" ")}"); }
        finally
        {
            try
            {
                if (menuAttempted)
                {
                    CloseTrayMenu();
                    // Also clean up if a later assertion failed after the Settings command.
                    if (!languageOnly) Eventually(() =>
                    {
                        var settings = FindVisibleWindow("设置");
                        if (settings is null) return true;
                        Click(Named("关闭", ControlType.Button, settings), settings);
                        return false;
                    }, "tray Settings cleanup");
                }
            }
            finally { CloseOverflow(); }
        }
    }

    private WindowScope AssertStableTrayMenu(Point click, string phase)
    {
        WindowScope? menu = null;
        Eventually(() => (menu = FindTrayMenu()) is not null, phase + ": tray context menu after real right-click");
        nint hwnd = menu!.Handle;
        Rectangle bounds = Rectangle.Empty;
        Eventually(() =>
        {
            Assert.True(Desktop.IsDiscoverableWindow(hwnd), phase + ": menu closed before placement settled");
            bounds = QueryVisibleWindow(hwnd, () => menu.Element.BoundingRectangle, Rectangle.Empty, "tray menu/anchor");
            return MenuTouchesClick(bounds, click);
        }, phase + $": menu corner within 24 physical px of {click} (or containing it at screen edge)", 2000);
        Assert.True(Desktop.GetWindowRect(hwnd, out var original));
        var watch = Stopwatch.StartNew();
        try
        {
            // Native popup bounds exclude WPF's slide animation. Do not reset this
            // interval on disappearance/replacement, and do not inject input or repair focus.
            do
            {
                CheckAbort();
                Assert.True(Desktop.IsDiscoverableWindow(hwnd), phase + ": tray menu closed itself");
                Assert.True(Desktop.GetWindowRect(hwnd, out var current));
                Assert.Equal(original.Rectangle, current.Rectangle);
                Assert.Equal(hwnd, Desktop.GetForegroundWindow());
                Thread.Sleep(40);
            } while (watch.ElapsedMilliseconds < 1500);
            Assert.Equal(hwnd, FindTrayMenu()?.Handle);
            Assert.True(Desktop.IsDiscoverableWindow(hwnd));
            Assert.True(Desktop.GetWindowRect(hwnd, out var final));
            Assert.Equal(original.Rectangle, final.Rectangle);
        }
        finally
        {
            Diagnostic($"Tray menu stability: {phase}; HWND=0x{hwnd:X}, click={click}, bounds={bounds}, "
                + $"native={original}, observed={watch.ElapsedMilliseconds} ms; {Desktop.FocusDescription(hwnd)}");
        }
        return menu;
    }

    private WindowScope? FindTrayMenu()
    {
        foreach (var root in AppRoots())
        {
            var menu = QueryVisibleWindow<AutomationElement?>(root.Handle, () =>
            {
                var item = root.Element.FindFirstDescendant(cf => cf.ByAutomationId("ShowSwiftdock").And(cf.ByControlType(ControlType.MenuItem)));
                if (!IsVisible(item)) return null;
                for (var parent = item!.Parent; parent is not null; parent = parent.Parent)
                {
                    if (parent.ControlType == ControlType.Menu) return parent;
                    if (parent.Equals(root.Element)) break;
                }
                return null;
            }, null, "tray menu/discovery");
            if (menu is not null) return new(root.Handle, menu);
        }
        return null;
    }

    private static bool MenuTouchesClick(Rectangle bounds, Point click)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        if (click.X >= bounds.Left && click.X <= bounds.Right && click.Y >= bounds.Top && click.Y <= bounds.Bottom) return true;
        return new[] { new Point(bounds.Left, bounds.Top), new Point(bounds.Right, bounds.Top),
            new Point(bounds.Left, bounds.Bottom), new Point(bounds.Right, bounds.Bottom) }
            .Any(corner => Math.Pow((double)corner.X - click.X, 2) + Math.Pow((double)corner.Y - click.Y, 2) <= 24 * 24);
    }

    private void CloseTrayMenu()
    {
        Eventually(() =>
        {
            var menu = FindTrayMenu();
            if (menu is null) return true;
            // Discovery is restricted to the test process. Never direct Escape to
            // an unrelated foreground application if activation is refused.
            Desktop.SetForegroundWindow(menu.Handle);
            if (Desktop.GetForegroundWindow() != menu.Handle) return false;
            try { PressKey(VirtualKeyShort.ESCAPE, "close tray context menu"); }
            finally { ReleaseKey(VirtualKeyShort.ESCAPE, "close tray context menu finally"); }
            return false;
        }, "tray context menu closed after Escape", 3000);
    }

    private bool VerifyTrayPoint(TrayTarget target, out Point point, string? refusal = null)
    {
        var errors = new List<string>();
        T Read<T>(Func<T> read, T fallback, string property)
        {
            try { return read(); }
            catch (Exception error) { errors.Add($"{property}: {error.Message}"); return fallback; }
        }
        string name = Read(() => target.Element.Name, "<unavailable>", "Name");
        int pid = Read(() => target.Element.Properties.ProcessId.Value, -1, "ProcessId");
        Rectangle? bounds = Read<Rectangle?>(() => target.Element.BoundingRectangle, null, "BoundingRectangle");
        bool? offscreen = Read<bool?>(() => target.Element.IsOffscreen, null, "IsOffscreen");
        bool? enabled = Read<bool?>(() => target.Element.IsEnabled, null, "IsEnabled");
        var root = ReadTrayWindow(target.Root.Handle);
        bool hasPoint = bounds is { Width: > 0, Height: > 0 };
        point = hasPoint ? Center(bounds!.Value) : Point.Empty;
        var hit = ReadTrayWindow(hasPoint ? Desktop.WindowFromPoint(point) : 0);
        var hitRoot = ReadTrayWindow(hasPoint ? Desktop.GetAncestor(hit.Handle, 2) : 0);
        bool expectedName = target.Step == "chevron" || TrayIconNameMatcher.Matches(name, TestTrayTooltip);
        if (!expectedName) errors.Add("test-profile tooltip no longer matches");
        if (!hasPoint) errors.Add("empty or unavailable rectangle");
        if (offscreen != false) errors.Add("element offscreen state is not false");
        if (enabled != true) errors.Add("element enabled state is not true");
        if (!root.Available) errors.Add("root is not a visible, uncloaked known-shell window");
        if (!root.SameWindow(target.Root)) errors.Add("root identity changed since discovery");
        if (!hitRoot.SameWindow(root)) errors.Add("WindowFromPoint GA_ROOT differs from the discovered root");
        if (pid != root.Pid) errors.Add($"element PID {pid} differs from root PID {root.Pid}");
        if (refusal is null && errors.Count == 0) return true;
        string reason = refusal ?? string.Join("; ", errors);
        Diagnostic(($"Tray verification FAILED step={target.Step}; reason={JsonSerializer.Serialize(reason)}; root=[{root}]; "
            + $"element PID={pid} name={JsonSerializer.Serialize(name)} rect={bounds?.ToString() ?? "unavailable"} IsOffscreen={offscreen?.ToString() ?? "unknown"} enabled={enabled?.ToString() ?? "unknown"}; "
            + $"point={point}; WindowFromPoint=[{hit}]; GA_ROOT=[{hitRoot}]").ReplaceLineEndings(" "));
        return false;
    }

    private Point ClickTrayTarget(TrayTarget target, bool doubleClick = false, MouseButton button = MouseButton.Left)
    {
        // Every opening/reopening goes through this gate. Settle once per gesture,
        // never between the two presses of a real double-click.
        if (target.Step == "overflow icon") SettleOverflowIcon(target);
        SaveCursor();
        if (!VerifyTrayPoint(target, out var point)) throw new TrayUnavailableException(target.Step + ": verification refused input.");
        MoveMouse(point, "real notification-area input");
        for (int click = 0; click < (doubleClick ? 2 : 1); click++)
        {
            // Reverify after moving and before the second press without activating the panel.
            if (!VerifyTrayPoint(target, out var current))
                throw new TrayUnavailableException(target.Step + ": target changed before mouse-down.");
            if (current != point)
            {
                VerifyTrayPoint(target, out _, $"element center moved from {point} to {current} before mouse-down");
                throw new TrayUnavailableException(target.Step + ": target moved before mouse-down.");
            }
            try { PressMouse(button); } finally { ReleaseMouse(button); }
            if (doubleClick && click == 0) Thread.Sleep((int)Math.Min(50, Desktop.GetDoubleClickTime() / 4));
        }
        return point;
    }

    private void SettleOverflowIcon(TrayTarget target)
    {
        const int timeoutMs = 2000, sampleMs = 100, requiredSamples = 3;
        var wait = Stopwatch.StartNew();
        Rectangle? previous = null, bounds = null;
        bool? offscreen = null;
        nint hitRoot = 0;
        int consecutive = 0;
        string? readError = null;
        while (wait.ElapsedMilliseconds < timeoutMs)
        {
            CheckAbort();
            try
            {
                bounds = target.Element.BoundingRectangle;
                if (wait.ElapsedMilliseconds >= timeoutMs) break;
                offscreen = target.Element.IsOffscreen;
                bool nonempty = bounds is { Width: > 0, Height: > 0 };
                hitRoot = nonempty ? Desktop.GetAncestor(Desktop.WindowFromPoint(Center(bounds.Value)), 2) : 0;
                bool ready = nonempty && offscreen == false && hitRoot == target.Root.Handle
                    && Desktop.IsDiscoverableWindow(target.Root.Handle);
                consecutive = ready ? (bounds == previous ? consecutive + 1 : 1) : 0;
                previous = ready ? bounds : null;
                readError = null;
                if (wait.ElapsedMilliseconds >= timeoutMs) break;
                if (consecutive >= requiredSamples)
                {
                    Diagnostic($"Tray overflow settled: {consecutive} samples, {wait.ElapsedMilliseconds} ms; rect={bounds}; GA_ROOT=0x{hitRoot:X}.");
                    return;
                }
            }
            catch (Exception error) when (TransientUiaFailure(error))
            {
                // This is an explicit bounded retry, not a required one-shot lookup.
                previous = null; consecutive = 0; readError = error.Message.ReplaceLineEndings(" ");
                Diagnostic($"UIA discovery retry: overflow settle; HWND=0x{target.Root.Handle:X}; "
                    + $"class={JsonSerializer.Serialize(target.Root.Class)}; {error.GetType().Name}: {readError}");
            }
            int remaining = timeoutMs - (int)wait.ElapsedMilliseconds;
            if (remaining > 0) Thread.Sleep(Math.Min(sampleMs, remaining));
        }
        string reason = $"overflow did not settle within {timeoutMs} ms: {consecutive}/{requiredSamples} stable samples; "
            + $"last rect={bounds}, IsOffscreen={offscreen}, GA_ROOT=0x{hitRoot:X}, UIA error={readError ?? "none"}";
        VerifyTrayPoint(target, out _, reason); // Preserve the full fail-closed diagnostic; never click on timeout.
        throw new TrayUnavailableException(reason);
    }
}
