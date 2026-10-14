using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using QuickLaunch.Interop;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class DialogAppearanceSmoke
{
    public static async Task RunAsync(LauncherWindow main, string directory, string profileDirectory, Action<string, bool> check)
    {
        var resources = Application.Current.Resources;
        var palette = resources.Keys.OfType<string>().Where(k => resources[k] is SolidColorBrush || k == "PopupShadowOpacity").ToDictionary(k => k, k => resources[k]);
        using var theme = new ThemeService(main.Dispatcher);
        bool exitCalled = false;
        var settings = new SettingsWindow(main.ViewModel, profileDirectory, "当前已注册：Alt+Space", _ => ("模拟应用失败", true),
            new StartupService(Environment.ProcessPath!, profileDirectory), main.ResetWindowSize, () => exitCalled = true, theme) { Owner = main };
        var edit = new EditItemDialog(main.SelectedGroup.Items[0], theme) { Owner = main };
        var name = new NameDialog("新建分组", "新分组", theme) { Owner = main };
        Window[] dialogs = [settings, edit, name];
        try
        {
            foreach (string font in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                var typeface = new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                check($"add-item-ecc8-exists-in-{font}", typeface.TryGetGlyphTypeface(out var glyphs)
                    && glyphs.FamilyNames.Values.Contains(font) && glyphs.CharacterToGlyphMap.TryGetValue(0xECC8, out ushort glyph) && glyph != 0);
            }
            foreach (var dialog in dialogs)
            {
                new WindowInteropHelper(dialog).EnsureHandle();
                CheckTitleBar(dialog, theme.IsDark, "source-initialized", check);
            }
            check("settings-all-required-names", new[] { "Alt", "Control", "Shift", "Windows", "MainKey", "ApplyButton", "ResetSizeButton", "StartupBox", "Result", "DirectoryLabel" }
                .All(n => settings.FindName(n) is FrameworkElement));
            foreach (bool dark in new[] { true, false })
            {
                ThemeService.ApplyPalette(resources, dark, Colors.Teal);
                main.ApplyBackdrop(dark, false, false);
                string suffix = dark ? "" : "-light";
                foreach (var dialog in dialogs)
                {
                    DialogTheme.Apply(dialog, dark); dialog.Show(); dialog.UpdateLayout();
                    await BenchmarkRunner.NextFrame();
                    CheckTitleBar(dialog, dark, dark ? "dark" : "light", check);
                    check($"{dialog.GetType().Name}-{dark}-solid-background", dialog.Background == resources["WindowBrush"] && !dialog.AllowsTransparency);
                    string file = dialog == settings ? "settings" : dialog == edit ? "edit-dialog" : "name-dialog";
                    PrototypeSmoke.SaveSnapshot(dialog, Path.Combine(directory, file + suffix + ".png"));
                    if (dialog == settings)
                    {
                        settings.MainKey.IsDropDownOpen = true;
                        await BenchmarkRunner.NextFrame();
                        var popup = (Popup)settings.MainKey.Template.FindName("PART_Popup", settings.MainKey);
                        CheckPopup(popup.Child, $"combobox-{dark}", dark, check);
                        PrototypeSmoke.SaveSnapshot((FrameworkElement)PresentationSource.FromVisual(popup.Child).RootVisual, Path.Combine(directory, "combobox" + suffix + ".png"));
                        settings.MainKey.IsDropDownOpen = false;
                    }
                    dialog.Hide();
                }
                var menu = new ContextMenu { PlacementTarget = main.SettingsButton };
                menu.Items.Add(new MenuItem { Header = "编辑项目…" }); menu.Items.Add(new MenuItem { Header = "删除" });
                try
                {
                    menu.IsOpen = true; await BenchmarkRunner.NextFrame();
                    CheckPopup(menu, $"context-menu-{dark}", dark, check);
                    PrototypeSmoke.SaveSnapshot(menu, Path.Combine(directory, "menu" + suffix + ".png"));
                }
                finally { menu.IsOpen = false; }
                var tooltip = new ToolTip { Content = "添加项目（也可拖入文件）", PlacementTarget = main.AddItemButton };
                try
                {
                    tooltip.IsOpen = true; await BenchmarkRunner.NextFrame();
                    CheckPopup(tooltip, $"tooltip-{dark}", dark, check);
                    PrototypeSmoke.SaveSnapshot(tooltip, Path.Combine(directory, "tooltip" + suffix + ".png"));
                }
                finally { tooltip.IsOpen = false; }
            }
            foreach (var dialog in dialogs) DialogTheme.Apply(dialog, !theme.IsDark);
            theme.QueueUpdate();
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            foreach (var dialog in dialogs) CheckTitleBar(dialog, theme.IsDark, "theme-changed", check);
            settings.Show(); await BenchmarkRunner.NextFrame();
            int originalIcon = main.ViewModel.IconSize;
            settings.LargeIcons.IsChecked = true;
            check("settings-size-segment-live-applies", main.ViewModel.IconSize == 48);
            main.ViewModel.SetIconSize(40);
            check("settings-size-segment-follows-model", settings.MediumIcons.IsChecked == true);
            settings.MediumIcons.Focus();
            settings.MediumIcons.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(settings), Environment.TickCount, Key.Right)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check("settings-size-segments-support-arrow-key", settings.LargeIcons.IsChecked == true && main.ViewModel.IconSize == 48);
            main.ViewModel.SetIconSize(originalIcon);
            settings.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("settings-error-uses-error-brush", settings.Result.Text == "模拟应用失败" && settings.Result.Foreground == resources["ErrorBrush"]);
            settings.DirectoryLabel.SelectAll();
            check("settings-directory-is-copyable", settings.DirectoryLabel.IsReadOnly && settings.DirectoryLabel.SelectedText == settings.DirectoryLabel.Text);
            settings.ExitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("settings-exit-calls-supplied-path", exitCalled);
            await using var readOnlyStore = ConfigService.Open(Path.Combine(profileDirectory, "settings-read-only"), readOnly: true);
            var readOnlyModel = new MainViewModel(readOnlyStore);
            var readOnly = new SettingsWindow(readOnlyModel, readOnlyStore.DirectoryPath, "", _ => ("", false),
                new StartupService(Environment.ProcessPath!, readOnlyStore.DirectoryPath), () => { }, () => { });
            try
            {
                check("settings-read-only-disables-edits", !readOnly.IconSizes.IsEnabled && !readOnly.ApplyButton.IsEnabled && !readOnly.ResetSizeButton.IsEnabled && !readOnly.StartupBox.IsEnabled);
            }
            finally { readOnly.Close(); readOnlyModel.Suspend(true); }
        }
        finally
        {
            foreach (var dialog in dialogs) dialog.Close();
            foreach (var (key, value) in palette) resources[key] = value;
            var preferences = ThemeService.ReadPreferences();
            main.ApplyBackdrop(preferences.Dark, preferences.HighContrast, preferences.Transparency);
        }
    }

    private static void CheckTitleBar(Window dialog, bool dark, string phase, Action<string, bool> check)
    {
        nint hwnd = new WindowInteropHelper(dialog).Handle;
        check($"{dialog.GetType().Name}-titlebar-{phase}", NativeMethods.DwmGetWindowAttribute(hwnd, 20, out int value, sizeof(int)) >= 0 && value == (dark ? 1 : 0));
    }

    private static void CheckPopup(DependencyObject root, string name, bool dark, Action<string, bool> check)
    {
        var source = PresentationSource.FromVisual((Visual)root) as HwndSource;
        check($"{name}-popup-is-transparent", source is { UsesPerPixelOpacity: true } && source.CompositionTarget.BackgroundColor.A == 0);
        var shadows = LauncherWindow.Descendants<Border>(root).Where(b => b.Effect is DropShadowEffect).ToArray();
        check($"{name}-one-unclipped-theme-shadow", shadows.Length == 1 && shadows[0].Effect is DropShadowEffect effect
            && effect.BlurRadius == 16 && effect.ShadowDepth == 3 && Math.Abs(effect.Opacity - (dark ? .25 : .14)) < .001
            && shadows[0].Parent is Grid { Margin.Left: >= 12, Margin.Top: >= 12, Margin.Right: >= 12, Margin.Bottom: >= 12, ClipToBounds: false });
    }
}
