using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickLaunch.Interop;
using QuickLaunch.Services;
using QuickLaunch.Views;
using QuickLaunch.Models;
using System.Windows.Controls;

namespace QuickLaunch.Diagnostics;

// Runs inside the actual WPF dispatcher. No synthetic keyboard input or launches of user applications.
internal static class PrototypeSmoke
{
    public static async Task<bool> RunAsync(LauncherWindow window, HotkeyService hotkey, ConfigService store, App app, string directory, bool trayAvailable)
    {
        var results = new List<object>();
        bool passed = true;
        void Check(string name, bool value)
        {
            results.Add(new { name, passed = value });
            passed &= value;
            window.Log.Write("smoke.check", new { name, passed = value });
            File.WriteAllText(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(new
            {
                passed = false, completed = false, checks = results, profileDirectory = store.DirectoryPath
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Check("tray-created", trayAvailable);
        Check("tray-uses-ico", app.Tray is { Icon: not null } && app.Tray.IconSource is not H.NotifyIcon.GeneratedIconSource);
        using (var expected = AppIcon.Load(AppIcon.TrayPixelSize()))
            Check("tray-icon-matches-taskbar-dpi", SameIcon(app.Tray?.Icon, expected));
        using (var executable = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!))
        using (var expected = AppIcon.Load(executable?.Width ?? 32))
            Check("exe-uses-app-ico", SameIcon(executable, expected));
        // A user's running launcher may own Alt+Space. Only this isolated smoke
        // profile switches to an available combination; leave that instance alone.
        if (!hotkey.Registered)
        {
            foreach (int key in Enumerable.Range(1, 12).Reverse())
            {
                var candidate = new HotkeyConfig { Modifiers = ["Control", "Alt", "Shift"], Key = $"F{key}" };
                if (!hotkey.TryChange(candidate)) continue;
                window.ViewModel.ApplySettings(candidate);
                break;
            }
        }
        Check("isolated-hotkey-registered", hotkey.Registered);
        window.ShowPanel("smoke");
        await Task.Delay(250);
        Check("visible-window-with-layout", window.IsVisible && window.ActualWidth > 0 && window.ActualHeight > 0);
        Check("two-groups", window.Groups.Count == 2);
        await window.LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check("real-frozen-icons-loaded", window.Groups[0].Items.All(i => i.Visual.Image is { IsFrozen: true }));
        await LauncherInputSmoke.RunAsync(window, directory, store.DirectoryPath, app.Tray?.ContextMenu, Check);
        await DialogBehaviorSmoke.RunAsync(window, directory, store.DirectoryPath, Check);
        await EditItemDialogSmoke.RunAsync(window, directory, store.DirectoryPath, Check);
        await IconPickerSmoke.RunAsync(window, directory, Check);
        await FaviconSmoke.RunAsync(window, directory, store.DirectoryPath, Check);
        nint handle = new WindowInteropHelper(window).Handle;
        Check("native-hwnd", handle != 0 && NativeMethods.GetWindowRect(handle, out _));
        await WindowPositionSmoke.RunAsync(window, store.DirectoryPath, Check);
        var before = window.Groups.Select(g => g.Items.Select(i => i.Id).ToArray()).ToArray();
        bool captured = window.CaptureRoot.CaptureMouse();
        Check("root-capture-acquired", captured);
        window.SelectGroup(window.Groups[1]);
        await Task.Delay(100);
        Check("capture-survives-hidden-source-group", ReferenceEquals(Mouse.Captured, window.CaptureRoot));
        window.CaptureRoot.ReleaseMouseCapture();
        Check("group-preview-does-not-mutate-items", window.Groups.Select((g, i) => g.Items.Select(x => x.Id).SequenceEqual(before[i])).All(x => x));
        window.SelectGroup(window.Groups[0]);
        window.UpdateLayout();
        window.Log.Write("smoke.layout", new { window.Width, window.ActualWidth, window.Height, window.ActualHeight, window.Viewer.ViewportWidth });
        Check("auto-width-fits-configured-columns", Math.Abs(window.ActualWidth - (window.ViewModel.Columns * window.CellSize + WindowPlacement.HorizontalChrome)) < .5);
        WindowLayoutSmoke.Run(window, Check);
        await LauncherAppearanceSmoke.RunAsync(window, directory, store.DirectoryPath, Check);
        window.HidePanel();
        Check("hide-retains-hwnd", !window.IsVisible && new WindowInteropHelper(window).Handle == handle);
        window.ShowPanel("smoke-reshow");
        await Task.Delay(150);
        Check("reshow-retains-hwnd", window.IsVisible && new WindowInteropHelper(window).Handle == handle);
        window.HidePanel();
        bool prewarmed = await window.PrewarmAsync();
        Check("prewarm-completes-offscreen-and-restores-opacity", prewarmed && !window.IsVisible && window.Opacity == 1);
        var warming = window.PrewarmAsync();
        window.ShowPanel("smoke-interrupt-warmup");
        await warming;
        Check("user-show-invalidates-prewarm-hide", window.IsVisible && window.Opacity == 1);
        Check("configuration-writable", !store.IsReadOnly);
        var group = window.Groups[0];
        app.OpenSettings();
        WindowSmoke.Pump();
        var settings = Application.Current.Windows.OfType<SettingsWindow>().Single();
        Check("settings-owned-and-visible", settings.IsVisible && settings.Owner == window && window.IsVisible);
        Check("header-routed-move-with-settings-open", WindowDragSmoke.RequestsMove(window, new Point(window.ActualWidth / 2, 22)));
        long settingsRevision = window.ViewModel.Revision;
        settings.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("settings-applied", window.ViewModel.Revision > settingsRevision);
        settings.Close(); WindowSmoke.Pump();
        Check("settings-close-keeps-main-alive", window.IsVisible && new WindowInteropHelper(window).Handle == handle);
        group.Items.Move(0, group.Items.Count - 1);
        await window.ViewModel.FlushAsync();
        var persisted = ConfigValidation.Parse(await File.ReadAllBytesAsync(store.FilePath));
        Check("reorder-persisted", persisted.Groups[0].Items.Select(i => i.Id).SequenceEqual(group.Items.Select(i => i.Id)));
        Check("latest-revision-saved", store.SavedRevision == window.ViewModel.Revision);
        Check("settings-persisted", persisted.Hotkey.SameAs(window.ViewModel.Hotkey) && store.SavedRevision == window.ViewModel.Revision);

        var editor = new EditItemDialog(null);
        var editorTimeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        editorTimeout.Tick += (_, _) => editor.Close();
        editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(() =>
        {
            WindowSmoke.Pump();
            editor.NameBox.Text = "测试网页"; editor.PathBox.Text = "https://example.com/download.exe";
            Check("url-disables-app-only-fields", !editor.ArgumentsBox.IsEnabled && !editor.WorkingBox.IsEnabled && !editor.ElevateBox.IsEnabled);
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        editorTimeout.Start();
        bool editorAccepted;
        try { editorAccepted = window.ShowEditor(editor); } finally { editorTimeout.Stop(); WindowSmoke.Pump(); }
        Check("edit-dialog-produces-valid-url", editorAccepted && editor.Result?.Kind == LaunchKind.Url && editor.Result.Name == "测试网页");

        var temporary = window.ViewModel.AddGroup("校验分组"); window.SelectGroup(temporary);
        var pending = window.ViewModel.BeginImport(temporary, 0, 2);
        var web = new LaunchItem("验证网页", "https://example.com/Validation", LaunchKind.Url);
        var imported = window.ViewModel.CompleteImport(temporary, pending, [web, web with { Id = Guid.NewGuid().ToString("N") }]);
        Check("import-deduplicates-and-removes-placeholders", imported.Added == 1 && temporary.Items.All(i => !i.IsPending));
        window.ViewModel.UpdateItem(temporary, web, web with { Name = "已重命名" });
        Check("edit-keeps-identity", temporary.Items[0].Id == web.Id && temporary.Items[0].Name == "已重命名");
        window.ViewModel.DeleteItem(temporary, temporary.Items[0]);
        Check("delete-undo-restores-item", window.ViewModel.UndoDelete() && temporary.Items[0].Id == web.Id);
        window.ViewModel.MoveGroup(window.Groups.ToList().IndexOf(temporary), 0);
        Check("group-order-updates", window.Groups[0] == temporary);
        window.ViewModel.RemoveGroup(temporary); window.SelectGroup(window.Groups[0]);
        Check("deleted-group-view-cleared", !window.Groups.Contains(temporary));
        await window.ViewModel.FlushAsync();

        bool? nativeHotkeyRollback = null;
        if (hotkey.Current is { } previous)
        {
            bool reserved = NativeMethods.RegisterHotKey(handle, 0x6001, 0x4006, 0x7A); // Ctrl+Shift+F11
            try
            {
                if (reserved)
                {
                    bool accepted = hotkey.TryChange(new HotkeyConfig { Modifiers = ["Control", "Shift"], Key = "F11" });
                    nativeHotkeyRollback = !accepted && hotkey.Current?.SameAs(previous) == true;
                    Check("native-hotkey-conflict-preserves-old", nativeHotkeyRollback.Value);
                }
            }
            finally { if (reserved) NativeMethods.UnregisterHotKey(handle, 0x6001); }
        }

        int requestsBefore = app.InstanceShowRequests;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "--config-dir", store.DirectoryPath, "--diagnostics-dir", directory, "--silent" }) start.ArgumentList.Add(argument);
        using (var secondary = Process.Start(start)!)
        {
            try { await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { secondary.Kill(); throw; }
            Check("secondary-exits-successfully", secondary.ExitCode == 0);
        }
        await Task.Delay(80);
        Check("secondary-forwards-show-request", app.InstanceShowRequests > requestsBefore && window.IsVisible);
        await WindowVisibilitySmoke.RunAsync(window, store.DirectoryPath, Check);
        var report = new
        {
            passed, completed = true, runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
            hotkeyRegistration = new { hotkey.Registered, lastAttemptError = hotkey.Error }, nativeHotkeyRollback,
            foregroundObserved = NativeMethods.GetForegroundWindow() == handle, focusObserved = window.IsKeyboardFocusWithin,
            nativeVisibilityObserved = NativeMethods.IsWindowVisible(handle),
            dpi = NativeMethods.GetDpiForWindow(handle), checks = results,
            savedRevision = store.SavedRevision, profileDirectory = store.DirectoryPath,
            notCovered = new[] { "Real hotkey input", "Mouse drag event sequences", "External browser OLE", "Mixed monitor DPI", "Cross-login-session UI", "UAC", "Shell launch", "Physical presentation latency", "OS shutdown" },
            screenshotKind = "WPF RenderTargetBitmap composited on solid ApplicationBackgroundBrush; does not capture DWM Acrylic, border or shadow"
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        window.Log.Write("smoke.complete", new { passed });
        return passed;
    }

    private static bool SameIcon(System.Drawing.Icon? actual, System.Drawing.Icon expected)
    {
        if (actual is null || actual.Size != expected.Size) return false;
        using var left = actual.ToBitmap();
        using var right = expected.ToBitmap();
        for (int y = 0; y < left.Height; y++)
            for (int x = 0; x < left.Width; x++)
                if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
        return true;
    }

    internal static void SaveSnapshot(FrameworkElement window, string path, bool includeOverflow = false)
    {
        var bounds = new Rect(window.RenderSize);
        if (includeOverflow) bounds.Union(VisualTreeHelper.GetDescendantBounds(window));
        var image = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle((Brush)window.FindResource("ApplicationBackgroundBrush"), null, new Rect(bounds.Size));
            if (includeOverflow) drawing.DrawRectangle(new VisualBrush(window) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, new Rect(bounds.Size));
        }
        image.Render(background);
        if (!includeOverflow) image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
