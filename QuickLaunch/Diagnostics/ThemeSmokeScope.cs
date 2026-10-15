using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace QuickLaunch.Diagnostics;

internal sealed class ThemeSmokeScope : IDisposable
{
    private readonly ApplicationTheme _original = ApplicationThemeManager.GetAppTheme();
    private bool _disposed;

    internal static void Apply(bool dark, bool highContrast = false)
    {
        var theme = highContrast ? ApplicationTheme.HighContrast : dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.Apply(Colors.Teal, theme);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ApplicationThemeManager.Apply(_original, WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.ApplySystemAccent();
    }
}
