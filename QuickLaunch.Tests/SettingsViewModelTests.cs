using System.ComponentModel;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task LivePreferencesApplyExactlyOnceAndFollowExternalChanges()
    {
        var state = new SettingsState(); var hotkey = new Hotkey(); var actions = new Actions();
        var writes = new List<string?>();
        var startup = Startup(_ => null, (_, command) => writes.Add(command));
        using var model = Create(state, startup, hotkey, actions);
        Assert.Equal("当前已注册：Alt+Space", model.Status);
        Assert.Equal("配置目录：C:\\profile", model.DirectoryLabel);
        Assert.Empty(state.Calls); Assert.Empty(writes);
        model.IconSize = 48; model.Placement = "cursor"; model.AutoFetchWebsiteIcons = false;
        Assert.Equal(["size:48", "placement:cursor", "favicons:False"], state.Calls);
        state.SetIconSize(32); state.SetPlacement("lastPosition"); state.SetAutoFetchWebsiteIcons(true);
        Assert.Equal(6, state.Calls.Count);
        Assert.Equal(32, model.IconSize); Assert.Equal("lastPosition", model.Placement); Assert.True(model.AutoFetchWebsiteIcons);
        state.Calls.Clear();
        model.Theme = "dark"; model.Backdrop = "solid"; model.Accent = "purple";
        Assert.Equal(["theme:dark", "backdrop:solid", "accent:purple"], state.Calls);
        state.SetTheme("light"); state.SetBackdrop("mica"); state.SetAccent("blue");
        Assert.Equal(6, state.Calls.Count);
        Assert.Equal("light", model.Theme); Assert.Equal("mica", model.Backdrop); Assert.Equal("blue", model.Accent);
        model.StartupEnabled = true; model.StartupEnabled = false;
        Assert.Equal([startup.Command, null], writes); Assert.Equal("自启设置已更新。", model.Status);
        model.ResetSizeCommand.Execute(null);
        Assert.Equal(1, actions.Resets); Assert.Equal("已恢复默认位置和大小，配置将自动保存。", model.Status);
        await model.ExitCommand.ExecuteAsync(null); Assert.Equal(1, actions.Exits);
    }

    [Fact]
    public void HotkeyValidatesBeforeRegistrationAndOnlyCommitsOnSuccess()
    {
        var state = new SettingsState(); var hotkey = new Hotkey(); var actions = new Actions();
        using var model = Create(state, Startup(), hotkey, actions);
        model.Alt = false; model.ApplyCommand.Execute(null);
        Assert.Equal("热键需要至少一个修饰键和受支持的主键。", model.Status);
        Assert.True(model.StatusError); Assert.Equal(0, hotkey.Attempts);
        model.Control = true; model.Shift = true; model.MainKey = "F8"; hotkey.Succeeds = false;
        model.ApplyCommand.Execute(null);
        Assert.Equal("新组合被占用或不可用（1409）；原有热键和配置已保留。", model.Status);
        Assert.Empty(state.Calls); Assert.Equal("Space", state.Hotkey.Key); Assert.Equal(0, actions.HotkeyChanges);
        hotkey.Succeeds = true; model.ApplyCommand.Execute(null);
        Assert.Equal(2, hotkey.Attempts); Assert.Equal(["Control", "Shift"], state.Hotkey.Modifiers);
        Assert.Equal("F8", state.Hotkey.Key); Assert.Equal(1, actions.HotkeyChanges);
        Assert.Equal("已应用，配置将自动保存。", model.Status); Assert.False(model.StatusError);
    }

    [Fact]
    public void StartupReadAndWriteFailuresKeepTheirMessagesAndState()
    {
        using var unreadable = Create(new(), Startup(_ => throw new IOException("读取自启失败")), new(), new());
        Assert.False(unreadable.StartupAvailable); Assert.True(unreadable.StatusError); Assert.Equal("读取自启失败", unreadable.Status);
        int writes = 0;
        using var unwritable = Create(new(), Startup(write: (_, _) => { writes++; throw new IOException("更新自启失败"); }), new(), new());
        unwritable.StartupEnabled = true;
        Assert.False(unwritable.StartupEnabled); Assert.Equal(1, writes);
        Assert.True(unwritable.StatusError); Assert.Equal("更新自启失败", unwritable.Status);
    }

    [Fact]
    public void ReadOnlyAndSuspensionDisableCommandsAndLiveWrites()
    {
        var state = new SettingsState(); state.SetEditable(false);
        var hotkey = new Hotkey(); var actions = new Actions(); int startupWrites = 0;
        using var model = Create(state, Startup(write: (_, _) => startupWrites++), hotkey, actions);
        Assert.Equal("当前配置只读，不能修改。", model.Status);
        Assert.False(model.ApplyCommand.CanExecute(null)); Assert.False(model.ResetSizeCommand.CanExecute(null));
        model.ApplyCommand.Execute(null); model.ResetSizeCommand.Execute(null);
        model.IconSize = 48; model.Placement = "cursor"; model.AutoFetchWebsiteIcons = false; model.StartupEnabled = true;
        model.Theme = "dark"; model.Backdrop = "solid"; model.Accent = "purple";
        Assert.Empty(state.Calls); Assert.Equal(0, startupWrites); Assert.Equal(0, hotkey.Attempts); Assert.Equal(0, actions.Resets);
        int changes = 0; model.ApplyCommand.CanExecuteChanged += (_, _) => changes++;
        state.SetEditable(true); Assert.True(model.ApplyCommand.CanExecute(null));
        state.SetEditable(false); Assert.False(model.ApplyCommand.CanExecute(null)); Assert.Equal(2, changes);
        Assert.True(model.ExitCommand.CanExecute(null));
    }

    [Fact]
    public void FailedRegistrationIsDisplayedAndDisposalUnsubscribes()
    {
        var state = new SettingsState(); var hotkey = new Hotkey { Registered = false };
        var model = Create(state, Startup(), hotkey, new());
        Assert.Equal("注册失败，错误码 1409；请选择其他组合。", model.Status); Assert.True(model.StatusError);
        model.Dispose(); state.SetIconSize(48);
        Assert.Equal(40, model.IconSize);
    }

    private static SettingsViewModel Create(SettingsState state, StartupService startup, Hotkey hotkey, Actions actions) =>
        new(state, startup, hotkey, actions, NullLogger<SettingsViewModel>.Instance);
    private static StartupService Startup(Func<string, string?>? read = null, Action<string, string?>? write = null) =>
        new(@"C:\QuickLaunch\QuickLaunch.exe", @"C:\profile", @"Software\QuickLaunch\Tests", read ?? (_ => null), write ?? ((_, _) => { }));

    private sealed class SettingsState : ISettingsState
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool CanEdit { get; private set; } = true;
        public string DirectoryPath => @"C:\profile";
        public HotkeyConfig Hotkey { get; private set; } = new();
        public int IconSize { get; private set; } = 40;
        public string Placement { get; private set; } = "lastPosition";
        public string Theme { get; private set; } = "system";
        public string Backdrop { get; private set; } = "acrylic";
        public string Accent { get; private set; } = "system";
        public bool AutoFetchWebsiteIcons { get; private set; } = true;
        internal List<string> Calls { get; } = [];
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
        internal void SetEditable(bool value) { CanEdit = value; Changed(nameof(CanEdit)); }
        public void ApplySettings(HotkeyConfig hotkey) { Hotkey = hotkey; Calls.Add("hotkey"); Changed(nameof(Hotkey)); }
        public void SetIconSize(int size) { IconSize = size; Calls.Add($"size:{size}"); Changed(nameof(IconSize)); }
        public void SetPlacement(string placement) { Placement = placement; Calls.Add($"placement:{placement}"); Changed(nameof(Placement)); }
        public void SetTheme(string theme) { Theme = theme; Calls.Add($"theme:{theme}"); Changed(nameof(Theme)); }
        public void SetBackdrop(string backdrop) { Backdrop = backdrop; Calls.Add($"backdrop:{backdrop}"); Changed(nameof(Backdrop)); }
        public void SetAccent(string accent) { Accent = accent; Calls.Add($"accent:{accent}"); Changed(nameof(Accent)); }
        public void SetAutoFetchWebsiteIcons(bool enabled) { AutoFetchWebsiteIcons = enabled; Calls.Add($"favicons:{enabled}"); Changed(nameof(AutoFetchWebsiteIcons)); }
    }
    private sealed class Hotkey : IHotkeyService
    {
        public bool Registered { get; set; } = true;
        public int Error => 1409;
        public HotkeyConfig Current { get; private set; } = new();
        internal bool Succeeds { get; set; } = true;
        internal int Attempts { get; private set; }
        public bool TryChange(HotkeyConfig candidate) { Attempts++; if (Succeeds) Current = candidate; return Succeeds; }
    }
    private sealed class Actions : ISettingsActions
    {
        internal int Resets { get; private set; }
        internal int Exits { get; private set; }
        internal int HotkeyChanges { get; private set; }
        public void ResetWindowSize() => Resets++;
        public void HotkeyChanged() => HotkeyChanges++;
        public Task ExitAsync() { Exits++; return Task.CompletedTask; }
    }
}
