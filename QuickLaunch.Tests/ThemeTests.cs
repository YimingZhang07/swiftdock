using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using QuickLaunch.Diagnostics;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;
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
            .Concat(code.SelectMany(text => Regex.Matches(text, "\"(\\w+Brush)\"").Select(m => m.Groups[1].Value)))
            .Concat(code.SelectMany(text => Regex.Matches(text, "(?:FindResource\\(|SetResourceReference\\([^;\\n]*?,\\s*)\"(\\w+)\"").Select(m => m.Groups[1].Value)))
            .Distinct().ToArray();
        Assert.True(keys.Length >= 20);
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark, ApplicationTheme.HighContrast })
        {
            ThemeSmokeScope.Apply(theme == ApplicationTheme.Dark, theme == ApplicationTheme.HighContrast);
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

    [Fact]
    public Task ThemeChangesReachLauncherAndMenusUseLibraryTemplates() => WpfTestHost.RunAsync(CheckLauncherThemeAsync);

    private static async Task CheckLauncherThemeAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-theme-" + Guid.NewGuid().ToString("N"));
        await using var log = new DiagnosticLog(directory);
        await using var store = ConfigService.Open(directory, readOnly: true);
        using var icons = new IconService(Path.Combine(directory, "icons"), log, new FaviconFixtureHandler());
        var model = new MainViewModel(store);
        var window = new LauncherWindow(log, model, icons) { GetPlacementDesktop = _ => LauncherInputSmoke.Desktop };
        try
        {
            window.InitializeNative(); window.ShowPanel("theme-test", forDrop: true);
            Color? previous = null;
            foreach (var theme in new[] { ApplicationTheme.Dark, ApplicationTheme.Light, ApplicationTheme.HighContrast })
            {
                bool dark = theme == ApplicationTheme.Dark, contrast = theme == ApplicationTheme.HighContrast;
                ThemeSmokeScope.Apply(dark, contrast);
                window.ApplyBackdrop(contrast, false);
                window.UpdateLayout();
                Assert.Equal("solid", window.BackdropMode);
                Assert.Same(window.FindResource("ApplicationBackgroundBrush"), window.Background);
                Assert.Same(window.Background, window.CaptureRoot.Background);
                var tile = (ListBoxItem)window.SelectedList.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.Same(window.FindResource("TextFillColorPrimaryBrush"), tile.Foreground);
                var ring = (Border)tile.Template.FindName("SelectionRing", tile);
                Assert.Same(window.FindResource("AccentTextFillColorPrimaryBrush"), ring.BorderBrush);
                var color = ((SolidColorBrush)tile.Foreground).Color;
                if (theme == ApplicationTheme.Light) Assert.NotEqual(previous, color);
                previous = color;
                await LauncherInputSmoke.CheckMenu(window, tile.ContextMenu, tile, "item", directory,
                    (name, passed) => Assert.True(passed, name));
                // H.NotifyIcon receives an ordinary ContextMenu created in App.CreateTray.
                var trayMenu = new ContextMenu();
                trayMenu.Items.Add(new MenuItem { Header = "显示 QuickLaunch" });
                trayMenu.Items.Add(new Separator());
                trayMenu.Items.Add(new MenuItem { Header = "退出" });
                await LauncherInputSmoke.CheckMenu(window, trayMenu, tile, "tray", directory,
                    (name, passed) => Assert.True(passed, name));
            }
        }
        finally { model.Suspend(true); window.PrepareExit(); window.Close(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ThemeTestsRestoreStateInEitherOrder(bool reverse) => WpfTestHost.RunAsync(async () =>
    {
        // Start after a real theme switch: WPF-UI has already replaced ThemesDictionary
        // with a plain ResourceDictionary, and a custom accent must survive cleanup too.
        ThemeSmokeScope.Apply(true);
        var theme = ApplicationThemeManager.GetAppTheme();
        var resources = Application.Current.Resources;
        var dictionaries = resources.MergedDictionaries.ToArray();
        var entries = resources.Cast<DictionaryEntry>().ToArray();
        void AssertRestored()
        {
            Assert.Equal(theme, ApplicationThemeManager.GetAppTheme());
            Assert.Same(resources, Application.Current.Resources);
            Assert.Equal(dictionaries.Length, resources.MergedDictionaries.Count);
            for (int i = 0; i < dictionaries.Length; i++) Assert.Same(dictionaries[i], resources.MergedDictionaries[i]);
            Assert.Equal(entries.Length, resources.Count);
            foreach (var entry in entries) Assert.Equal(entry.Value, resources[entry.Key]);
        }

        Func<Task>[] checks = [CheckLibraryResourcesAsync, CheckLauncherThemeAsync];
        if (reverse) Array.Reverse(checks);
        foreach (var check in checks)
        {
            await WpfTestHost.RunAsync(check);
            AssertRestored();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => WpfTestHost.RunAsync(() =>
        {
            ThemeSmokeScope.Apply(false);
            throw new InvalidOperationException("Exercise theme cleanup after a failing test.");
        }));
        AssertRestored();
    });

    [Theory]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, false, false, true)]
    public void AcrylicRequiresSupportTransparencyAndNormalContrast(bool supported, bool transparency, bool highContrast, bool warming, bool expected) =>
        Assert.Equal(expected, LauncherWindow.CanUseAcrylic(supported, transparency, highContrast, warming));
}
