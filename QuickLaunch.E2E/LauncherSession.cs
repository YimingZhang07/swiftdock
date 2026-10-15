using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace QuickLaunch.E2E;

internal sealed partial class LauncherSession
{
    public const string PrimaryGroupId = "00000000000000000000000000000001";
    public const string BravoId = "00000000000000000000000000000012";
    private readonly ITestOutputHelper _output;
    private readonly string _repo = FindRepo();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "QuickLaunch.E2E", Guid.NewGuid().ToString("N"));
    private readonly string _test;
    private readonly List<RecordedLaunch> _expectedLaunches = [];
    private UIA3Automation _automation = null!;
    private Process? _process;
    private byte[]? _panelImage;
    private Window? _targetWindow;
    private bool _stopped;
    private Point? _cursor;

    private LauncherSession(ITestOutputHelper output, string test) { _output = output; _test = test; }
    public Window Panel { get; private set; } = null!;
    public nint PanelHandle => Panel.Properties.NativeWindowHandle.Value;
    public double Scale => Desktop.GetDpiForWindow(PanelHandle) / 96.0;
    public string ConfigFile => Path.Combine(_temp, "config", "config.json");
    private string LaunchFile => Path.Combine(_temp, "launches.jsonl");
    private string Executable => Path.Combine(_repo, "QuickLaunch", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64", "QuickLaunch.exe");

    public static void Run(ITestOutputHelper output, Action<LauncherSession> scenario,
        bool websiteIcons = false, int iconSize = 32, int windowWidth = 620,
        string? theme = null, string? backdrop = null, string? accent = null, bool silentStart = false, [CallerMemberName] string test = "")
    {
        var session = new LauncherSession(output, test);
        var failures = new List<Exception>();
        // UIA bounds and SendInput use physical pixels, including on mixed-DPI desktops.
        nint previousDpi = Desktop.SetThreadDpiAwarenessContext(-4); // PerMonitorV2, this test thread only
        try
        {
            try { session.Start(websiteIcons, iconSize, windowWidth, theme, backdrop, accent, silentStart); scenario(session); }
            catch (Exception ex) { failures.Add(ex); session.SaveFailure(ex); }
            try { session.Stop(); session.AssertLaunches(); }
            catch (Exception ex) { failures.Add(ex); session.SaveFailure(ex); }
        }
        finally
        {
            try { if (session._cursor is { } cursor) session.MoveMouse(cursor, "restore cursor"); }
            catch (Exception ex) { failures.Add(ex); session.SaveFailure(ex); }
            try
            {
                session.ReleaseAndDrainInput("between scenarios");
            }
            catch (Exception ex) { failures.Add(ex); session.SaveFailure(ex); }
            session._automation?.Dispose();
            session._process?.Dispose();
            if (previousDpi != 0) Desktop.SetThreadDpiAwarenessContext(previousDpi);
            if (failures.Count == 0 && Directory.Exists(session._temp)) Directory.Delete(session._temp, recursive: true);
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private void Start(bool websiteIcons, int iconSize, int windowWidth, string? theme, string? backdrop, string? accent, bool silentStart)
    {
        Assert.True(File.Exists(Executable), $"Release app is missing: {Executable}. Run scripts/E2E.ps1 or build QuickLaunch -c Release first.");
        Assert.True(Desktop.UnavailableReason() is null, "The interactive desktop became unavailable after test discovery.");
        ReleaseAndDrainInput("before starting instance", releaseAllUsed: true);
        Seed(websiteIcons, iconSize, windowWidth, theme, backdrop, accent);
        _automation = new UIA3Automation { TransactionTimeout = TimeSpan.FromSeconds(2), ConnectionTimeout = TimeSpan.FromSeconds(2) };
        List<string> arguments = ["--config-dir", Path.GetDirectoryName(ConfigFile)!, "--diagnostics-dir", Path.Combine(_temp, "diagnostics"),
            "--e2e-launch-log", LaunchFile];
        if (silentStart) arguments.Add("--silent");
        _process = StartProcess(arguments.ToArray());
        if (silentStart)
        {
            Eventually(WarmupCompleted, "silent startup to finish prewarming", 8000);
            AssertVisible(false);
            // Exercise the real second-process pipe path after offscreen warmup.
            using var secondary = StartProcess("--config-dir", Path.GetDirectoryName(ConfigFile)!, "--silent");
            bool forwarded = secondary.WaitForExit(8000);
            if (!forwarded)
            {
                Diagnostic("Secondary show client timed out; terminating only this test-owned client.");
                secondary.Kill(); secondary.WaitForExit(2000);
            }
            Assert.True(forwarded, "Secondary instance did not exit after forwarding show.");
            Assert.Equal(0, secondary.ExitCode);
        }
        Eventually(() =>
        {
            Assert.False(_process.HasExited, "QuickLaunch exited during startup; inspect the retained diagnostics.");
            Panel = FindVisibleWindow("QuickLaunch")!;
            return Panel is not null && Tiles().Length == 4;
        }, "the QuickLaunch panel and four seeded tiles", 8000);
        Foreground();
        ReleaseAndDrainInput("startup foreground ready");
        RememberPanel();
        AssertSelection(-1, "fresh startup");
    }

    private bool WarmupCompleted()
    {
        Assert.False(_process!.HasExited, "QuickLaunch exited during silent startup.");
        foreach (string file in Directory.GetFiles(Path.Combine(_temp, "diagnostics"), "quicklaunch-*.jsonl"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                JsonDocument entry;
                try { entry = JsonDocument.Parse(line); }
                catch (JsonException) { continue; } // A final line can still be in flight.
                using (entry)
                {
                    var root = entry.RootElement;
                    if (!root.TryGetProperty("EventId", out var eventId) || !eventId.TryGetProperty("Name", out var name)
                        || name.GetString() != "warmup.completed") continue;
                    Assert.True(root.GetProperty("completed").GetBoolean(), "Offscreen prewarm did not complete.");
                    return true;
                }
            }
        }
        return false;
    }

    private void Seed(bool websiteIcons, int iconSize, int windowWidth, string? theme, string? backdrop, string? accent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        Directory.CreateDirectory(Path.Combine(_temp, "diagnostics"));
        object Item(int id, string name)
        {
            string folder = Path.Combine(_temp, name);
            Directory.CreateDirectory(folder);
            return new { id = id.ToString("D32"), name, path = folder, kind = "Folder", icon = "glyph:E943:#2F6FDB" };
        }
        var config = new
        {
            // F12 is reserved for Windows debuggers; F11 keeps the unusual chord registerable.
            version = 1, hotkey = new { modifiers = new[] { "Control", "Alt", "Shift" }, key = "F11" },
            iconSize, columns = 6, autoFetchWebsiteIcons = websiteIcons,
            theme, backdrop, accent,
            windowWidth, windowHeight = 320, windowLeft = 80, windowTop = 80,
            lastGroupId = PrimaryGroupId,
            groups = new[]
            {
                new { id = PrimaryGroupId, name = "E2E Main", items = new[] { Item(11, "Alpha"), Item(12, "Bravo"), Item(13, "Charlie"), Item(14, "Delta") } },
                new { id = "00000000000000000000000000000002", name = "E2E Other", items = new[] { Item(15, "Echo") } }
            }
        };
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions
            { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        File.WriteAllText(LaunchFile, "");
    }

    private Process StartProcess(params string[] arguments)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = _repo };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        // Direct dotnet test also works when only the repo-local Desktop Runtime is installed.
        string runtime = Path.Combine(_repo, ".tools", "dotnet");
        if (Directory.Exists(runtime)) info.Environment["DOTNET_ROOT_X64"] = runtime;
        return Process.Start(info) ?? throw new XunitException("Could not start QuickLaunch.exe.");
    }

    private AutomationElement[] AppRoots() => _automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_process!.Id));

    private AutomationElement[] AppWindows(AutomationElement[]? roots = null)
    {
        var scopes = (roots ?? AppRoots()).ToList();
        // Supplement the UIA roots with every process HWND: owned windows need not be under
        // the launcher's UIA root. FromHandle still reads state through the real UIA provider.
        foreach (nint handle in Desktop.ProcessWindowHandles(_process!.Id))
        {
            try
            {
                var window = _automation.FromHandle(handle);
                if (window is not null) scopes.Add(window);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            { Diagnostic($"Window HWND 0x{handle:X} became unavailable during discovery: {ex.Message}"); }
        }
        var windows = new List<AutomationElement>();
        foreach (var root in scopes.Distinct())
        {
            try
            {
                if (root.ControlType == ControlType.Window) windows.Add(root);
                windows.AddRange(root.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            { Diagnostic($"Window subtree became unavailable during discovery: {ex.Message}"); }
        }
        return windows.Distinct().ToArray();
    }

    public AutomationElement ById(string id, AutomationElement? root = null)
    {
        AutomationElement? result = null;
        Eventually(() => IsVisible(result = (root ?? FindVisibleWindow("QuickLaunch"))?.FindFirstDescendant(cf => cf.ByAutomationId(id))), $"visible AutomationId '{id}'");
        return result!;
    }

    public AutomationElement Named(string name, ControlType type, AutomationElement? root = null)
    {
        AutomationElement? result = null;
        Eventually(() =>
        {
            var roots = root is null ? AppRoots() : [root];
            // Retain non-Window roots too: WPF context menus may be separate desktop popups.
            var scopes = root is null ? roots.Concat(AppWindows(roots)).Distinct() : roots;
            result = scopes.SelectMany(r => r.FindAllDescendants(cf => cf.ByName(name).And(cf.ByControlType(type))).Prepend(r))
                .FirstOrDefault(e => IsVisible(e) && e.ControlType == type && e.Name == name);
            return result is not null;
        }, $"visible {type} '{name}'");
        return result!;
    }

    public Window Dialog(string title)
    {
        Window? result = null;
        try
        {
            Eventually(() => (result = FindVisibleWindow(title)) is not null,
                $"dialog '{title}'");
        }
        catch (XunitException)
        {
            SaveWindowDiagnostics(title);
            throw;
        }
        _targetWindow = result;
        return result!;
    }

    private static bool VisibilityUnavailable(Exception error) => error is PropertyNotSupportedException or ElementNotAvailableException
        || error is COMException { HResult: unchecked((int)0x80040201) or unchecked((int)0x80010108) or unchecked((int)0x800401FD) };

    private static bool IsVisible(AutomationElement? element)
    {
        try { return element is not null && !element.IsOffscreen && !element.BoundingRectangle.IsEmpty; }
        catch (Exception ex) when (VisibilityUnavailable(ex)) { return false; }
    }

    private Window? FindVisibleWindow(string title)
    {
        // Re-enumerate on every poll: hiding/showing can invalidate the previous UIA element.
        try
        {
            foreach (var element in AppWindows())
            {
                try
                {
                    if (IsVisible(element) && element.Name == title) return element.AsWindow();
                }
                catch (Exception ex) when (VisibilityUnavailable(ex)) { }
            }
        }
        catch (Exception ex) when (VisibilityUnavailable(ex)) { }
        return null;
    }

    public bool HasWindow(string title)
    {
        var window = FindVisibleWindow(title);
        if (title == "QuickLaunch" && window is not null) Panel = window;
        return window is not null;
    }
    public bool HasNamed(string name, ControlType type) => AppRoots().Any(root =>
        root.FindAllDescendants(cf => cf.ByName(name).And(cf.ByControlType(type))).Any(IsVisible));

    public void Chord(VirtualKeyShort modifier, VirtualKeyShort key)
    {
        Foreground();
        try { PressKey(modifier); PressKey(key); }
        finally { ReleaseKey(key); ReleaseKey(modifier); }
    }

    public bool HasMenuItem(string name) => AppRoots().Any(r => r.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(name))).Any(IsVisible));
    public bool HasTab(string name) => Panel.FindAllDescendants(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name))).Any(IsVisible);
    public AutomationElement[] Tiles() => Panel.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Where(IsVisible).ToArray();
    public string[] TileNames() => Tiles().Select(e => e.Name).ToArray();

    public void AssertTileListFocused() => Eventually(() => Panel.FindAllDescendants(cf => cf.ByControlType(ControlType.List))
        .Any(list => IsVisible(list) && list.Properties.HasKeyboardFocus.Value), "selected group's tile list to have keyboard focus");

    public void AssertSelection(int index, string phase = "scenario")
    {
        string lastTiles = "No successful UIA selection read.", lastSelected = "unknown";
        try
        {
            Eventually(() =>
            {
                var tiles = Tiles().Select((tile, i) => new { Index = i, tile.Name, Selected = tile.Patterns.SelectionItem.Pattern.IsSelected.Value }).ToArray();
                var selected = tiles.Where(tile => tile.Selected).ToArray();
                lastTiles = JsonSerializer.Serialize(tiles);
                lastSelected = selected.Length == 0 ? "none" : string.Join(", ", selected.Select(tile => $"{tile.Index}: {tile.Name}"));
                return (phase != "fresh startup" || tiles.Length == 4)
                    && selected.Select(tile => tile.Index).SequenceEqual(index < 0 ? [] : new[] { index });
            }, $"{phase} SelectionItemPattern selection = {index} (−1 means none)");
        }
        catch (XunitException ex)
        {
            throw new XunitException($"{ex.Message}\nSelected tiles: {lastSelected}\nLast UIA snapshot: {lastTiles}"
                + $"\nKeys/buttons down now: {DescribeDownInputs()}\nRecent harness inputs (including prior scenario):\n{RecentInputs()}");
        }
    }

    public void AssertVisible(bool visible) => Eventually(() => HasWindow("QuickLaunch") == visible, $"panel visible = {visible}");

    public void Foreground(Window? window = null)
    {
        Window? target = null;
        Eventually(() => IsVisible(target = window ?? FindVisibleWindow("QuickLaunch")), "a visible window before sending input");
        if (window is null) Panel = target!;
        _targetWindow = target;
        nint handle = target!.Properties.NativeWindowHandle.Value;
        string name = target.Name;
        var retry = Stopwatch.StartNew();
        long nextAttempt = 0;
        int attempts = 0;
        bool pulsedAlt = false;
        Eventually(() =>
        {
            if (Desktop.GetForegroundWindow() == handle) return true;
            if (retry.ElapsedMilliseconds < nextAttempt) return false;
            nextAttempt = retry.ElapsedMilliseconds + 200;
            target = _automation.FromHandle(handle).AsWindow();
            if (!IsVisible(target)) return false;
            _targetWindow = target;
            if (window is null) Panel = target;
            if (attempts++ == 0) target.SetForeground();
            else
            {
                // Windows can deny startup activation. A standalone Alt press releases
                // the foreground lock; never add Alt to a held key/button (e.g. Enter).
                // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-locksetforegroundwindow
                if (!pulsedAlt && Enumerable.Range(1, 254).All(key => (Desktop.GetAsyncKeyState(key) & 0x8000) == 0))
                {
                    try { PressKey(VirtualKeyShort.ALT, "foreground unlock"); }
                    finally { ReleaseKey(VirtualKeyShort.ALT, "foreground unlock finally"); }
                    pulsedAlt = true;
                    Desktop.SetForegroundWindow(handle);
                }
                if (Desktop.GetForegroundWindow() != handle) target.Focus();
            }
            return Desktop.GetForegroundWindow() == handle;
        }, $"foreground HWND {handle} ({name}); refusing to type into another application", 3000);
    }

    public void AssertForeground() => Eventually(() => PanelHandle == Desktop.GetForegroundWindow(), "panel to remain foreground", 1000);

    public void DeactivateAndAssertVisible()
    {
        using var guard = new HotkeyInputWindow();
        Foreground(_automation.FromHandle(guard.Handle).AsWindow());
        AssertVisible(true);
    }

    public void Hotkey(bool panelHidden = false)
    {
        using var guard = panelHidden ? new HotkeyInputWindow() : null;
        var target = guard is null ? Panel : _automation.FromHandle(guard.Handle).AsWindow();
        if (guard is not null)
        {
            Desktop.GetWindowThreadProcessId(guard.Handle, out uint process);
            Assert.Equal((uint)Environment.ProcessId, process);
        }
        var held = new Stack<VirtualKeyShort>();
        try
        {
            foreach (var key in new[] { VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.F11 })
            {
                Foreground(target);
                held.Push(key);
                PressKey(key);
            }
        }
        finally { while (held.TryPop(out var key)) ReleaseKey(key); }
        AssertVisible(panelHidden);
    }

    public void Key(VirtualKeyShort key, Window? window = null)
    {
        Foreground(window);
        try { PressKey(key); }
        finally { ReleaseKey(key); } // Always release our held key, even if focus changes as a result of the key.
    }

    public void HoldEnter()
    {
        Foreground();
        int before = Launches().Length;
        try
        {
            PressKey(VirtualKeyShort.RETURN);
            Eventually(() => Launches().Length > before, "the initial Enter launch");
            // SendInput does not synthesize typematic for a single down. Repeated downs with no up
            // exercise WPF IsRepeat deterministically, independent of the user's repeat-rate setting.
            for (int i = 0; i < 12; i++)
            {
                Foreground();
                PressKey(VirtualKeyShort.RETURN);
                Thread.Sleep(50);
            }
        }
        finally { ReleaseKey(VirtualKeyShort.RETURN); }
    }

    public void SetText(string text, Window dialog, AutomationElement field)
    {
        Foreground(dialog);
        // Exercise the actual text control/TextChanged without starting an IME composition.
        field.Patterns.Value.Pattern.SetValue(text);
        Eventually(() => field.Patterns.Value.Pattern.Value.Value == text, $"text control value '{text}'");
    }

    public static Point Center(Rectangle rectangle) => new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
    public void Click(AutomationElement element, Window? window = null, MouseButton button = MouseButton.Left) => Click(Center(element.BoundingRectangle), window, button);

    public void Click(Point point, Window? window = null, MouseButton button = MouseButton.Left)
    {
        Foreground(window);
        SaveCursor();
        MoveMouse(point);
        AssertAppAt(point);
        try { PressMouse(button); }
        finally { ReleaseMouse(button); }
    }

    public void Drag(Point from, Point to)
    {
        Foreground();
        SaveCursor();
        MoveMouse(from);
        AssertAppAt(from);
        try
        {
            PressMouse(MouseButton.Left);
            Thread.Sleep(80);
            for (int step = 1; step <= 15; step++)
            {
                AssertForeground();
                MoveMouse(new Point(from.X + (to.X - from.X) * step / 15, from.Y + (to.Y - from.Y) * step / 15));
                Thread.Sleep(20);
            }
            Thread.Sleep(180); // Allow the insertion preview/native move loop to consume the final point.
        }
        finally { ReleaseMouse(MouseButton.Left); }
    }

    private void SaveCursor() => _cursor ??= Mouse.Position;
    private void AssertAppAt(Point point)
    {
        Desktop.GetWindowThreadProcessId(Desktop.WindowFromPoint(point), out uint process);
        Assert.True(process == _process!.Id, $"The input point {point} is covered or outside QuickLaunch; refusing to click another application.");
    }

    public void ExpectLaunch(string id, string name, bool elevated = false)
    {
        _expectedLaunches.Add(new(id, name, elevated));
        Eventually(() => Launches().Length >= _expectedLaunches.Count, $"launch record for {name}");
        AssertLaunches();
    }

    private RecordedLaunch[] Launches()
    {
        if (!File.Exists(LaunchFile)) return [];
        // The app writes one flushed JSON line per launch; permit reading while it appends.
        using var stream = new FileStream(LaunchFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        return text.Split('\n').SkipLast(1).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<RecordedLaunch>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).ToArray();
    }

    private void AssertLaunches() => Assert.Equal(_expectedLaunches.ToArray(), Launches());

    public JsonDocument ExitAndReadConfig()
    {
        Stop();
        return JsonDocument.Parse(File.ReadAllText(ConfigFile));
    }

    private void Stop()
    {
        if (_process is null || _stopped) return;
        Exception? inputFailure = null;
        try { ReleaseAndDrainInput("before --exit", releaseAllUsed: true); }
        catch (Exception ex) { inputFailure = ex; } // Still shut down this instance if cleanup failed.
        RememberPanel();
        if (_process.HasExited)
        {
            _stopped = true;
            throw new XunitException($"QuickLaunch exited unexpectedly (code {_process.ExitCode}) before --exit.");
        }
        using var request = StartProcess("--exit", "--config-dir", Path.GetDirectoryName(ConfigFile)!);
        bool acknowledged = request.WaitForExit(6000);
        bool exited = _process.WaitForExit(8000);
        _stopped = true;
        if (!acknowledged || !exited)
        {
            var failure = new XunitException("Graceful --exit timed out. Last-resort Kill was required; this is a test failure (check for a ghost tray icon).");
            SaveFailure(failure);
            // Never use FlaUI Application.Dispose/Close as an implicit process termination policy.
            if (!request.HasExited) request.Kill();
            if (!_process.HasExited) _process.Kill();
            request.WaitForExit(2000);
            _process.WaitForExit(2000);
            throw failure;
        }
        Assert.True(request.ExitCode == 0, $"--exit was not acknowledged (code {request.ExitCode}).");
        Assert.True(_process.ExitCode == 0, $"QuickLaunch failed during shutdown (code {_process.ExitCode}).");
        if (inputFailure is not null) throw new AggregateException("Input cleanup before --exit failed.", inputFailure);
    }

    private void RememberPanel()
    {
        try
        {
            if (_process is null || _process.HasExited) return;
            _panelImage = CaptureWindow(Panel) ?? _panelImage;
        }
        catch (Exception ex) { Diagnostic($"Panel capture unavailable: {ex.Message}"); }
    }

    private byte[]? CaptureWindow(AutomationElement? window)
    {
        try
        {
            if (window is null || window.IsOffscreen) return null;
            var bounds = window.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            using var capture = Capture.Rectangle(bounds);
            using var stream = new MemoryStream();
            capture.Bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) { Diagnostic($"Window capture unavailable: {ex.Message}"); }
        return null;
    }

    private byte[]? CaptureFailureWindow()
    {
        try
        {
            // Independent of window-tree discovery, so a lookup failure can still capture its dialog.
            nint foreground = Desktop.GetForegroundWindow();
            Desktop.GetWindowThreadProcessId(foreground, out uint process);
            if (_process is not null && !_process.HasExited && process == _process.Id)
            {
                var image = CaptureWindow(_automation.FromHandle(foreground));
                if (image is not null) return image;
            }
        }
        catch (Exception ex) { Diagnostic($"Foreground capture unavailable: {ex.Message}"); }
        return CaptureWindow(_targetWindow) ?? CaptureWindow(Panel) ?? _panelImage;
    }

    private void Diagnostic(string message)
    {
        try { _output.WriteLine(message); }
        catch { /* A closed test-output sink must never replace the original test failure. */ }
    }

    private void SaveWindowDiagnostics(string title)
    {
        try
        {
            string artifacts = Path.Combine(_repo, "artifacts", "e2e");
            Directory.CreateDirectory(artifacts);
            string path = Path.Combine(artifacts, _test + "-" + Path.GetFileName(_temp) + ".windows.txt");
            using var writer = new StreamWriter(path, append: true);
            writer.WriteLine($"{DateTime.UtcNow:O}: waiting for dialog {JsonSerializer.Serialize(title)}; process {_process?.Id}; foreground HWND 0x{Desktop.GetForegroundWindow():X}");
            try
            {
                var windows = AppWindows(); // Include offscreen windows in the diagnostic inventory.
                writer.WriteLine($"UIA Window elements: {windows.Length}");
                foreach (var window in windows)
                {
                    writer.WriteLine("Window: " + DescribeElement(window));
                    try
                    {
                        var parent = window.Parent;
                        int depth = 0;
                        while (parent is not null && depth < 32)
                        {
                            writer.WriteLine($"  Parent {++depth}: {DescribeElement(parent)}");
                            parent = parent.Parent;
                        }
                        if (parent is not null) writer.WriteLine("  Parent chain truncated after 32 ancestors.");
                    }
                    catch (Exception ex) { writer.WriteLine($"  Parent chain unavailable: {ex.Message}"); }
                }
            }
            catch (Exception ex) { writer.WriteLine($"Window enumeration failed: {ex}"); }
            Diagnostic($"Dialog-timeout window inventory: {path}");
        }
        catch (Exception ex) { Diagnostic($"Could not save window inventory: {ex.Message}"); }
    }

    private static string DescribeElement(AutomationElement element)
    {
        static string Read(Func<string> property)
        {
            try { return property(); }
            catch (Exception ex) { return $"<unavailable: {ex.Message}>"; }
        }
        return $"name={Read(() => JsonSerializer.Serialize(element.Name))}; class={Read(() => JsonSerializer.Serialize(element.ClassName))}; "
            + $"automationId={Read(() => JsonSerializer.Serialize(element.AutomationId))}; hwnd={Read(() => $"0x{element.Properties.NativeWindowHandle.Value:X}")}; "
            + $"type={Read(() => element.ControlType.ToString())}; pid={Read(() => element.Properties.ProcessId.Value.ToString())}; offscreen={Read(() => element.IsOffscreen.ToString())}";
    }

    private void SaveFailure(Exception error)
    {
        try
        {
            string artifacts = Path.Combine(_repo, "artifacts", "e2e");
            string name = _test + "-" + Path.GetFileName(_temp);
            string directory = Path.Combine(artifacts, name);
            string screenshot = Path.Combine(artifacts, name + ".png");
            Directory.CreateDirectory(directory);
            string report = Path.Combine(directory, "failure.txt");
            File.AppendAllText(report, error + Environment.NewLine + $"Retained profile/diagnostics: {_temp}" + Environment.NewLine
                + "Recent harness inputs:" + Environment.NewLine + RecentInputs() + Environment.NewLine);
            // Keep the first failure's live dialog image if shutdown later reports another failure.
            if (!File.Exists(screenshot))
            {
                var image = CaptureFailureWindow();
                if (image is not null) File.WriteAllBytes(screenshot, image);
                else File.AppendAllText(report, "No window screenshot was available; see capture diagnostics in test output." + Environment.NewLine);
            }
            RememberPanel();
            // Preserve the existing per-test panel artifact as additional context.
            if (_panelImage is not null) File.WriteAllBytes(Path.Combine(directory, "panel.png"), _panelImage);
            Diagnostic($"Failure screenshot: {screenshot}; report: {report}; retained temp data: {_temp}");
        }
        catch (Exception ex) { Diagnostic($"Could not save failure artifacts: {ex.Message}. Original failure: {error}"); }
    }

    public static void Eventually(Func<bool> condition, string expected, int timeoutMs = 3000)
    {
        var watch = Stopwatch.StartNew();
        Exception? last = null;
        do
        {
            try { if (condition()) return; }
            catch (Exception ex) when (VisibilityUnavailable(ex) || ex is COMException) { last = ex; }
            Thread.Sleep(40);
        } while (watch.ElapsedMilliseconds < timeoutMs);
        throw new XunitException($"Timed out after {timeoutMs} ms waiting for {expected}. {last?.Message}");
    }

    private static string FindRepo()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "QuickLaunch.slnx"))) return directory.FullName;
        throw new XunitException("Cannot locate QuickLaunch.slnx above the test output. Run the E2E project from its repository checkout.");
    }

    private sealed record RecordedLaunch(string Id, string Name, bool Elevated);
}
