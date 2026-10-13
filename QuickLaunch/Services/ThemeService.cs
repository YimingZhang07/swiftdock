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
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            IsDark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { IsDark = false; }
        Color accent = Color.FromRgb(36, 133, 120);
        try { if (_settings is not null) { var color = _settings.GetColorValue(UIColorType.Accent); accent = Color.FromRgb(color.R, color.G, color.B); } } catch { }
        ApplyPalette(Application.Current.Resources, IsDark, accent, SystemParameters.HighContrast);
    }
    public static void ApplyPalette(ResourceDictionary resources, bool dark, Color accent, bool highContrast = false)
    {
        void Brush(string key, Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); resources[key] = brush; }
        Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
        Brush("WindowBrush", highContrast ? SystemColors.WindowColor : Parse(dark ? "#202124" : "#F7F7F8"));
        Brush("PanelBrush", highContrast ? SystemColors.WindowColor : Parse(dark ? "#30343B" : "#E9EDF1"));
        Brush("HoverBrush", highContrast ? SystemColors.HighlightColor : Parse(dark ? "#414954" : "#D7E4E9"));
        Brush("TextBrush", highContrast ? SystemColors.WindowTextColor : Parse(dark ? "#F0F3F7" : "#20262D"));
        Brush("MutedBrush", highContrast ? SystemColors.WindowTextColor : Parse(dark ? "#B3BFCD" : "#586777"));
        Brush("BorderBrush", highContrast ? SystemColors.WindowTextColor : Parse(dark ? "#49535F" : "#C9D0D7"));
        Brush("AccentBrush", highContrast ? SystemColors.HighlightColor : accent);
        Brush("ErrorBrush", Parse(dark ? "#FF9A90" : "#B3261E"));
    }
    public void Dispose()
    {
        _disposed = true; SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        if (_settings is not null) _settings.ColorValuesChanged -= ColorsChanged;
    }
}
