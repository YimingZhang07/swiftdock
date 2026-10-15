using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace QuickLaunch.Services;

public sealed class ThemeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _disposed, _queued;
    public bool IsDark { get; private set; }
    public bool HighContrast { get; private set; }
    public bool TransparencyEnabled { get; private set; }
    public event Action? Changed;

    public ThemeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        Apply();
    }

    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs args) => QueueUpdate();
    public void QueueUpdate()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _queued) return;
            _queued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _queued = false; Apply(); });
        });
    }

    private void Apply()
    {
        if (_disposed) return;
        (IsDark, HighContrast, TransparencyEnabled) = ReadPreferences();
        // Theme and accent belong to WPF-UI. Apply our backdrop policy afterwards:
        // its default watcher would re-enable Mica even when transparency is off.
        ApplicationThemeManager.Apply(HighContrast ? ApplicationTheme.HighContrast : IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.ApplySystemAccent();
        Changed?.Invoke();
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
    }
}
