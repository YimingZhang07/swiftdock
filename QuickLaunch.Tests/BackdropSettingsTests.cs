using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using QuickLaunch.Interop;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.Snapshots;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;
using Wpf.Ui.Appearance;
using Xunit;

namespace QuickLaunch.Tests;

[Collection("WPF")]
public sealed class BackdropSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonClientActivationSuppressesFramePaintingAndAllowsDeactivation(bool active)
    {
        var calls = new List<(nint Window, int Message, nint Active, nint Paint)>();
        nint DefaultProc(nint hwnd, int message, nint wParam, nint lParam)
        { calls.Add((hwnd, message, wParam, lParam)); return 42; }
        Assert.Equal((nint)42, LauncherWindow.RefreshNonClientActivation(123, active, DefaultProc));
        Assert.Equal(active ? (nint)42 : 1, LauncherWindow.PreserveNonClientActivation(123, active ? 1 : 0, 456, DefaultProc));
        Assert.Equal(new[] { ((nint)123, NativeMethods.WmNcActivate, active ? (nint)1 : 0, (nint)(-1)),
            ((nint)123, NativeMethods.WmNcActivate, (nint)1, (nint)(-1)) }, calls);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, 0)]
    [InlineData(37, 37)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void NormalizeClampAndRoundtrip(int value, int expected)
    {
        foreach (bool? keep in new bool?[] { null, true, false })
        {
            var original = AppConfig.CreateDefault() with { BackdropOpacity = value, KeepBackdropWhenInactive = keep };
            var normalized = ConfigValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig));
            Assert.Equal(expected, normalized.BackdropOpacity);
            Assert.Equal(keep == false ? false : (bool?)null, normalized.KeepBackdropWhenInactive);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(normalized, ConfigJsonContext.Default.AppConfig));
            Assert.Equal(expected != 0, json.RootElement.TryGetProperty("backdropOpacity", out _));
            Assert.Equal(keep == false, json.RootElement.TryGetProperty("keepBackdropWhenInactive", out _));
            Assert.Equal(original.Hotkey.Key, normalized.Hotkey.Key);
            Assert.Equal(original.Groups[0].Items.Select(i => (i.Id, i.Name, i.Path)), normalized.Groups[0].Items.Select(i => (i.Id, i.Name, i.Path)));
        }
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(50, 128)]
    [InlineData(99, 252)]
    [InlineData(100, 255)]
    [InlineData(500, 255)]
    public void OverlayUsesThemeRgbAndRoundedClampedAlpha(int opacity, byte alpha)
    {
        foreach (var color in new[] { Color.FromRgb(32, 32, 32), Color.FromRgb(250, 250, 250) })
            Assert.Equal(Color.FromArgb(alpha, color.R, color.G, color.B), AppearanceOptions.OverlayColor(color, opacity));
    }

    [Fact]
    public void NonClientDecisionNeverChangesActivationFocusOrPrewarmMessages()
    {
        foreach (int message in new[] { NativeMethods.WmNcActivate, 0x0006, 0x001C, 0x0007, 0x0008, NativeMethods.WmHotkey })
        foreach (bool keep in new[] { false, true })
        foreach (string material in new[] { "solid", "acrylic", "mica" })
        foreach (bool visible in new[] { false, true })
        foreach (bool warming in new[] { false, true })
            Assert.Equal(message == NativeMethods.WmNcActivate && keep && material != "solid" && visible && !warming,
                AppearanceOptions.KeepNonClientActive(message, keep, material, visible, warming));
    }

    [Fact]
    public Task LiveChangesClampPersistResetAndRespectReadOnly() => WpfTestHost.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-backdrop-" + Guid.NewGuid().ToString("N"));
        await using var store = ConfigService.Open(directory);
        using var state = new MainViewModel(store);
        Assert.Equal(0, state.BackdropOpacity); Assert.True(state.KeepBackdropWhenInactive);
        state.SetBackdropOpacity(200); state.SetKeepBackdropWhenInactive(false);
        state.SetBackdropOpacity(100); state.SetKeepBackdropWhenInactive(false);
        Assert.Equal(2, state.Revision);
        await state.FlushAsync();
        await using (var lockedStore = ConfigService.Open(directory, readOnly: true))
        using (var locked = new MainViewModel(lockedStore))
        {
            locked.SetBackdropOpacity(10); locked.SetKeepBackdropWhenInactive(true);
            Assert.Equal(100, locked.BackdropOpacity); Assert.False(locked.KeepBackdropWhenInactive);
            Assert.Equal(0, locked.Revision);
        }
        state.SetBackdropOpacity(-1); state.SetKeepBackdropWhenInactive(true);
        await state.FlushAsync();
        string json = await File.ReadAllTextAsync(Path.Combine(directory, "config.json"));
        Assert.DoesNotContain("backdropOpacity", json); Assert.DoesNotContain("keepBackdropWhenInactive", json);
    });

    [Fact]
    public Task ViewModelSubrowsFollowSolidFallbackAndExternalChanges() => WpfTestHost.RunAsync(() =>
    {
        var state = new SettingsViewModelTests.SettingsState();
        var preferences = (Dark: true, HighContrast: false, Transparency: true);
        using var appearance = new ThemeService(Dispatcher.CurrentDispatcher, state, new NoThemeChanges(), () => preferences);
        using var model = CreateModel(state, appearance);
        bool supported = AppearanceOptions.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Acrylic);
        Assert.Equal(supported, model.CanEditBackdrop);
        state.SetBackdropOpacity(45); state.SetKeepBackdropWhenInactive(false);
        Assert.Equal(45, model.BackdropOpacity); Assert.False(model.KeepBackdropWhenInactive);
        state.SetBackdrop("solid"); Assert.False(model.CanEditBackdrop);
        state.Calls.Clear(); model.BackdropOpacity = 70; model.KeepBackdropWhenInactive = true;
        Assert.Empty(state.Calls);
        state.SetBackdrop("acrylic"); Assert.Equal(supported, model.CanEditBackdrop);
        preferences = (true, false, false); appearance.Apply(); Assert.False(model.CanEditBackdrop);
        preferences = (true, true, true); appearance.Apply(); Assert.False(model.CanEditBackdrop);
        preferences = (true, false, true); appearance.Apply();
        appearance.ReportBackdrop(true); Assert.False(model.CanEditBackdrop);
        appearance.ReportBackdrop(false); Assert.Equal(supported, model.CanEditBackdrop);
        state.SetEditable(false); Assert.False(model.CanEditBackdrop);
        return Task.CompletedTask;
    });

    [Fact]
    public void ViewModelLiveChangesWriteOnceAndOpenLogsUsesAction()
    {
        var state = new SettingsViewModelTests.SettingsState();
        var actions = new SettingsViewModelTests.Actions();
        using var model = CreateModel(state, actions: actions);
        model.BackdropOpacity = 42; model.KeepBackdropWhenInactive = false;
        Assert.Equal(new[] { "opacity:42", "keep:False" }, state.Calls);
        model.OpenLogsCommand.Execute(null); Assert.Equal(1, actions.LogOpens);
        model.Dispose(); state.SetBackdropOpacity(80); Assert.Equal(42, model.BackdropOpacity);
    }

    [Fact]
    public Task SettingsPagesFitAtNormalTextSizeWithoutDesktopWindows() => WpfTestHost.RunAsync(async () =>
    {
        using var model = CreateModel(new() { DirectoryPath = @"C:\Users\LongProfileName\AppData\Roaming\QuickLaunch\" + new string('x', 120) });
        foreach (var page in new SettingsPage[] { new SettingsGeneralPage(), new SettingsAppearancePage(), new SettingsPanelPage(), new SettingsAboutPage() })
        {
            page.DataContext = model;
            page.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            // Conservative content allocation within the 760 x 540 window and 160-DIP pane.
            var size = new Size(590, 430);
            page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            page.UpdateLayout();
            var scroll = (ScrollViewer)page.FindName("PageScroll");
            Assert.True(scroll.ScrollableHeight == 0, $"{page.GetType().Name}: {scroll.ExtentHeight} > {scroll.ViewportHeight}");
            foreach (var control in LauncherWindow.Descendants<Control>(page).Where(c => c is Button or CheckBox or ComboBox or Slider or RadioButton or TextBox or Wpf.Ui.Controls.ToggleSwitch))
            {
                if (!control.IsVisible && control.Visibility == Visibility.Collapsed) continue;
                var bounds = control.TransformToAncestor(page).TransformBounds(new Rect(control.RenderSize));
                Assert.True(bounds.Right <= size.Width && bounds.Left >= 0, $"{page.GetType().Name}/{control.Name}: clipped at {bounds}");
            }
        }
    });

    [Fact]
    public Task SettingsForegroundFollowsThemeAndContrastGuardRejectsBlackText() => WpfTestHost.RunAsync(async () =>
    {
        using var model = CreateModel(new());
        var pages = new SettingsPage[] { new SettingsGeneralPage(), new SettingsAppearancePage(), new SettingsPanelPage(), new SettingsAboutPage() };
        foreach (var page in pages) page.DataContext = model;
        // App resource changes invalidate Window trees, not detached visual roots.
        // Owning windows provide that lifetime without creating/showing HWNDs.
        var hosts = pages.Select(page => new Window { Content = page }).ToArray();
        try
        {
            foreach (var theme in new[] { ApplicationTheme.Dark, ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                new WpfThemeApplication().ApplyTheme(theme);
                foreach (var page in pages)
                {
                    page.Measure(new Size(590, 430)); page.Arrange(new Rect(0, 0, 590, 430));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    page.UpdateLayout();
                    var expected = ((SolidColorBrush)page.FindResource("TextFillColorPrimaryBrush")).Color;
                    var title = LauncherWindow.Descendants<TextBlock>(page).Single(t => t.FontSize == 20);
                    Assert.Equal(expected, ((SolidColorBrush)title.Foreground).Color);
                    if (theme == ApplicationTheme.Dark)
                        Assert.True(SettingsTextContrast.FindFailures(page).Count == 0, string.Join("\n", SettingsTextContrast.FindFailures(page)));
                }
            }
            var titleToBreak = LauncherWindow.Descendants<TextBlock>(pages[0]).Single(t => t.FontSize == 20);
            titleToBreak.Foreground = Brushes.Black;
            Assert.Contains(SettingsTextContrast.FindFailures(pages[0]), error => error.Contains(titleToBreak.Text));
        }
        finally { foreach (var host in hosts) host.Close(); }
    });

    [Fact]
    public Task EveryHotkeyNameFitsTheSelectedComboBoxText() => WpfTestHost.RunAsync(async () =>
    {
        using var model = CreateModel(new());
        var page = new SettingsGeneralPage { DataContext = model, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") };
        var combo = (ComboBox)page.FindName("MainKey");
        foreach (string key in model.Keys)
        {
            combo.SelectedItem = key;
            page.Measure(new Size(590, 430)); page.Arrange(new Rect(0, 0, 590, 430));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            page.UpdateLayout();
            var text = Assert.Single(LauncherWindow.Descendants<TextBlock>(combo), t => t.Text == key);
            var fullText = new FormattedText(key, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
                text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
            Assert.True(text.ActualWidth >= fullText.WidthIncludingTrailingWhitespace,
                $"{key}: selected text has {text.ActualWidth:F2} DIP, needs {fullText.WidthIncludingTrailingWhitespace:F2} DIP.");
        }
        var apply = (FrameworkElement)page.FindName("ApplyButton");
        double keyCenter = combo.TranslatePoint(new Point(0, combo.ActualHeight / 2), page).Y;
        double applyCenter = apply.TranslatePoint(new Point(0, apply.ActualHeight / 2), page).Y;
        Assert.InRange(Math.Abs(keyCenter - applyCenter), 0, 1); // Wider key selector still shares the controls row.
    });

    [Fact]
    public Task NavigationUsesSharedViewModelAndSelectedAccentItemWithoutShowingWindow() => WpfTestHost.RunAsync(async () =>
    {
        using var model = CreateModel(new());
        var window = new SettingsWindow(model);
        // Layout the actual window content without Show/EnsureHandle or a desktop HWND.
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.DataContext = model;
        content.Measure(new Size(760, 540)); content.Arrange(new Rect(0, 0, 760, 540));
        var navigation = (Wpf.Ui.Controls.NavigationView)window.FindName("SettingsNavigation");
        foreach (string page in new[] { "general", "appearance", "panel", "about", "general" })
        {
            window.NavigateTo(page);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            content.UpdateLayout();
            var selected = Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(navigation.SelectedItem);
            Assert.True(navigation.IsPaneOpen);
            Assert.False(navigation.IsFooterSeparatorVisible);
            Assert.Equal(page, selected.TargetPageTag); Assert.True(selected.IsActive);
            var view = Assert.Single(LauncherWindow.Descendants<SettingsPage>(navigation));
            Assert.Same(model, view.DataContext);
            var scroll = (ScrollViewer)view.FindName("PageScroll");
            Assert.True(scroll.ViewportHeight > 0 && scroll.ScrollableHeight == 0,
                $"{page}: extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight}");
        }
        window.Close();
    });

    private static SettingsViewModel CreateModel(SettingsViewModelTests.SettingsState state, ThemeService? appearance = null, SettingsViewModelTests.Actions? actions = null) =>
        new(state, new StartupService(@"C:\QuickLaunch\QuickLaunch.exe", @"C:\profile", "Tests", _ => null, (_, _) => { }),
            new SettingsViewModelTests.Hotkey(), actions ?? new(), NullLogger<SettingsViewModel>.Instance, appearance);

    private sealed class NoThemeChanges : IThemeApplication
    {
        public ApplicationTheme CurrentTheme => ApplicationTheme.Dark;
        public void ApplyTheme(ApplicationTheme theme) { }
        public void ApplySystemAccent(ApplicationTheme theme) { }
        public void ApplyAccent(Color color, ApplicationTheme theme) { }
    }
}
