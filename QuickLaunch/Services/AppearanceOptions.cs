using System.Windows.Media;
using Wpf.Ui.Controls;

namespace QuickLaunch.Services;

public sealed record AccentPreset(string Value, string Name, string Hex)
{
    public Color Color => (Color)ColorConverter.ConvertFromString(Hex);
}

public static class AppearanceOptions
{
    public static IReadOnlyList<AccentPreset> Accents { get; } = Array.AsReadOnly<AccentPreset>(
    [
        new("blue", "蓝", "#0067C0"), new("teal", "青", "#038387"), new("green", "绿", "#107C10"),
        new("purple", "紫", "#8764B8"), new("magenta", "玫红", "#C239B3"), new("orange", "橙", "#CA5010"),
        new("slate", "石板灰", "#69797E")
    ]);

    public static string? NormalizeTheme(string? value) => value is "light" or "dark" ? value : null;
    public static string? NormalizeBackdrop(string? value) => value is "mica" or "solid" ? value : null;
    public static string? NormalizeAccent(string? value) => Accents.Any(preset => preset.Value == value) ? value : null;
    internal static Color OverlayColor(Color background, int opacity) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(opacity, 0, 100) * 255.0 / 100, MidpointRounding.AwayFromZero), background.R, background.G, background.B);
    // Only non-client painting is overridden. WM_ACTIVATE/WM_SETFOCUS still reach WPF.
    internal static bool KeepNonClientActive(int message, bool enabled, string effectiveBackdrop, bool visible, bool warming) =>
        message == Interop.NativeMethods.WmNcActivate && enabled && effectiveBackdrop is "acrylic" or "mica" && visible && !warming;
    internal static WindowBackdropType BackdropType(string value) => value switch
    {
        "solid" => WindowBackdropType.None, "mica" => WindowBackdropType.Mica, _ => WindowBackdropType.Acrylic
    };
    internal static bool IsSupported(WindowBackdropType type) =>
        (type != WindowBackdropType.Acrylic || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) && WindowBackdrop.IsSupported(type);
    internal static WindowBackdropType EffectiveBackdrop(string value, bool supported, bool transparency, bool highContrast, bool warming = false) =>
        supported && transparency && !highContrast && !warming ? BackdropType(value) : WindowBackdropType.None;
    internal static string DescribeBackdrop(string value, bool supported, bool transparency, bool highContrast, bool failed = false) =>
        value == "solid" ? "面板和对话框均使用纯色背景" : highContrast ? "当前使用纯色：Windows 高对比度已开启"
        : !transparency ? "当前使用纯色：Windows 透明效果已关闭" : !supported ? "当前使用纯色：系统不支持所选材质"
        : failed ? "当前使用纯色：背景材质暂不可用" : "应用于面板；对话框使用云母背景";
}
