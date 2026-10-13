using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickLaunch.Interop;
using QuickLaunch.Services;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

// Runs inside the actual WPF dispatcher. No synthetic keyboard input or launches of user applications.
internal static class PrototypeSmoke
{
    public static async Task<bool> RunAsync(LauncherWindow window, HotkeyService hotkey, string directory)
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
        using (var output = File.Create(Path.Combine(directory, "m0-window.png"))) encoder.Save(output);
        window.HidePanel();
        Check("hide-retains-hwnd", !window.IsVisible && new WindowInteropHelper(window).Handle == handle);
        window.ShowPanel("smoke-reshow");
        await Task.Delay(150);
        Check("reshow-retains-hwnd", window.IsVisible && new WindowInteropHelper(window).Handle == handle);
        var report = new
        {
            passed, runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
            hotkeyRegistration = new { hotkey.Registered, hotkey.Error },
            foregroundObserved = NativeMethods.GetForegroundWindow() == handle, focusObserved = window.IsKeyboardFocusWithin,
            dpi = NativeMethods.GetDpiForWindow(handle), checks = results,
            notCovered = new[] { "Real hotkey input", "Mouse drag event sequences", "External browser OLE", "Mixed monitor DPI", "UAC", "Shell launch", "Physical presentation latency" },
            screenshotKind = "WPF RenderTargetBitmap; not a physical-screen presentation measurement"
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        window.Log.Write("smoke.complete", new { passed });
        return passed;
    }
}
