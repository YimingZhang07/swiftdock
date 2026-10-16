using System.Windows;
using System.Windows.Threading;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickLaunch.Diagnostics;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace QuickLaunch.Services;

public sealed class ThemeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ISettingsState? _state;
    private readonly IThemeApplication _application;
    private readonly ILogger<ThemeService> _log;
    private readonly Func<(bool Dark, bool HighContrast, bool Transparency)> _readPreferences;
    private readonly HashSet<FluentWindow> _dialogs = [];
    private readonly HashSet<string> _queuedSources = [];
    private bool _disposed, _queued;
    private bool _backdropFailed;
    public bool IsDark { get; private set; }
    public bool HighContrast { get; private set; }
    public bool TransparencyEnabled { get; private set; }
    public event Action? Changed;
    public event Action? BackdropChanged;
    public string Backdrop => _state?.Backdrop ?? "acrylic";
    public bool HasBackdrop => !_backdropFailed && AppearanceOptions.EffectiveBackdrop(Backdrop,
        AppearanceOptions.IsSupported(AppearanceOptions.BackdropType(Backdrop)), TransparencyEnabled, HighContrast) != WindowBackdropType.None;
    public string BackdropDescription => AppearanceOptions.DescribeBackdrop(Backdrop,
        AppearanceOptions.IsSupported(AppearanceOptions.BackdropType(Backdrop)), TransparencyEnabled, HighContrast, _backdropFailed);

    public ThemeService(Dispatcher dispatcher, ISettingsState? state = null, ILogger<ThemeService>? log = null)
        : this(dispatcher, state, new WpfThemeApplication(), ReadPreferences, log) { }

    internal ThemeService(Dispatcher dispatcher, ISettingsState? state, IThemeApplication application,
        Func<(bool Dark, bool HighContrast, bool Transparency)> readPreferences, ILogger<ThemeService>? log = null)
    {
        _dispatcher = dispatcher; _state = state; _application = application; _readPreferences = readPreferences;
        _log = log ?? NullLogger<ThemeService>.Instance;
        if (_state is not null) _state.PropertyChanged += StateChanged;
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        Apply("startup");
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ISettingsState.Theme) or nameof(ISettingsState.Accent) or nameof(ISettingsState.Backdrop)) Apply("setting:" + args.PropertyName);
    }

    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs args) => QueueUpdate("preference:" + args.Category);
    public void QueueUpdate(string source = "notification")
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _queuedSources.Add(source);
            if (_queued) return;
            _queued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                string sources = string.Join(",", _queuedSources);
                _queuedSources.Clear(); _queued = false; Apply(sources);
            });
        });
    }

    internal void Apply(string source = "explicit")
    {
        if (_disposed) return;
        long started = Stopwatch.GetTimestamp();
        int dictionariesBefore = CountDictionaries(), reloads = 0;
        void Reload(ApplicationTheme value) { _application.ApplyTheme(value); reloads++; }
        var preferences = _readPreferences();
        HighContrast = preferences.HighContrast; TransparencyEnabled = preferences.Transparency;
        IsDark = !HighContrast && (_state?.Theme switch { "dark" => true, "light" => false, _ => preferences.Dark });
        // Theme and accent belong to WPF-UI. Apply our backdrop policy afterwards:
        // its default watcher would re-enable Mica even when transparency is off.
        var theme = HighContrast ? ApplicationTheme.HighContrast : IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        // High contrast uses the library's contrast palette, never a personal accent.
        if (!HighContrast)
        {
            if (AppearanceOptions.Accents.FirstOrDefault(preset => preset.Value == _state?.Accent) is { } accent)
            {
                // The 4.3.0 explicit-palette overload reads GetAppTheme(). Only a real
                // theme transition needs this preliminary reload; same-theme startup,
                // accent/backdrop changes and notifications all reload once, below.
                if (_application.CurrentTheme != theme) Reload(theme);
                _application.ApplyAccent(accent.Color, theme);
            }
            else _application.ApplySystemAccent(theme); // This overload accepts the target theme directly.
        }
        // Resolve the library's control brushes only after publishing the final palette.
        Reload(theme);
        _backdropFailed = false;
        foreach (var dialog in _dialogs) ApplyDialogBackdrop(dialog);
        Changed?.Invoke();
        BackdropChanged?.Invoke();
        _log.ThemeApplied(source, theme.ToString(), _state?.Accent ?? "system", reloads,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, dictionariesBefore, CountDictionaries(), _dialogs.Count);
    }

    private static int CountDictionaries()
    {
        static int Count(ResourceDictionary dictionary) => 1 + dictionary.MergedDictionaries.Sum(Count);
        return Application.Current is { } application ? Count(application.Resources) : 0;
    }

    internal void ReportBackdrop(bool failed)
    {
        if (_backdropFailed == failed) return;
        _backdropFailed = failed; BackdropChanged?.Invoke();
    }

    internal void TrackDialog(Window window)
    {
        if (window is not FluentWindow dialog || !_dialogs.Add(dialog)) return;
        dialog.SourceInitialized += DialogSourceInitialized;
        dialog.Loaded += DialogLoaded;
        dialog.Closed += DialogClosed;
        ApplyDialogBackdrop(dialog);
    }
    private void DialogSourceInitialized(object? sender, EventArgs e) => ApplyDialogBackdrop((FluentWindow)sender!);
    private void DialogLoaded(object sender, RoutedEventArgs e)
    {
        var dialog = (FluentWindow)sender;
        ApplyDialogBackdrop(dialog);
        // FluentWindow can queue its original material until Loaded. A choice made
        // between source initialization and Show must win after those callbacks too.
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_disposed && _dialogs.Contains(dialog)) ApplyDialogBackdrop(dialog);
        });
    }
    private void DialogClosed(object? sender, EventArgs e) => UntrackDialog((FluentWindow)sender!);
    private void UntrackDialog(FluentWindow dialog)
    {
        _dialogs.Remove(dialog);
        dialog.SourceInitialized -= DialogSourceInitialized; dialog.Loaded -= DialogLoaded; dialog.Closed -= DialogClosed;
    }
    private void ApplyDialogBackdrop(FluentWindow dialog)
    {
        var type = AppearanceOptions.EffectiveBackdrop(Backdrop == "solid" ? "solid" : "mica",
            AppearanceOptions.IsSupported(WindowBackdropType.Mica), TransparencyEnabled, HighContrast);
        dialog.SetCurrentValue(FluentWindow.WindowBackdropTypeProperty, type);
        nint hwnd = new WindowInteropHelper(dialog).Handle;
        if (hwnd == 0) return;
        bool applied = WindowBackdrop.ApplyBackdrop(hwnd, type);
        if (type != WindowBackdropType.None && applied)
        {
            WindowBackdrop.RemoveBackground(dialog); WindowBackdrop.RemoveTitlebarBackground(dialog);
        }
        else
        {
            dialog.SetCurrentValue(FluentWindow.WindowBackdropTypeProperty, WindowBackdropType.None);
            WindowBackdrop.RemoveBackdrop(hwnd);
            dialog.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                target.BackgroundColor = ((SolidColorBrush)dialog.FindResource("ApplicationBackgroundBrush")).Color;
        }
    }

    internal static (bool Dark, bool HighContrast, bool Transparency) ReadPreferences()
    {
        SystemThemeManager.UpdateSystemThemeCache();
        bool dark = ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // WPF-UI 4.3.0 prioritizes the .theme filename. Windows' custom mode can
            // override the app light/dark preference without changing that filename.
            if (key?.GetValue("AppsUseLightTheme") is int light) dark = light == 0;
            return (dark, SystemParameters.HighContrast, key?.GetValue("EnableTransparency") is not int transparency || transparency != 0);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        { return (dark, SystemParameters.HighContrast, false); }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        if (_state is not null) _state.PropertyChanged -= StateChanged;
        foreach (var dialog in _dialogs.ToArray()) UntrackDialog(dialog);
    }
}

internal interface IThemeApplication
{
    ApplicationTheme CurrentTheme { get; }
    void ApplyTheme(ApplicationTheme theme);
    void ApplySystemAccent(ApplicationTheme theme);
    void ApplyAccent(Color color, ApplicationTheme theme);
}

internal sealed class WpfThemeApplication : IThemeApplication
{
    public ApplicationTheme CurrentTheme => ApplicationThemeManager.GetAppTheme();
    // WPF-UI 4.3.0 writes accent overrides into Application.Resources. Remove those
    // overrides when entering high contrast so its theme dictionary can win again.
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary",
        "SystemAccentBrush", "SystemFillColorAttentionBrush", "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush", "AccentFillColorSelectedTextBackgroundBrush", "AccentFillColorDefault", "AccentFillColorDefaultBrush",
        "AccentFillColorSecondary", "AccentFillColorSecondaryBrush", "AccentFillColorTertiary", "AccentFillColorTertiaryBrush",
        "TextOnAccentFillColorPrimary", "TextOnAccentFillColorSecondary", "TextOnAccentFillColorDisabled", "TextOnAccentFillColorSelectedText",
        "AccentTextFillColorDisabled"
    ];
    public void ApplyTheme(ApplicationTheme theme)
    {
        if (theme == ApplicationTheme.HighContrast)
            foreach (string key in AccentKeys) Application.Current.Resources.Remove(key);
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
    }
    public void ApplySystemAccent(ApplicationTheme theme) => ApplicationAccentColorManager.Apply(
        ApplicationAccentColorManager.GetColorizationColor(), theme, systemAccentColor: true);
    public void ApplyAccent(Color color, ApplicationTheme theme)
    {
        // The single-color overload subtracts 45 saturation points from the dark
        // fill variant: our purple has only 46%, so it becomes #E2E2E3. Generate
        // hue-preserving shades through WPF-UI and let its explicit-palette overload
        // update every accent resource. Only ApplySystemAccent uses Windows colors.
        bool dark = theme == ApplicationTheme.Dark;
        ApplicationAccentColorManager.Apply(color, color.UpdateBrightness(dark ? 10 : -10),
            color.UpdateBrightness(dark ? 17 : -25), color.UpdateBrightness(dark ? 25 : -40));
    }
}
