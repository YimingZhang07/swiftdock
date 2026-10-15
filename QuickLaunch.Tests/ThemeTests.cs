using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
using System.Xml.Linq;
using QuickLaunch.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;

namespace QuickLaunch.Tests;

[Collection("WPF")]
public sealed class ThemeTests
{
    [Fact]
    public Task LibraryDictionariesResolveAllSourceResourcesInEveryTheme() => WpfTestHost.RunAsync(CheckLibraryResourcesAsync);

    private static Task CheckLibraryResourcesAsync()
    {
        var resources = Application.Current.Resources;
        Assert.Contains(resources.MergedDictionaries, dictionary => dictionary.Source?.OriginalString
            .Contains("/Wpf.Ui;component/Resources/Theme/", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(resources.MergedDictionaries, dictionary => dictionary is ControlsDictionary);
        var assembly = typeof(ThemeTests).Assembly;
        string[] Read(string extension) => assembly.GetManifestResourceNames().Where(n => n.EndsWith(extension, StringComparison.Ordinal)).Select(n =>
        {
            using var stream = assembly.GetManifestResourceStream(n)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }).ToArray();
        var xaml = Read(".xaml");
        var code = Read(".cs");
        Assert.True(xaml.Length >= 5 && code.Length >= 50);
        var localKeys = xaml.Select(XDocument.Parse).SelectMany(d => d.Descendants())
            .Select(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .Where(key => key is not null).ToHashSet();
        var keys = xaml.SelectMany(text => Regex.Matches(text, @"\{(?:Dynamic|Static)Resource (\w+)\}").Select(m => m.Groups[1].Value))
            .Concat(code.SelectMany(text => Regex.Matches(text, "(?:FindResource\\(|SetResourceReference\\([^;\\n]*?,\\s*)\"(\\w+)\"").Select(m => m.Groups[1].Value)))
            .Distinct().ToArray();
        Assert.True(keys.Length >= 20);
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark, ApplicationTheme.HighContrast })
        {
            new WpfThemeApplication().ApplyTheme(theme);
            foreach (string key in keys)
            {
                if (localKeys.Contains(key)) continue;
                Assert.True(resources[key] is not null, $"{theme}: missing {key}");
                if (key.EndsWith("Brush", StringComparison.Ordinal)) Assert.IsAssignableFrom<Brush>(resources[key]);
            }
            foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(CheckBox), typeof(ComboBox), typeof(Expander),
                typeof(ContextMenu), typeof(MenuItem), typeof(Separator), typeof(ToolTip), typeof(ScrollBar) })
                Assert.IsType<Style>(resources[type]);
            Assert.IsType<Style>(resources[MenuItem.SeparatorStyleKey]);
        }
        return Task.CompletedTask;
    }

}
