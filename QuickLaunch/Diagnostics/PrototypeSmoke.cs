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
    public static async Task<bool> RunAsync(LauncherWindow window, HotkeyService hotkey, ConfigService store, App app, string directory)
    {
        var results = new List<object>();
        bool passed = true;
        void Check(string name, bool value)
        {
            results.Add(new { name, passed = value });
            passed &= value;
        }
        window.Pinned = true;
        window.ShowPanel("smoke");
        await Task.Delay(250);
        Check("visible-window-with-layout", window.IsVisible && window.ActualWidth > 0 && window.ActualHeight > 0);
        Check("two-groups", window.Groups.Count == 2);
        nint handle = new WindowInteropHelper(window).Handle;
        Check("native-hwnd", handle != 0 && NativeMethods.GetWindowRect(handle, out _));
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
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(Path.Combine(directory, "m1-window.png"))) encoder.Save(output);
        window.HidePanel();
        Check("hide-retains-hwnd", !window.IsVisible && new WindowInteropHelper(window).Handle == handle);
        window.ShowPanel("smoke-reshow");
        await Task.Delay(150);
        Check("reshow-retains-hwnd", window.IsVisible && new WindowInteropHelper(window).Handle == handle);
        Check("configuration-writable", !store.IsReadOnly);
        var group = window.Groups[0];
        group.Items.Move(0, group.Items.Count - 1);
        await window.ViewModel.FlushAsync();
        var persisted = ConfigValidation.Parse(await File.ReadAllBytesAsync(store.FilePath));
        Check("reorder-persisted", persisted.Groups[0].Items.Select(i => i.Id).SequenceEqual(group.Items.Select(i => i.Id)));
        Check("latest-revision-saved", store.SavedRevision == window.ViewModel.Revision);

        app.OpenSettings();
        await Task.Delay(80);
        var settings = Application.Current.Windows.OfType<SettingsWindow>().Single();
        Check("settings-owned-and-visible", settings.IsVisible && settings.Owner == window && window.IsVisible);
        settings.HideOnDeactivate.IsChecked = false;
        settings.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("settings-applied", !window.ViewModel.HideOnDeactivate);
        settings.Close();
        Check("settings-close-keeps-main-alive", window.IsVisible && new WindowInteropHelper(window).Handle == handle);
        await window.ViewModel.FlushAsync();
        persisted = ConfigValidation.Parse(await File.ReadAllBytesAsync(store.FilePath));
        Check("settings-persisted", !persisted.HideOnDeactivate);

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
        var report = new
        {
            passed, runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
            hotkeyRegistration = new { hotkey.Registered, lastAttemptError = hotkey.Error }, nativeHotkeyRollback,
            foregroundObserved = NativeMethods.GetForegroundWindow() == handle, focusObserved = window.IsKeyboardFocusWithin,
            dpi = NativeMethods.GetDpiForWindow(handle), checks = results,
            savedRevision = store.SavedRevision, profileDirectory = store.DirectoryPath,
            notCovered = new[] { "Real hotkey input", "Mouse drag event sequences", "External browser OLE", "Mixed monitor DPI", "Cross-login-session UI", "UAC", "Shell launch", "Physical presentation latency", "OS shutdown" },
            screenshotKind = "WPF RenderTargetBitmap; not a physical-screen presentation measurement"
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        window.Log.Write("smoke.complete", new { passed });
        return passed;
    }
}
