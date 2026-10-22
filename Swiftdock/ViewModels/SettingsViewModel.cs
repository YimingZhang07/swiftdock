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
    [ObservableProperty] private string _language;
    [ObservableProperty] private int _transparency;
    [ObservableProperty] private bool _keepBackdropWhenInactive;
    [ObservableProperty] private bool _autoFetchWebsiteIcons, _startupEnabled;
    [ObservableProperty] private bool _startupAvailable = true;
    private UiText _status = "";
    public string Status => _status.Value;
    [ObservableProperty] private bool _statusError;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ResetSizeCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditBackdrop))]
    private bool _canEdit;

    public string DirectoryLabel => _state.DirectoryPath;
    public string AppVersion => L.Text("AboutVersion", L.Instance["Swiftdock"], L.Resources.GetString("Swiftdock", L.Instance.Culture.Name == "en" ? System.Globalization.CultureInfo.GetCultureInfo("zh-CN") : System.Globalization.CultureInfo.GetCultureInfo("en")), typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3));
    public bool CanEditBackdrop => CanEdit && Backdrop != "solid" && (_appearance?.HasBackdrop ?? true);
    public IReadOnlyList<string> Keys => ConfigValidation.Keys;
    public string BackdropDescription => _appearance?.BackdropDescription ?? L.Text("PanelMaterialDialogsUseMica");
    public string ThemeDescription => _appearance?.HighContrast == true ? L.Text("UsingWindowsHighContrastTheme") : L.Text("AppliesImmediatelyToPanelAndDialogs");
    public string AccentDescription => _appearance?.HighContrast == true ? L.Text("UsingWindowsHighContrastColors") : L.Text("ForSelectionsAndPrimaryActions");

    public SettingsViewModel(ISettingsState state, StartupService startup, IHotkeyService hotkey,
        ISettingsActions actions, ILogger<SettingsViewModel> log, ThemeService? appearance = null)
    {
        _state = state; _startup = startup; _hotkey = hotkey; _actions = actions; _log = log;
        _appearance = appearance;
        _language = state.Language; L.Observe(LanguageChanged);
        _theme = state.Theme; _backdrop = state.Backdrop; _accent = state.Accent;
        _transparency = 100 - state.BackdropOpacity; _keepBackdropWhenInactive = state.KeepBackdropWhenInactive;
        if (_appearance is not null) _appearance.BackdropChanged += AppearanceChanged;
        _canEdit = state.CanEdit;
        _alt = state.Hotkey.Modifiers.Contains("Alt"); _control = state.Hotkey.Modifiers.Contains("Control");
        _shift = state.Hotkey.Modifiers.Contains("Shift"); _windows = state.Hotkey.Modifiers.Contains("Windows");
        _mainKey = state.Hotkey.Key; _scale = state.Scale; _placement = state.Placement;
        _autoFetchWebsiteIcons = state.AutoFetchWebsiteIcons;
        SetStatus(!CanEdit ? L.Text("ConfigurationIsReadOnlyAndCannotBeChanged") : hotkey.Registered
            ? L.Text("Registered0", hotkey.Current!.Display) : L.Text("RegistrationFailedError0ChooseAnotherCombination", hotkey.Error), !CanEdit || !hotkey.Registered);
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
            Language = _state.Language; Theme = _state.Theme; Backdrop = _state.Backdrop; Accent = _state.Accent;
            Transparency = 100 - _state.BackdropOpacity; KeepBackdropWhenInactive = _state.KeepBackdropWhenInactive;
            AutoFetchWebsiteIcons = _state.AutoFetchWebsiteIcons;
        }
        finally { _syncing = false; }
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e)
    { OnPropertyChanged(string.Empty); }
    partial void OnLanguageChanged(string value) { if (!_syncing && CanEdit) _state.SetLanguage(value); }
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
        try { _startup.SetEnabled(value); SetStatus(L.Text("StartupSettingsUpdated")); }
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
            { SetStatus(L.Text("TheNewCombinationIsInUseOrUnavailable0TheOriginalHotkeyAndSetting", _hotkey.Error), true); return; }
            _state.ApplySettings(candidate); _actions.HotkeyChanged(); _log.HotkeyChanged(candidate.Display);
            SetStatus(L.Text("AppliedChangesWillBeSavedAutomatically"));
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ResetSize()
    {
        if (!CanEdit) return;
        try { _actions.ResetWindowSize(); SetStatus(L.Text("DefaultPositionAndSizeRestoredChangesWillBeSavedAutomatically")); }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand] private Task ExitAsync() => _actions.ExitAsync();
    [RelayCommand] private void OpenLogs()
    {
        try { _actions.OpenLogs(); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void SetStatus(UiText message, bool error = false) { _status = message; OnPropertyChanged(nameof(Status)); StatusError = error; }
    private void ReportError(Exception ex)
    {
        SetStatus(L.Error(ex), true);
        _log.LogWarning("Settings failed: {ErrorType} {Message}", ex.GetType().Name, LogPrivacy.Message(L.Diagnostic(ex)));
    }
    public void Dispose()
    {
        L.Unobserve(LanguageChanged);
        _state.PropertyChanged -= StateChanged;
        if (_appearance is not null) _appearance.BackdropChanged -= AppearanceChanged;
    }
}
