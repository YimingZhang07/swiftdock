using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace QuickLaunch.Services;

public sealed class ThemeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private UISettings? _settings;
    private bool _disposed;
    public bool IsDark { get; private set; }
    public bool HighContrast { get; private set; }
    public bool TransparencyEnabled { get; private set; }
    public event Action? Changed;
    public ThemeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        try { _settings = new(); _settings.ColorValuesChanged += ColorsChanged; } catch { }
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        Apply();
    }
    private void ColorsChanged(UISettings sender, object args) => QueueUpdate();
    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs args) => QueueUpdate();
    public void QueueUpdate() { if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(Apply); }
    private void Apply()
    {
        if (_disposed) return;
        (IsDark, HighContrast, TransparencyEnabled) = ReadPreferences();
        Color accent = Color.FromRgb(36, 133, 120);
        Color ReadAccent(UIColorType type, Color fallback)
        {
            try { if (_settings is not null) { var color = _settings.GetColorValue(type); return Color.FromRgb(color.R, color.G, color.B); } } catch { }
            return fallback;
        }
        accent = ReadAccent(UIColorType.Accent, accent);
        ApplyPalette(Application.Current.Resources, IsDark, accent, HighContrast,
            ReadAccent(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1, accent));
        Changed?.Invoke();
    }
    internal static (bool Dark, bool HighContrast, bool Transparency) ReadPreferences()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue("AppsUseLightTheme") is int light && light == 0, SystemParameters.HighContrast,
                key?.GetValue("EnableTransparency") is not int transparency || transparency != 0);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        { return (false, SystemParameters.HighContrast, false); }
    }
    public static void ApplyPalette(ResourceDictionary resources, bool dark, Color accent, bool highContrast = false, Color? accentText = null)
    {
        resources["PopupShadowOpacity"] = highContrast ? 0.0 : dark ? .25 : .14;
        void Brush(string key, Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); resources[key] = brush; }
        Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
        Brush("WindowBrush", highContrast ? SystemColors.WindowColor : Parse(dark ? "#202020" : "#F3F3F3"));
        Brush("LayerBrush", highContrast ? SystemColors.WindowColor : Parse(dark ? "#2C2C2C" : "#FBFBFB"));
        Brush("SubtleFillBrush", highContrast ? SystemColors.ControlColor : Parse(dark ? "#0FFFFFFF" : "#0A000000"));
        Brush("SubtlePressedBrush", highContrast ? SystemColors.ControlColor : Parse(dark ? "#0AFFFFFF" : "#06000000"));
        Brush("SelectedFillBrush", highContrast ? SystemColors.ControlColor : Parse(dark ? "#15FFFFFF" : "#0F000000"));
        Brush("TextBrush", highContrast ? SystemColors.WindowTextColor : Parse(dark ? "#FFFFFF" : "#1A1A1A"));
        Brush("MutedBrush", highContrast ? SystemColors.WindowTextColor : Parse(dark ? "#C5C5C5" : "#5F5F5F"));
        Brush("TertiaryBrush", highContrast ? SystemColors.GrayTextColor : Parse(dark ? "#9A9A9A" : "#8A8A8A"));
        Brush("BorderBrush", highContrast ? SystemColors.WindowTextColor : Parse(dark ? "#14FFFFFF" : "#0F000000"));
        Brush("AccentBrush", highContrast ? SystemColors.HighlightColor : accentText ?? accent);
        Brush("AccentFillBrush", highContrast ? SystemColors.HighlightColor : accent);
        Brush("OnAccentBrush", highContrast ? SystemColors.HighlightTextColor : AccentForeground(accent));
        Brush("ErrorBrush", highContrast ? SystemColors.WindowTextColor : Parse(dark ? "#FF99A4" : "#C42B1C"));
    }
    private static Color AccentForeground(Color accent)
    {
        double Linear(byte value) { double channel = value / 255.0; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
        double luminance = .2126 * Linear(accent.R) + .7152 * Linear(accent.G) + .0722 * Linear(accent.B);
        return luminance > .179 ? Colors.Black : Colors.White;
    }
    public void Dispose()
    {
        _disposed = true; SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        if (_settings is not null) _settings.ColorValuesChanged -= ColorsChanged;
    }
}
