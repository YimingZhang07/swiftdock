using System.Diagnostics;
using System.Drawing;
using System.ComponentModel;
using System.Text.Json;
using System.Runtime.InteropServices;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace QuickLaunch.E2E;

internal sealed partial class LauncherSession
{
    public void AssertTabAccent(Color preset, string phase)
    {
        Foreground();
        SaveCursor();
        var panelBounds = Panel.BoundingRectangle;
        MoveMouse(new Point(panelBounds.Right - (int)(24 * Scale), panelBounds.Bottom - (int)(24 * Scale)), "keep cursor off sampled underline");
        Color observed = Color.Empty;
        var stable = Stopwatch.StartNew();
        Eventually(() =>
        {
            if (Desktop.GetForegroundWindow() != PanelHandle) { stable.Restart(); return false; }
            var tab = Named("E2E Main", ControlType.Button, Panel).BoundingRectangle;
            // The approved template has a centered 14x3-DIP underline, 2 DIPs
            // above the tab's bottom. Sample its interior, away from rounded ends.
            var point = new Point(tab.Left + tab.Width / 2, (int)Math.Round(tab.Bottom - 3.5 * Scale));
            AssertAppAt(point);
            using var capture = Capture.Rectangle(new Rectangle(point.X - 1, point.Y, 3, 1));
            observed = capture.Bitmap.GetPixel(1, 0);
            bool matches = Enumerable.Range(0, 3).All(x =>
            {
                var pixel = capture.Bitmap.GetPixel(x, 0);
                return pixel.GetSaturation() >= .2 && Math.Abs(pixel.GetHue() - preset.GetHue()) <= 15;
            });
            if (!matches) stable.Restart();
            return matches && stable.ElapsedMilliseconds >= 350;
        }, $"{phase}: rendered tab underline hue near {preset.GetHue():F1} degrees and saturation >= 20%", 5000);
        Diagnostic($"{phase}: tab underline #{observed.R:X2}{observed.G:X2}{observed.B:X2}, hue={observed.GetHue():F1}, saturation={observed.GetSaturation():P1}");
        string artifacts = Path.Combine(_repo, "artifacts", "e2e");
        Directory.CreateDirectory(artifacts);
        var image = CaptureWindow(Panel);
        Assert.NotNull(image);
        File.WriteAllBytes(Path.Combine(artifacts, $"{_test}-{phase}.png"), image);
    }

    public void ChangeAppearance(string theme, string backdrop)
    {
        Click(ById("SettingsButton"));
        var settings = Dialog("设置");
        NavigateSettings(settings, "Appearance");
        Click(ById(theme == "dark" ? "DarkTheme" : "LightTheme", settings), settings);
        string material = backdrop switch { "mica" => "MicaBackdrop", "solid" => "SolidBackdrop", _ => "AcrylicBackdrop" };
        Click(ById(material, settings), settings);
        AssertNativeBackdrop(settings.Properties.NativeWindowHandle.Value, backdrop, dialog: true);
        Click(Named("关闭", ControlType.Button, settings), settings);
        Eventually(() => !HasWindow("设置"), "appearance settings to close");
        Foreground();
        AssertNativeBackdrop(PanelHandle, backdrop);
    }

    public void NavigateSettings(Window settings, string page)
    {
        Click(ById($"Settings{page}Nav", settings), settings);
        ById($"Settings{page}Title", settings);
    }

    public void DragBackdropOpacity(Window settings, int percent)
    {
        var slider = ById("BackdropOpacitySlider", settings);
        Assert.True(slider.IsEnabled, "Opacity must be enabled when the material is supported.");
        Click(slider, settings); Key(VirtualKeyShort.HOME, settings);
        Eventually(() => slider.Patterns.RangeValue.Pattern.Value.Value == 0, "opacity slider at zero");
        var bounds = slider.BoundingRectangle;
        int inset = (int)Math.Round(10 * Desktop.GetDpiForWindow(settings.Properties.NativeWindowHandle.Value) / 96.0);
        var from = new Point(bounds.Left + inset, Center(bounds).Y);
        var to = new Point(from.X + (bounds.Width - 2 * inset) * percent / 100, from.Y);
        Foreground(settings); SaveCursor(); MoveMouse(from); AssertAppAt(from);
        try
        {
            PressMouse(MouseButton.Left);
            for (int step = 1; step <= 15; step++)
            {
                Assert.Equal(settings.Properties.NativeWindowHandle.Value, Desktop.GetForegroundWindow());
                MoveMouse(new Point(from.X + (to.X - from.X) * step / 15, from.Y)); Thread.Sleep(20);
            }
            Eventually(() => slider.Patterns.RangeValue.Pattern.Value.Value > 0, "opacity changes while thumb is still held");
        }
        finally { ReleaseMouse(MouseButton.Left); }
    }

    public bool SupportsMaterial(string backdrop)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return false;
        bool supported = Desktop.DwmGetWindowAttribute(PanelHandle, 38, out int material, sizeof(int)) >= 0
            && material == (backdrop == "mica" ? 2 : 3);
        if (!supported) Diagnostic($"Pixel assertions unavailable: OS reports material {material} for requested {backdrop}.");
        return supported;
    }

    private Color PanelBackgroundPixel()
    {
        var bounds = Panel.BoundingRectangle;
        // Empty area below the only tile row, away from chrome, scrollbars and shadows.
        var point = new Point(bounds.Left + bounds.Width / 2, bounds.Bottom - (int)(44 * Scale));
        AssertAppAt(point);
        using var capture = Capture.Rectangle(new Rectangle(point.X - 1, point.Y - 1, 3, 3));
        return capture.Bitmap.GetPixel(1, 1);
    }

    private static int ColorDistance(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private Color SettledBackground(nint foreground)
    {
        Color last = Color.Empty;
        var stable = Stopwatch.StartNew();
        Eventually(() =>
        {
            Assert.Equal(foreground, Desktop.GetForegroundWindow());
            var pixel = PanelBackgroundPixel();
            if (last.IsEmpty || ColorDistance(pixel, last) > 1) stable.Restart();
            last = pixel;
            return stable.ElapsedMilliseconds >= 800;
        }, "panel background to settle after DWM activation animation", 5000);
        return last;
    }

    public void AssertInactiveBackdrop(string backdrop)
    {
        var bounds = Panel.BoundingRectangle;
        using var probe = new HotkeyInputWindow(new Rectangle(bounds.Left - 24, bounds.Top - 24, bounds.Width + 48, bounds.Height + 144));
        if (SupportsMaterial(backdrop)) AssertInactiveMaterial(backdrop, probe);
        AssertPinAndInactiveHotkey(probe); // Also runs when DWM falls back to solid.
    }

    private void AssertInactiveMaterial(string backdrop, HotkeyInputWindow probe)
    {
        var inputWindow = RequireVisibleWindowElement(probe.Handle, "material fixture/FromHandle").AsWindow();
        bool checkSettingsFrame = true;
        void Keep(bool enabled)
        {
            Foreground();
            var before = checkSettingsFrame ? CapturePanelFrame("before-settings") : null;
            Click(ById("SettingsButton"));
            var settings = Dialog("设置"); NavigateSettings(settings, "Appearance");
            var toggle = ById("KeepBackdropWhenInactiveBox", settings);
            bool current = toggle.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;
            if (current != enabled) Click(toggle, settings);
            Eventually(() => (toggle.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On) == enabled, $"keep inactive = {enabled}");
            nint settingsHandle = settings.Properties.NativeWindowHandle.Value;
            Click(Named("关闭", ControlType.Button, settings), settings);
            Eventually(() => !Desktop.IsWindowVisible(settingsHandle), "settings HWND closed");
            if (before is not null) AssertPanelFrameUnchanged(before, "after-settings");
            checkSettingsFrame = false;
            Eventually(() => !HasWindow("设置"), "settings closed");
            Foreground();
        }
        var errors = new List<string>();
        var focusResults = new List<bool>();
        var activePixels = new List<Color>();
        var inactivePixels = new List<Color>();
        foreach (bool enabled in new[] { false, true })
        {
            string phase = enabled ? "ON" : "OFF control";
            string expected = enabled ? "on" : "off";
            Keep(enabled);
            activePixels.Add(SettledBackground(PanelHandle));
            DateTimeOffset since = DateTimeOffset.UtcNow;
            bool focusPassed = false;
            try
            {
                LogFocusPhase(phase + " before foreground", probe.Handle, null);
                Foreground(inputWindow);
                LogFocusPhase(phase + " after foreground", probe.Handle, null);
                Assert.True(Desktop.GetWindowRect(probe.EditHandle, out var editBounds));
                var point = Center(editBounds.Rectangle);
                SaveCursor(); MoveMouse(point);
                Desktop.GetWindowThreadProcessId(Desktop.WindowFromPoint(point), out uint process);
                Assert.Equal((uint)Environment.ProcessId, process);
                try { PressMouse(MouseButton.Left); } finally { ReleaseMouse(MouseButton.Left); }
                LogFocusPhase(phase + " after real EDIT click", probe.Handle, null);
                Eventually(() => Desktop.GetForegroundWindow() == probe.Handle && Desktop.GuiInfo().Focus == probe.EditHandle,
                    phase + ": other window owns foreground AND keyboard focus");
                probe.ClearEditText();
                string cleared = Desktop.ReadEditText(probe.EditHandle);
                Assert.True(cleared.Length == 0,
                    $"{phase}: fixture clear expected EDIT text=\"\", actual EDIT text={JsonSerializer.Serialize(cleared)}.");
                TypeOwnedText(expected); // Real Unicode SendInput; the fixture has detached its IME contexts.
                Eventually(() => Desktop.ReadEditText(probe.EditHandle) == expected, phase + ": typing reaches the other window");
                bool? panelActive = null;
                Eventually(() => (panelActive = ReadPanelActive(since, probe.Handle, enabled)) is not null,
                    phase + ": fresh panel IsActive diagnostic");
                LogFocusPhase(phase + " after typing", probe.Handle, panelActive);
                Assert.False(panelActive.GetValueOrDefault(true), phase + ": WPF panel IsActive must be false while the other window receives typing");
                focusPassed = true;
                Key(VirtualKeyShort.ESCAPE, inputWindow); AssertVisible(true);
            }
            catch (Exception error)
            {
                focusPassed = false;
                string actual;
                try { actual = JsonSerializer.Serialize(Desktop.ReadEditText(probe.EditHandle)); }
                catch (Exception readError) { actual = $"<unavailable: {readError.Message}>"; }
                string detail = $"{error.Message} Expected EDIT text={JsonSerializer.Serialize(expected)}, actual EDIT text={actual}.";
                LogFocusPhase(phase + " FAILED: " + detail, probe.Handle, ReadPanelActive(since, probe.Handle, enabled));
                errors.Add(phase + " focus: " + detail);
            }
            focusResults.Add(focusPassed);
            try { inactivePixels.Add(SettledBackground(probe.Handle)); }
            catch (Exception error) { inactivePixels.Add(Color.Empty); errors.Add(phase + " pixels: " + error.Message); }
        }
        Color active = activePixels[0], fallback = inactivePixels[0], keptActive = activePixels[1], keptInactive = inactivePixels[1];
        Diagnostic($"{backdrop}: active={active}; OFF inactive/fallback={fallback}; ON active={keptActive}; ON inactive={keptInactive}");
        // DWM chooses the inactive fallback; acrylic's need not match the theme solid.
        if (!fallback.IsEmpty && ColorDistance(active, fallback) <= 3)
            errors.Add($"OFF must visibly change from active {active}; observed inactive fallback {fallback} (distance must exceed 3).");
        if (!keptInactive.IsEmpty && (ColorDistance(keptInactive, fallback) <= 3 || ColorDistance(keptInactive, keptActive) > 3))
            errors.Add($"ON failed to retain {backdrop}: active={keptActive}, inactive={keptInactive}, fallback={fallback}.");
        string diagnosis = focusResults[0] && !focusResults[1] ? "OFF passed / ON failed: product activation regression."
            : !focusResults[0] && !focusResults[1] ? "Both OFF and ON failed: fixture/foreground path is not proven."
            : !focusResults[0] ? "OFF control failed: investigate the harness before attributing this to the product."
            : "Both OFF and ON delivered typing with panel IsActive=false.";
        Diagnostic(diagnosis);
        Assert.True(errors.Count == 0, diagnosis + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private void AssertPinAndInactiveHotkey(HotkeyInputWindow probe)
    {
        var inputWindow = RequireVisibleWindowElement(probe.Handle, "pin fixture/FromHandle").AsWindow();
        void AssertProbeFocus()
        {
            Assert.Equal(probe.Handle, Desktop.GetForegroundWindow());
            Assert.Equal(probe.EditHandle, Desktop.GuiInfo().Focus);
        }
        void TypeInProbe(string text)
        {
            Foreground(inputWindow);
            Assert.True(Desktop.GetWindowRect(probe.EditHandle, out var bounds));
            var point = Center(bounds.Rectangle);
            SaveCursor(); MoveMouse(point);
            Desktop.GetWindowThreadProcessId(Desktop.WindowFromPoint(point), out uint process);
            Assert.Equal((uint)Environment.ProcessId, process);
            try { PressMouse(MouseButton.Left); } finally { ReleaseMouse(MouseButton.Left); }
            Eventually(() => Desktop.GetForegroundWindow() == probe.Handle && Desktop.GuiInfo().Focus == probe.EditHandle,
                "test EDIT owns foreground and keyboard focus before hotkey");
            probe.ClearEditText();
            Assert.Equal("", Desktop.ReadEditText(probe.EditHandle));
            TypeOwnedText(text);
            Eventually(() => Desktop.ReadEditText(probe.EditHandle) == text, "typing reaches test EDIT before hotkey");
            AssertProbeFocus();
        }
        void HotkeyFromProbe()
        {
            AssertProbeFocus();
            // No Foreground() call here: the product alone must change visibility/focus.
            try
            {
                foreach (var key in new[] { VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.F11 })
                {
                    AssertProbeFocus();
                    PressKey(key);
                }
            }
            finally
            {
                ReleaseKey(VirtualKeyShort.F11); ReleaseKey(VirtualKeyShort.SHIFT); ReleaseKey(VirtualKeyShort.ALT); ReleaseKey(VirtualKeyShort.CONTROL);
            }
        }
        void AssertPin(bool enabled)
        {
            var pin = ById("PinButton");
            Assert.Equal("置顶", pin.Name);
            Eventually(() => pin.Patterns.Toggle.Pattern.ToggleState.Value == (enabled ? ToggleState.On : ToggleState.Off),
                $"pin TogglePattern = {enabled}");
            Eventually(() => ((Desktop.GetWindowLong(PanelHandle, -20) & 0x00000008) != 0) == enabled,
                $"native WS_EX_TOPMOST = {enabled}");
        }
        void AssertSavedPin(bool enabled) => Eventually(() =>
        {
            try
            {
                using var stream = new FileStream(ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var json = JsonDocument.Parse(stream);
                bool present = json.RootElement.TryGetProperty("alwaysOnTop", out var value);
                return enabled ? !present : present && value.ValueKind == JsonValueKind.False;
            }
            catch (IOException) { return false; }
        }, enabled ? "alwaysOnTop omitted after pin ON" : "alwaysOnTop false persisted after pin OFF");

        AssertVisible(true); AssertPin(true);
        TypeInProbe("pinned");
        HotkeyFromProbe();
        AssertVisible(false); AssertProbeFocus();
        // Do not reactivate or click the fixture after hiding: its original focus must survive.
        TypeOwnedText("hidden");
        Eventually(() =>
        {
            AssertProbeFocus();
            return Desktop.ReadEditText(probe.EditHandle) == "pinnedhidden";
        }, "one pinned hotkey hides without stealing foreground or EDIT focus");

        HotkeyFromProbe(); AssertVisible(true); AssertForeground(); AssertTileListFocused();
        Click(ById("PinButton")); AssertPin(false); AssertSavedPin(false);
        TypeInProbe("unpinned");
        HotkeyFromProbe(); AssertVisible(true); AssertForeground(); AssertTileListFocused();
        Hotkey(); AssertVisible(false);
        Hotkey(panelHidden: true); AssertForeground(); AssertTileListFocused();
        Click(ById("PinButton")); AssertPin(true); AssertSavedPin(true);
        Key(VirtualKeyShort.ESCAPE); AssertVisible(false); Hotkey(panelHidden: true);
    }

    private bool? ReadPanelActive(DateTimeOffset since, nint foreground, bool keep)
    {
        bool? active = null;
        foreach (string file in Directory.GetFiles(Path.Combine(_temp, "diagnostics"), "quicklaunch-*.jsonl").Order())
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.TryGetProperty("EventId", out var eventId) && eventId.TryGetProperty("Name", out var name)
                        && name.GetString() == "window.activation" && root.GetProperty("@t").GetDateTimeOffset() >= since
                        && root.GetProperty("foregroundHwnd").GetInt64() == foreground.ToInt64()
                        && root.GetProperty("keepBackdrop").GetBoolean() == keep)
                        active = root.GetProperty("isActive").GetBoolean();
                }
                catch (JsonException) { } // Last buffered line may still be in flight.
            }
        }
        return active;
    }

    private void LogFocusPhase(string phase, nint otherWindow, bool? panelActive)
    {
        string message = $"{DateTimeOffset.UtcNow:O} {phase}: {Desktop.FocusDescription(otherWindow)}; "
            + $"panel thread: {Desktop.FocusDescription(PanelHandle)}; panel.IsActive={panelActive?.ToString() ?? "awaiting fresh product log"}";
        Diagnostic(message);
        File.AppendAllText(Path.Combine(_temp, "focus-phases.log"), message + Environment.NewLine);
    }

    public void AssertNativeBackdrop(nint handle, string backdrop, bool dialog = false)
    {
        // DWMWA_SYSTEMBACKDROP_TYPE is available on Windows 11 22H2 and later.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;
        using var preferences = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool transparent = preferences?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        int expected = !transparent || backdrop == "solid" ? 1 : dialog || backdrop == "mica" ? 2 : 3;
        Eventually(() => Desktop.DwmGetWindowAttribute(handle, 38, out int material, sizeof(int)) >= 0 && material == expected,
            $"native {(dialog ? "dialog" : "panel")} backdrop = {expected}");
    }

    public void NotifyWindowsAppearanceChanged()
    {
        Foreground();
        // Deliver the real window messages without changing the reviewer's Windows
        // preferences. WM_SETTINGCHANGE is a pointer-bearing system message and
        // cannot use asynchronous PostMessage cross-process. No string is needed:
        // null parameters exercise the product's normal registry/theme refresh.
        const uint abortIfHung = 0x0002, timeoutMs = 2000;
        nint handle = PanelHandle;
        foreach (uint message in new uint[] { 0x001A, 0x031A, 0x0320 })
        {
            nint delivered = Desktop.SendMessageTimeout(handle, message, 0, 0, abortIfHung, timeoutMs, out nuint result);
            int error = Marshal.GetLastWin32Error(); // Capture before another interop call can replace it.
            string detail = error == 0 ? "No extended error; delivery failed or timed out." : new Win32Exception(error).Message;
            Assert.True(delivered != 0, $"SendMessageTimeoutW failed for message 0x{message:X4}, HWND 0x{handle:X}, "
                + $"SMTO_ABORTIFHUNG, timeout {timeoutMs} ms; Win32 error {error}: {detail}");
            // WndProc may legitimately return zero; the API return indicates delivery.
            Diagnostic($"Delivered appearance message 0x{message:X4} to HWND 0x{handle:X}; WndProc result 0x{result:X}.");
        }
        Assert.True(_process!.WaitForInputIdle(1000), "QuickLaunch did not process appearance notifications.");
    }
}
