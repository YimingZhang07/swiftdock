using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace Swiftdock.E2E;

internal sealed partial class LauncherSession
{
    internal Window AssertBrowserRouting(Window settings)
    {
        // Copy a harmless Windows utility into this scenario's owned temp directory.
        // The launch recorder checks its existence and records routing without starting it.
        string program = Path.Combine(_temp, "Browser Fixture.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"), program);
        PickBrowserProgram(settings, program);
        AssertBrowserSetting(program);
        CloseSettings(settings);

        const string url = "https://example.com/a path?q=\"quoted\"&other=2";
        const string name = "Browser routing";
        Click(ById("AddItemButton"));
        var editor = Dialog("添加项目");
        Click(ById("UrlCard", editor), editor);
        SetText(url, editor, ById("PathBox", editor));
        SetText(name, editor, ById("NameBox", editor));
        Click(ById("SaveButton", editor), editor);
        Eventually(() => !HasWindow("添加项目") && TileNames().Contains(name), "website routing fixture to save");
        string? id = null;
        Eventually(() =>
        {
            using var config = TryReadConfig();
            if (config is null) return false;
            var item = config.RootElement.GetProperty("groups")[0].GetProperty("items").EnumerateArray()
                .FirstOrDefault(i => i.GetProperty("name").GetString() == name);
            id = item.ValueKind == JsonValueKind.Object ? item.GetProperty("id").GetString() : null;
            return id is not null;
        }, "website routing fixture to persist");
        Click(Tiles().Single(t => t.Name == name));
        ExpectLaunch(id!, name);
        AssertWebsiteRoute(program, url);

        Click(ById("SettingsButton")); settings = Dialog("设置");
        NavigateSettings(settings, "General");
        Click(ById("BrowserBox", settings), settings);
        Click(Named("系统默认浏览器", ControlType.ListItem), settings);
        AssertBrowserSetting(null);
        CloseSettings(settings);
        Click(Tiles().Single(t => t.Name == name));
        ExpectLaunch(id!, name);
        AssertWebsiteRoute("shell", null);
        Click(ById("SettingsButton"));
        return Dialog("设置");
    }

    private void PickBrowserProgram(Window settings, string program)
    {
        const string title = "选择浏览器程序";
        Exception? failure = null;
        try
        {
            NavigateSettings(settings, "General");
            Click(ById("BrowserBox", settings), settings);
            Click(Named("自定义程序…", ControlType.ListItem), settings);
            var (picker, filename) = NativeBrowserDialog(title);
            RequireNativeBrowserFocus(picker, filename);
            // Set rather than type the path because IMEs can intercept input in native dialogs.
            Desktop.SetEditText(filename, program);
            Assert.Equal(program, Desktop.ReadEditText(filename));
            RequireNativeBrowserFocus(picker, filename);
            try { PressKey(VirtualKeyShort.RETURN, "submit native browser filename"); }
            finally { ReleaseKey(VirtualKeyShort.RETURN); }
            Eventually(() => !Desktop.IsWindow(picker), "browser file picker HWND to disappear", 5000);
        }
        catch (Exception error)
        {
            failure = error;
            LogNativeBrowserDialog(title); // Never ask this dialog's unreliable UIA provider for diagnostics.
            throw;
        }
        finally
        {
            try { CancelNativeBrowserDialog(title); }
            catch (Exception cleanupError)
            {
                if (failure is null) throw;
                throw new AggregateException("Browser picker failed and cancellation also failed.", failure, cleanupError);
            }
        }
    }

    private nint NativeBrowserDialogHandle(string title) => _process is null || _process.HasExited ? 0
        : Desktop.ProcessWindowHandles(_process.Id).FirstOrDefault(hwnd => Desktop.IsWindowVisible(hwnd)
            && Desktop.WindowClass(hwnd) == "#32770" && Desktop.WindowTitle(hwnd) == title);

    private (nint Dialog, nint Filename) NativeBrowserDialog(string title)
    {
        nint dialog = 0, filename = 0;
        // Common Item Dialog UIA providers can remain unavailable after the native
        // window is ready. Use only HWNDs and the dialog thread's keyboard focus.
        Eventually(() =>
        {
            CheckAbort();
            dialog = NativeBrowserDialogHandle(title);
            if (dialog == 0) return false;
            if (Desktop.GetForegroundWindow() != dialog)
            {
                Desktop.SetForegroundWindow(dialog);
                return false;
            }
            uint thread = Desktop.GetWindowThreadProcessId(dialog, out _);
            try { filename = Desktop.GuiInfo(thread).Focus; }
            catch (Win32Exception) { return false; } // The cold dialog thread may still be initializing.
            return filename != 0 && Desktop.GetAncestor(filename, 2) == dialog // GA_ROOT
                && Desktop.WindowClass(filename).Equals("Edit", StringComparison.OrdinalIgnoreCase);
        }, $"native dialog '{title}' to own foreground and filename Edit focus", 15_000);
        Diagnostic($"Native browser picker ready: HWND=0x{dialog:X}; class={Desktop.WindowClass(dialog)}; filename=0x{filename:X}; class={Desktop.WindowClass(filename)}");
        return (dialog, filename);
    }

    private void RequireNativeBrowserFocus(nint dialog, nint filename)
    {
        CheckAbort();
        uint thread = Desktop.GetWindowThreadProcessId(dialog, out uint process);
        Assert.True(process == _process!.Id && Desktop.IsWindow(dialog) && Desktop.GetForegroundWindow() == dialog
            && Desktop.GetAncestor(filename, 2) == dialog && Desktop.WindowClass(filename).Equals("Edit", StringComparison.OrdinalIgnoreCase),
            $"Refusing native browser input: dialog=0x{dialog:X}, class={Desktop.WindowClass(dialog)}; filename=0x{filename:X}, class={Desktop.WindowClass(filename)}; {Desktop.FocusDescription(dialog)}");
        Assert.Equal(filename, Desktop.GuiInfo(thread).Focus);
    }

    private void LogNativeBrowserDialog(string title)
    {
        Diagnostic($"Native browser picker failure for {JsonSerializer.Serialize(title)}: {Desktop.FocusDescription()}");
        if (_process is null || _process.HasExited) return;
        foreach (nint hwnd in Desktop.ProcessWindowHandles(_process.Id))
        {
            uint thread = Desktop.GetWindowThreadProcessId(hwnd, out _);
            string focus;
            try
            {
                nint edit = Desktop.GuiInfo(thread).Focus;
                focus = $"focus=0x{edit:X}; focusClass={Desktop.WindowClass(edit)}; focusRoot=0x{Desktop.GetAncestor(edit, 2):X}";
            }
            catch (Win32Exception error) { focus = "GetGUIThreadInfo: " + error.Message; }
            Diagnostic($"Native candidate: HWND=0x{hwnd:X}; title={JsonSerializer.Serialize(Desktop.WindowTitle(hwnd))}; class={Desktop.WindowClass(hwnd)}; owner=0x{Desktop.GetWindow(hwnd, 4):X}; visible={Desktop.IsWindowVisible(hwnd)}; {focus}");
        }
    }

    private void CancelNativeBrowserDialog(string title)
    {
        // Native IDCANCEL works even if UIA/foreground discovery failed. Only address
        // this test process's visible dialog with the exact caption; never the desktop.
        var watch = Stopwatch.StartNew();
        Eventually(() =>
        {
            nint hwnd = NativeBrowserDialogHandle(title);
            if (hwnd == 0) return true;
            Diagnostic($"Cancelling browser picker HWND=0x{hwnd:X}; title={JsonSerializer.Serialize(title)}; class={Desktop.WindowClass(hwnd)}");
            _ = Desktop.SendMessageTimeout(hwnd, 0x0111, 2, 0, 0x0002, 500, out _); // WM_COMMAND / IDCANCEL
            if (watch.ElapsedMilliseconds >= 1500)
                _ = Desktop.SendMessageTimeout(hwnd, 0x0010, 0, 0, 0x0002, 500, out _); // WM_CLOSE also cancels a Common Item Dialog.
            return NativeBrowserDialogHandle(title) == 0;
        }, "native browser picker cancellation", 4000);
    }

    private void CloseSettings(Window settings)
    {
        Click(Named("关闭", ControlType.Button, settings), settings);
        Eventually(() => !HasWindow("设置"), "settings to close");
    }

    private void AssertBrowserSetting(string? program) => Eventually(() =>
    {
        using var config = TryReadConfig();
        if (config is null) return false;
        string? stored = config.RootElement.TryGetProperty("browser", out var browser) ? browser.GetString() : null;
        return (stored is null ? null : Environment.ExpandEnvironmentVariables(stored)) == program;
    }, "browser choice to persist after real input");

    private void AssertWebsiteRoute(string program, string? url)
    {
        using var entry = JsonDocument.Parse(File.ReadAllLines(LaunchFile)[^1]);
        Assert.Equal(program, entry.RootElement.GetProperty("program").GetString());
        var arguments = entry.RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()).ToArray();
        if (url is null) Assert.Empty(arguments);
        else Assert.Equal(url, Assert.Single(arguments));
    }
}
