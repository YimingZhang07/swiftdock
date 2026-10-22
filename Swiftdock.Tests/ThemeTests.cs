using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using System.Xml.Linq;
using Swiftdock.Services;
using Swiftdock.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Extensions;
using Wpf.Ui.Markup;
using Xunit;
using Xunit.Abstractions;

namespace Swiftdock.Tests;

[Collection("WPF")]
public sealed class ThemeTests(ITestOutputHelper output)
{
    [Fact]
    public Task WpfHostRestoresThemeAndAccentEvenWhenATestFails() => WpfTestHost.RunAsync(async () =>
    {
        var current = Assert.IsType<Application>(Application.Current); // Product App must never own the test dispatcher.
        Assert.Equal(ShutdownMode.OnExplicitShutdown, current.ShutdownMode);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(current, Application.Current);
        static void Apply(string theme, string accent)
        {
            var state = new SettingsViewModelTests.SettingsState();
            state.SetTheme(theme); state.SetAccent(accent);
            using var appearance = new ThemeService(Dispatcher.CurrentDispatcher, state, new WpfThemeApplication(), () => (false, false, true));
        }
        // Exercise a baseline with no local accent overrides as well as merged brushes.
        var application = new WpfThemeApplication();
        application.ApplyTheme(ApplicationTheme.HighContrast);
        application.ApplyTheme(ApplicationTheme.Light);
        async Task<Color[]> Colors()
        {
            var toggle = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = true };
            var window = new Window { Content = toggle };
            try
            {
                toggle.Measure(new Size(100, 40)); toggle.Arrange(new Rect(0, 0, 100, 40));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                toggle.UpdateLayout();
                return [ApplicationAccentColorManager.SystemAccent, ApplicationAccentColorManager.PrimaryAccent,
                    ((SolidColorBrush)toggle.Background).Color];
            }
            finally { window.Close(); }
        }
        var before = await Colors();
        await Assert.ThrowsAsync<InvalidOperationException>(() => WpfTestHost.RunAsync(async () =>
        {
            Apply("dark", "purple");
            Assert.NotEqual(before[0], (await Colors())[0]);
            throw new InvalidOperationException("Exercise failed-test cleanup.");
        }));
        Assert.Equal(ApplicationTheme.Light, ApplicationThemeManager.GetAppTheme());
        Assert.Equal(before, await Colors());
        Assert.Same(current, Application.Current);
        Assert.False(current.Dispatcher.HasShutdownStarted);
    });

    [Fact]
    public Task CommonAppliesReloadOnceWithoutGrowingDictionaries() => WpfTestHost.RunAsync(async () =>
    {
        var state = new SettingsViewModelTests.SettingsState();
        state.SetTheme("dark"); state.SetAccent("blue");
        using (var setup = new ThemeService(Dispatcher.CurrentDispatcher, state, new WpfThemeApplication(), () => (true, false, true))) { }
        state.SetAccent("system"); // The measured startup below intentionally exercises system accent.
        var toggle = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = true };
        var content = new StackPanel { Children = { toggle, new Slider { Value = 5 } } };
        var window = new Window { Content = content };
        content.Measure(new Size(300, 100)); content.Arrange(new Rect(0, 0, 300, 100)); content.UpdateLayout();
        static int DictionaryCount(ResourceDictionary root) => 1 + root.MergedDictionaries.Sum(DictionaryCount);
        int dictionaries = DictionaryCount(Application.Current.Resources), reloads = 0;
        var application = new MeasuredThemeApplication();
        void Reloaded(ApplicationTheme _, Color __) => reloads++;
        ApplicationThemeManager.Changed += Reloaded;
        var measured = new List<int>();
        var watch = Stopwatch.StartNew();
        void Record(string phase)
        {
            int count = DictionaryCount(Application.Current.Resources);
            output.WriteLine($"{phase}: {watch.Elapsed.TotalMilliseconds:F2} ms; ApplyTheme calls={application.Reloads}; Changed events={reloads}; dictionaries={count}");
            Assert.Equal(dictionaries, count);
            measured.Add(application.Reloads); application.Reloads = 0; reloads = 0; watch.Restart();
        }
        try
        {
            using var theme = new ThemeService(Dispatcher.CurrentDispatcher, state, application, () => (true, false, true));
            Record("startup/system");
            state.SetAccent("purple"); Record("accent/purple");
            state.SetBackdrop("mica"); Record("backdrop/mica");
            theme.QueueUpdate(); theme.QueueUpdate(); theme.QueueUpdate();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Record("three queued notifications");
            for (int index = 0; index < 12; index++) { theme.Apply(); Record($"repeat/{index}"); }
            Assert.All(measured, count => Assert.Equal(1, count));
        }
        finally { ApplicationThemeManager.Changed -= Reloaded; window.Close(); }
    });

    private sealed class MeasuredThemeApplication : IThemeApplication
    {
        private readonly WpfThemeApplication _inner = new();
        public ApplicationTheme CurrentTheme => _inner.CurrentTheme;
        public int Reloads { get; set; }
        public void ApplyTheme(ApplicationTheme theme) { Reloads++; _inner.ApplyTheme(theme); }
        public void ApplyAccent(Color color, ApplicationTheme theme) => _inner.ApplyAccent(color, theme);
        public void ApplySystemAccent(ApplicationTheme theme) => _inner.ApplySystemAccent(theme);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExistingControlFillsFollowAccentThenTheme(bool switchTheme) => WpfTestHost.RunAsync(async () =>
    {
        var state = new SettingsViewModelTests.SettingsState();
        state.SetTheme(switchTheme ? "light" : "dark");
        state.SetAccent("orange");
        using var theme = new ThemeService(Dispatcher.CurrentDispatcher, state, new WpfThemeApplication(), () => (false, false, true));
        var toggle = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = true };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 65, Width = 200 };
        var content = new StackPanel { Children = { toggle, slider } };
        // Keep a real Window resource/invalidation parent, but never Show or create an HWND.
        var window = new Window { Content = content };
        async Task<Color[]> RenderFills()
        {
            content.Measure(new Size(300, 100)); content.Arrange(new Rect(0, 0, 300, 100));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            content.UpdateLayout();
            new RenderTargetBitmap(300, 100, 96, 96, PixelFormats.Pbgra32).Render(content);
            var checkedFill = (Rectangle)toggle.Template.FindName("ActiveToggleRectangle", toggle);
            // WPF-UI 4.3.0's slider has a neutral track and an accent-filled thumb;
            // there is no separate value-fill segment in its shipped template.
            var thumbFill = LauncherWindow.Descendants<Ellipse>(slider).Single();
            return [((SolidColorBrush)checkedFill.Fill).Color, ((SolidColorBrush)thumbFill.Fill).Color];
        }
        try
        {
            var original = await RenderFills(); // Resolve both existing templates before changing accent.
            state.SetAccent("purple");
            if (switchTheme) state.SetTheme("dark");
            var expected = AppearanceOptions.Accents.Single(a => a.Value == "purple").Color.UpdateBrightness(10);
            var actual = await RenderFills();
            Assert.All(original, color => Assert.NotEqual(expected, color));
            Assert.Equal(new[] { expected, expected }, actual);
            // The direct launcher brush uses the explicit secondary dark shade, never grey.
            Assert.Equal(AppearanceOptions.Accents.Single(a => a.Value == "purple").Color.UpdateBrightness(17),
                ((SolidColorBrush)window.FindResource("AccentFillColorDefaultBrush")).Color);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LibraryDictionariesResolveAllSourceResourcesInEveryTheme() => WpfTestHost.RunAsync(CheckLibraryResourcesAsync);

    private static Task CheckLibraryResourcesAsync()
    {
        var state = new SettingsViewModelTests.SettingsState();
        state.SetAccent("blue");
        var preferences = (Dark: false, HighContrast: false, Transparency: true);
        using var appearance = new ThemeService(Dispatcher.CurrentDispatcher, state, new WpfThemeApplication(), () => preferences);
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
            preferences = (theme == ApplicationTheme.Dark, theme == ApplicationTheme.HighContrast, true);
            state.SetTheme(theme == ApplicationTheme.Dark ? "dark" : "light");
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
