using System.Diagnostics;
using System.Drawing;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
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
        Click(ById(theme == "dark" ? "DarkTheme" : "LightTheme", settings), settings);
        string material = backdrop switch { "mica" => "MicaBackdrop", "solid" => "SolidBackdrop", _ => "AcrylicBackdrop" };
        Click(ById(material, settings), settings);
        AssertNativeBackdrop(settings.Properties.NativeWindowHandle.Value, backdrop, dialog: true);
        Click(Named("关闭", ControlType.Button, settings), settings);
        Eventually(() => !HasWindow("设置"), "appearance settings to close");
        Foreground();
        AssertNativeBackdrop(PanelHandle, backdrop);
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
