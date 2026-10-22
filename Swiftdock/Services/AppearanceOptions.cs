using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Swiftdock.Services;

public sealed record AccentPreset(string Value, UiText Label, string Hex)
{
    public string Name => Label.Value;
    public Color Color => (Color)ColorConverter.ConvertFromString(Hex);
}

public static class AppearanceOptions
{
    public static IReadOnlyList<AccentPreset> Accents { get; } = Array.AsReadOnly<AccentPreset>(
    [
        new("blue", L.Text("Blue"), "#0067C0"), new("teal", L.Text("Teal"), "#038387"), new("green", L.Text("Green"), "#107C10"),
        new("purple", L.Text("Purple"), "#8764B8"), new("magenta", L.Text("Magenta"), "#C239B3"), new("orange", L.Text("Orange"), "#CA5010"),
        new("slate", L.Text("Slate"), "#69797E")
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
        value == "solid" ? L.Text("SolidBackgroundsForPanelAndDialogs") : highContrast ? L.Text("UsingSolidWindowsHighContrastIsOn")
        : !transparency ? L.Text("UsingSolidWindowsTransparencyIsOff") : !supported ? L.Text("UsingSolidMaterialIsNotSupported")
        : failed ? L.Text("UsingSolidMaterialIsUnavailable") : L.Text("PanelMaterialDialogsUseMica");
}
