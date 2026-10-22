using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Swiftdock.Diagnostics;
using Swiftdock.Models;
using Swiftdock.Services;

namespace Swiftdock.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsState _state;
    private readonly StartupService _startup;
    private readonly IHotkeyService _hotkey;
    private readonly ISettingsActions _actions;
    private readonly ILogger<SettingsViewModel> _log;
    private readonly ThemeService? _appearance;
    private bool _syncing;

    [ObservableProperty] private bool _alt, _control, _shift, _windows;
    [ObservableProperty] private string _mainKey;
    [ObservableProperty] private int _scale;
    [ObservableProperty] private string _placement;
    [ObservableProperty] private string _theme, _backdrop, _accent;
    [ObservableProperty] private int _transparency;
    [ObservableProperty] private bool _keepBackdropWhenInactive;
    [ObservableProperty] private bool _autoFetchWebsiteIcons, _startupEnabled;
    [ObservableProperty] private bool _startupAvailable = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusError;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ResetSizeCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditBackdrop))]
    private bool _canEdit;

    public string DirectoryLabel => _state.DirectoryPath;
    public string AppVersion => $"迅启 Swiftdock {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";
    public bool CanEditBackdrop => CanEdit && Backdrop != "solid" && (_appearance?.HasBackdrop ?? true);
    public IReadOnlyList<string> Keys => ConfigValidation.Keys;
    public string BackdropDescription => _appearance?.BackdropDescription ?? "应用于面板；对话框使用云母背景";
    public string ThemeDescription => _appearance?.HighContrast == true ? "当前使用 Windows 高对比度主题" : "立即应用于面板和对话框";
    public string AccentDescription => _appearance?.HighContrast == true ? "当前使用 Windows 高对比度配色" : "用于选中状态和主要操作";

    public SettingsViewModel(ISettingsState state, StartupService startup, IHotkeyService hotkey,
        ISettingsActions actions, ILogger<SettingsViewModel> log, ThemeService? appearance = null)
    {
        _state = state; _startup = startup; _hotkey = hotkey; _actions = actions; _log = log;
        _appearance = appearance;
        _theme = state.Theme; _backdrop = state.Backdrop; _accent = state.Accent;
        _transparency = 100 - state.BackdropOpacity; _keepBackdropWhenInactive = state.KeepBackdropWhenInactive;
        if (_appearance is not null) _appearance.BackdropChanged += AppearanceChanged;
        _canEdit = state.CanEdit;
        _alt = state.Hotkey.Modifiers.Contains("Alt"); _control = state.Hotkey.Modifiers.Contains("Control");
        _shift = state.Hotkey.Modifiers.Contains("Shift"); _windows = state.Hotkey.Modifiers.Contains("Windows");
        _mainKey = state.Hotkey.Key; _scale = state.Scale; _placement = state.Placement;
        _autoFetchWebsiteIcons = state.AutoFetchWebsiteIcons;
        SetStatus(!CanEdit ? "当前配置只读，不能修改。" : hotkey.Registered
            ? $"当前已注册：{hotkey.Current!.Display}" : $"注册失败，错误码 {hotkey.Error}；请选择其他组合。", !CanEdit || !hotkey.Registered);
        try { _startupEnabled = startup.IsEnabled; }
        catch (Exception ex) { ReportError(ex); StartupAvailable = false; }
        state.PropertyChanged += StateChanged;
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        _syncing = true;
        try
        {
            CanEdit = _state.CanEdit;
            Scale = _state.Scale; Placement = _state.Placement;
            Theme = _state.Theme; Backdrop = _state.Backdrop; Accent = _state.Accent;
            Transparency = 100 - _state.BackdropOpacity; KeepBackdropWhenInactive = _state.KeepBackdropWhenInactive;
            AutoFetchWebsiteIcons = _state.AutoFetchWebsiteIcons;
        }
        finally { _syncing = false; }
    }

    partial void OnScaleChanged(int value) { if (!_syncing && CanEdit) _state.SetScale(value); }
    partial void OnPlacementChanged(string value) { if (!_syncing && CanEdit) _state.SetPlacement(value); }
    partial void OnThemeChanged(string value) { if (!_syncing && CanEdit) _state.SetTheme(value); }
    partial void OnBackdropChanged(string value)
    {
        if (!_syncing && CanEdit) _state.SetBackdrop(value);
        OnPropertyChanged(nameof(CanEditBackdrop));
    }
    partial void OnTransparencyChanged(int value) { if (!_syncing && CanEditBackdrop) _state.SetBackdropOpacity(100 - value); }
    partial void OnKeepBackdropWhenInactiveChanged(bool value) { if (!_syncing && CanEditBackdrop) _state.SetKeepBackdropWhenInactive(value); }
    partial void OnAccentChanged(string value) { if (!_syncing && CanEdit) _state.SetAccent(value); }
    private void AppearanceChanged()
    {
        OnPropertyChanged(nameof(BackdropDescription)); OnPropertyChanged(nameof(ThemeDescription)); OnPropertyChanged(nameof(AccentDescription));
        OnPropertyChanged(nameof(CanEditBackdrop));
    }
    partial void OnAutoFetchWebsiteIconsChanged(bool value) { if (!_syncing && CanEdit) _state.SetAutoFetchWebsiteIcons(value); }
    partial void OnStartupEnabledChanged(bool value)
    {
        if (_syncing || !CanEdit || !StartupAvailable) return;
        try { _startup.SetEnabled(value); SetStatus("自启设置已更新。"); }
        catch (Exception ex)
        {
            _syncing = true;
            try { StartupEnabled = false; }
            finally { _syncing = false; }
            ReportError(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Apply()
    {
        if (!CanEdit) return;
        var candidate = new HotkeyConfig
        {
            Modifiers = new[] { ("Alt", Alt), ("Control", Control), ("Shift", Shift), ("Windows", Windows) }
                .Where(pair => pair.Item2).Select(pair => pair.Item1).ToArray(), Key = MainKey
        };
        try
        {
            ConfigValidation.ValidateHotkey(candidate);
            if (!_hotkey.TryChange(candidate))
            { SetStatus($"新组合被占用或不可用（{_hotkey.Error}）；原有热键和配置已保留。", true); return; }
            _state.ApplySettings(candidate); _actions.HotkeyChanged(); _log.HotkeyChanged(candidate.Display);
            SetStatus("已应用，配置将自动保存。");
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ResetSize()
    {
        if (!CanEdit) return;
        try { _actions.ResetWindowSize(); SetStatus("已恢复默认位置和大小，配置将自动保存。"); }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand] private Task ExitAsync() => _actions.ExitAsync();
    [RelayCommand] private void OpenLogs()
    {
        try { _actions.OpenLogs(); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void SetStatus(string message, bool error = false) { Status = message; StatusError = error; }
    private void ReportError(Exception ex)
    {
        SetStatus(ex.Message, true);
        _log.LogWarning("Settings failed: {ErrorType} {Message}", ex.GetType().Name, LogPrivacy.Message(ex.Message));
    }
    public void Dispose()
    {
        _state.PropertyChanged -= StateChanged;
        if (_appearance is not null) _appearance.BackdropChanged -= AppearanceChanged;
    }
}
