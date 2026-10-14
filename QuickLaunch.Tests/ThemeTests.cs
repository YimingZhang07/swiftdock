using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class ThemeTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void PaletteDefinesEveryXamlBrushAndMatchesDefaults(bool dark, bool highContrast)
    {
        var palette = new ResourceDictionary();
        ThemeService.ApplyPalette(palette, dark, Colors.Teal, highContrast);
        var assembly = typeof(ThemeTests).Assembly;
        var xaml = assembly.GetManifestResourceNames().Where(n => n.EndsWith(".xaml", StringComparison.Ordinal)).Select(n =>
        {
            using var stream = assembly.GetManifestResourceStream(n)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }).ToArray();
        Assert.True(xaml.Length >= 5);
        var keys = xaml.SelectMany(text => Regex.Matches(text, @"\{(?:Dynamic|Static)Resource (\w+Brush)\}").Select(m => m.Groups[1].Value)).Distinct().ToArray();
        Assert.True(keys.Length >= 10);
        var defaults = xaml.Select(XDocument.Parse).SelectMany(d => d.Descendants()).Where(e => e.Name.LocalName == "SolidColorBrush")
            .Select(e => (string)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))!).ToArray();
        Assert.Equal(defaults.Order(), palette.Keys.Cast<string>().Where(k => palette[k] is SolidColorBrush).Order());
        Assert.Equal(highContrast ? 0.0 : dark ? .25 : .14, palette["PopupShadowOpacity"]);
        foreach (string key in keys)
        {
            Assert.Contains(key, defaults);
            var brush = Assert.IsType<SolidColorBrush>(palette[key]);
            Assert.True(brush.IsFrozen);
        }
    }

    [Fact]
    public void HighContrastUsesSystemColorsForEveryToken()
    {
        var expected = new Dictionary<string, Color>
        {
            ["WindowBrush"] = SystemColors.WindowColor, ["LayerBrush"] = SystemColors.WindowColor,
            ["SubtleFillBrush"] = SystemColors.ControlColor, ["SubtlePressedBrush"] = SystemColors.ControlColor,
            ["SelectedFillBrush"] = SystemColors.ControlColor, ["TextBrush"] = SystemColors.WindowTextColor,
            ["MutedBrush"] = SystemColors.WindowTextColor, ["TertiaryBrush"] = SystemColors.GrayTextColor,
            ["BorderBrush"] = SystemColors.WindowTextColor, ["AccentBrush"] = SystemColors.HighlightColor,
            ["AccentFillBrush"] = SystemColors.HighlightColor, ["OnAccentBrush"] = SystemColors.HighlightTextColor,
            ["ErrorBrush"] = SystemColors.WindowTextColor
        };
        foreach (bool dark in new[] { false, true })
        {
            var palette = new ResourceDictionary();
            ThemeService.ApplyPalette(palette, dark, Colors.Teal, true, Colors.Red);
            Assert.Equal(expected.Count, palette.Values.OfType<SolidColorBrush>().Count());
            foreach (var (key, color) in expected) Assert.Equal(color, ((SolidColorBrush)palette[key]).Color);
        }
    }

    [Theory]
    [InlineData(true, "#202020", "#2C2C2C")] [InlineData(false, "#F3F3F3", "#FBFBFB")]
    public void NeutralSurfacesAndSeparateAccentRoles(bool dark, string window, string layer)
    {
        var palette = new ResourceDictionary();
        ThemeService.ApplyPalette(palette, dark, Colors.Black, accentText: Colors.Teal);
        Assert.Equal((Color)ColorConverter.ConvertFromString(window), ((SolidColorBrush)palette["WindowBrush"]).Color);
        Assert.Equal((Color)ColorConverter.ConvertFromString(layer), ((SolidColorBrush)palette["LayerBrush"]).Color);
        Assert.Equal(Colors.Teal, ((SolidColorBrush)palette["AccentBrush"]).Color);
        Assert.Equal(Colors.Black, ((SolidColorBrush)palette["AccentFillBrush"]).Color);
        Assert.Equal(Colors.White, ((SolidColorBrush)palette["OnAccentBrush"]).Color);
        ThemeService.ApplyPalette(palette, dark, Colors.White);
        Assert.Equal(Colors.Black, ((SolidColorBrush)palette["OnAccentBrush"]).Color);
    }
}
