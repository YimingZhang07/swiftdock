using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftdock.Models;
using Swiftdock.Services;
using Swiftdock.ViewModels;
using Swiftdock.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Xunit;

namespace Swiftdock.Tests;

[Collection("WPF")]
public sealed class AppearanceTests
{
    [Fact]
    public Task PanelLayoutScalesLiveWithoutScalingDialogOrScrollbars() => WpfTestHost.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-scale-layout-" + Guid.NewGuid().ToString("N"));
        await using var store = ConfigService.Open(directory);
        using var state = new MainViewModel(store);
        state.SetAutoFetchWebsiteIcons(false); state.SetWindowSize(new(620, 350));
        using var services = new ServiceCollection().AddLogging().AddIconServices(Path.Combine(directory, "icons")).BuildServiceProvider();
        var icons = services.GetRequiredService<IconService>();
        var dialogs = new DialogService(services);
        LauncherWindow? window = null;
        using var launcher = new LauncherViewModel(state, new LauncherDialogs(dialogs, () => window!),
            new LauncherActions(icons, (_, _) => Task.FromResult(new LaunchResult(true, false, "test"))), NullLogger<LauncherViewModel>.Instance);
        window = new LauncherWindow(NullLogger<LauncherWindow>.Instance, launcher, icons, dialogs) { Width = 620, Height = 350 };
        var root = (Grid)window.Content;
        using var settings = new SettingsViewModel(state,
            new StartupService(@"C:\Swiftdock\Swiftdock.exe", directory, "Tests", _ => null, (_, _) => { }),
            new SettingsViewModelTests.Hotkey(), new SettingsViewModelTests.Actions(), NullLogger<SettingsViewModel>.Instance);
        var normalDialog = new SettingsAppearancePage { DataContext = settings };
        var drag = new ContentControl { ContentTemplate = (DataTemplate)window.Resources["TileDragAdorner"], Content = state.Groups[0].Items[0] };
        try
        {
            foreach (int scale in new[] { 80, 100, 150, 100 })
            {
                state.SetScale(scale);
                var size = new Size(window.Width, window.Height);
                root.Measure(size); root.Arrange(new Rect(size));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); root.UpdateLayout();
                double factor = scale / 100d;
                var tile = (TileListBoxItem)window.SelectedList.ItemContainerGenerator.ContainerFromIndex(0);
                var label = (TextBlock)tile.Template.FindName("TileLabel", tile);
                // GetChildren includes raw peers; the label is excluded from control view.
                // E2E must use RawViewWalker rather than a control-view descendant search.
                var listPeer = UIElementAutomationPeer.CreatePeerForElement(window.SelectedList);
                var tilePeer = listPeer.GetChildren()[0];
                var labelPeer = Assert.Single(tilePeer.GetChildren(), peer => peer.GetAutomationId() == "TileLabel");
                Assert.Equal(AutomationControlType.Text, labelPeer.GetAutomationControlType());
                Assert.False(labelPeer.IsControlElement());
                Assert.Equal(88 * factor, tile.Width, 8);
                Assert.Equal(12 * factor, label.FontSize, 8); Assert.Equal(28 * factor, label.Height, 8);
                Assert.Equal(48 * factor, root.RowDefinitions[0].Height.Value, 8);
                Assert.Equal(32 * factor, ((Button)window.FindName("SettingsButton")).Width, 8);
                var panel = Assert.Single(LauncherWindow.Descendants<TilePanel>(window.SelectedList));
                Assert.Equal(6, panel.Geometry.Columns);
                Assert.Equal(state.CellSize, panel.Geometry.RowHeight, 8);
                Assert.Equal(92 * factor, panel.ItemWidth, 8);
                Assert.Equal(14, LauncherWindow.Descendants<System.Windows.Controls.Primitives.ScrollBar>(root)
                    .First(bar => bar.Orientation == Orientation.Vertical).Width);
                normalDialog.Measure(new Size(590, 430)); normalDialog.Arrange(new Rect(0, 0, 590, 430)); normalDialog.UpdateLayout();
                Assert.Equal(20, LauncherWindow.Descendants<TextBlock>(normalDialog)
                    .Single(text => System.Windows.Automation.AutomationProperties.GetAutomationId(text) == "SettingsAppearanceTitle").FontSize);
                drag.Measure(new Size(400, 200)); drag.Arrange(new Rect(0, 0, 400, 200)); drag.UpdateLayout();
                Assert.Equal(10 * factor, LauncherWindow.Descendants<Border>(drag).First().Padding.Left, 8);
                Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            }
        }
        finally { state.Suspend(true); window.Dispose(); }
    });

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("system", "acrylic", "system")]
    [InlineData("Light", "Mica", "Blue")]
    [InlineData("unknown", "unknown", "#0067C0")]
    [InlineData("", "", "")]
    public void DefaultsAndInvalidValuesAreOmittedWithoutChangingOtherSettings(string? theme, string? backdrop, string? accent)
    {
        var original = AppConfig.CreateDefault();
        var normalized = ConfigValidation.Normalize(original with { Theme = theme, Backdrop = backdrop, Accent = accent });
        Assert.Equal(original, normalized);
        var json = JsonSerializer.Serialize(normalized, ConfigJsonContext.Default.AppConfig);
        Assert.DoesNotContain("\"theme\"", json); Assert.DoesNotContain("\"backdrop\"", json); Assert.DoesNotContain("\"accent\"", json);
    }

    [Fact]
    public void EveryAppearanceCombinationRoundtrips()
    {
        foreach (string? theme in new[] { null, "light", "dark" })
        foreach (string? backdrop in new[] { null, "mica", "solid" })
        foreach (string? accent in AppearanceOptions.Accents.Select(p => p.Value).Prepend(null))
        {
            var original = AppConfig.CreateDefault() with { Theme = theme, Backdrop = backdrop, Accent = accent };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig);
            var loaded = ConfigValidation.Parse(bytes);
            Assert.Equal(theme, loaded.Theme); Assert.Equal(backdrop, loaded.Backdrop); Assert.Equal(accent, loaded.Accent);
            Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(loaded, ConfigJsonContext.Default.AppConfig));
        }
    }

    [Fact]
    public Task LivePreferencesPersistResetAndRespectReadOnly() => WpfTestHost.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-appearance-" + Guid.NewGuid().ToString("N"));
        await using var store = ConfigService.Open(directory);
        using var model = new MainViewModel(store);
        model.SetTheme("dark"); model.SetBackdrop("mica"); model.SetAccent("purple");
        Assert.Equal(3, model.Revision);
        model.SetTheme("dark"); model.SetBackdrop("mica"); model.SetAccent("purple");
        Assert.Equal(3, model.Revision);
        await model.FlushAsync();
        var saved = ConfigValidation.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "config.json")));
        Assert.Equal("dark", saved.Theme); Assert.Equal("mica", saved.Backdrop); Assert.Equal("purple", saved.Accent);
        await using (var readOnly = ConfigService.Open(directory, readOnly: true))
        using (var locked = new MainViewModel(readOnly))
        {
            locked.SetTheme("light"); locked.SetBackdrop("solid"); locked.SetAccent("blue");
            Assert.Equal("dark", locked.Theme); Assert.Equal("mica", locked.Backdrop); Assert.Equal("purple", locked.Accent);
            Assert.Equal(0, locked.Revision);
        }
        model.SetTheme("system"); model.SetBackdrop("acrylic"); model.SetAccent("system");
        await model.FlushAsync();
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "config.json"));
        Assert.DoesNotContain("\"theme\"", json); Assert.DoesNotContain("\"backdrop\"", json); Assert.DoesNotContain("\"accent\"", json);
    });

    [Fact]
    public Task ThemeServiceCallsLibraryAdapterAndHighContrastOverridesChoices() => WpfTestHost.RunAsync(async () =>
    {
        await using var store = ConfigService.Open(Path.Combine(Path.GetTempPath(), "Swiftdock-theme-calls-" + Guid.NewGuid().ToString("N")));
        using var state = new MainViewModel(store);
        var calls = new ThemeCalls();
        var preferences = (Dark: true, HighContrast: false, Transparency: true);
        using var theme = new ThemeService(Dispatcher.CurrentDispatcher, state, calls, () => preferences);
        Assert.Equal(["system-accent:Dark", "theme:Dark"], calls.Calls);
        calls.Calls.Clear(); state.SetTheme("light");
        Assert.Equal(["system-accent:Light", "theme:Light"], calls.Calls);
        foreach (var preset in AppearanceOptions.Accents)
        {
            calls.Calls.Clear(); state.SetAccent(preset.Value);
            Assert.Equal([$"accent:{preset.Color}:Light", "theme:Light"], calls.Calls);
        }
        calls.Calls.Clear(); state.SetTheme("dark");
        Assert.Equal(["theme:Dark", $"accent:{AppearanceOptions.Accents[^1].Color}:Dark", "theme:Dark"], calls.Calls);
        preferences = (false, false, true); theme.Apply(); Assert.True(theme.IsDark);
        state.SetTheme("system"); Assert.False(theme.IsDark);
        calls.Calls.Clear(); preferences = (true, true, true); theme.Apply();
        Assert.Equal(["theme:HighContrast"], calls.Calls); Assert.False(theme.IsDark);
        Assert.Contains("高对比度", theme.BackdropDescription);
        calls.Calls.Clear(); state.SetTheme("dark"); state.SetAccent("purple");
        Assert.Equal(["theme:HighContrast", "theme:HighContrast"], calls.Calls);
        calls.Calls.Clear(); preferences = (false, false, false); theme.Apply();
        Assert.Equal(["theme:Dark", $"accent:{AppearanceOptions.Accents[3].Color}:Dark", "theme:Dark"], calls.Calls);
        Assert.Contains("透明效果已关闭", theme.BackdropDescription);
        calls.Calls.Clear(); theme.Dispose(); state.SetAccent("blue");
        Assert.Empty(calls.Calls);
    });

    [Fact]
    public Task RealLibraryGeneratesVariantsAndClearsPersonalAccentInHighContrast() => WpfTestHost.RunAsync(() =>
    {
        var application = new WpfThemeApplication();
        Color? light = null;
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            application.ApplyTheme(theme); application.ApplyAccent(AppearanceOptions.Accents[3].Color, theme);
            Assert.Equal(theme, ApplicationThemeManager.GetAppTheme());
            Assert.Equal(AppearanceOptions.Accents[3].Color, ApplicationAccentColorManager.SystemAccent);
            var fill = ((SolidColorBrush)Application.Current.FindResource("AccentFillColorDefaultBrush")).Color;
            if (light is null) light = fill; else Assert.NotEqual(light, fill);
        }
        application.ApplyTheme(ApplicationTheme.HighContrast);
        Assert.Equal(ApplicationTheme.HighContrast, ApplicationThemeManager.GetAppTheme());
        Assert.DoesNotContain("AccentFillColorDefaultBrush", Application.Current.Resources.Keys.Cast<object>());
        Assert.IsAssignableFrom<Brush>(Application.Current.FindResource("AccentFillColorDefaultBrush"));
        application.ApplyTheme(ApplicationTheme.Light); application.ApplySystemAccent(ApplicationTheme.Light);
        Assert.Equal(ApplicationAccentColorManager.GetColorizationColor(), ApplicationAccentColorManager.SystemAccent);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("acrylic", true, true, false, WindowBackdropType.Acrylic)]
    [InlineData("mica", true, true, false, WindowBackdropType.Mica)]
    [InlineData("solid", true, true, false, WindowBackdropType.None)]
    [InlineData("acrylic", false, true, false, WindowBackdropType.None)]
    [InlineData("mica", false, true, false, WindowBackdropType.None)]
    [InlineData("acrylic", true, false, false, WindowBackdropType.None)]
    [InlineData("mica", true, false, false, WindowBackdropType.None)]
    [InlineData("acrylic", true, true, true, WindowBackdropType.None)]
    [InlineData("mica", true, true, true, WindowBackdropType.None)]
    public void BackdropPolicyPreservesFallbacks(string value, bool supported, bool transparency, bool contrast, WindowBackdropType expected)
    {
        Assert.Equal(expected, AppearanceOptions.EffectiveBackdrop(value, supported, transparency, contrast));
        Assert.Equal(WindowBackdropType.None, AppearanceOptions.EffectiveBackdrop(value, supported, transparency, contrast, warming: true));
        if (expected == WindowBackdropType.None) Assert.Contains("纯色", AppearanceOptions.DescribeBackdrop(value, supported, transparency, contrast));
    }

    private sealed class ThemeCalls : IThemeApplication
    {
        public ApplicationTheme CurrentTheme { get; private set; } = ApplicationTheme.Unknown;
        internal List<string> Calls { get; } = [];
        public void ApplyTheme(ApplicationTheme theme) { CurrentTheme = theme; Calls.Add($"theme:{theme}"); }
        public void ApplySystemAccent(ApplicationTheme theme) => Calls.Add($"system-accent:{theme}");
        public void ApplyAccent(Color color, ApplicationTheme theme) => Calls.Add($"accent:{color}:{theme}");
    }
}
