using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class DialogAppearanceSmoke
{
    public static async Task RunAsync(LauncherWindow main, string directory, string profileDirectory, Action<string, bool> check)
    {
        var resources = Application.Current.Resources;
        using var themeScope = new ThemeSmokeScope();
        bool exitCalled = false;
        var settings = new SettingsWindow(main.ViewModel, profileDirectory, "当前已注册：Alt+Space", _ => ("模拟应用失败", true),
            new StartupService(Environment.ProcessPath!, profileDirectory), main.ResetWindowSize, () => exitCalled = true) { Owner = main };
        var edit = new EditItemDialog(main.SelectedGroup.Items[0]) { Owner = main };
        var name = new NameDialog("新建分组", "新分组") { Owner = main };
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
                WindowSmoke.Pump();
                CheckTitleBar(dialog, "source-initialized", check);
            }
            check("settings-all-required-names", new[] { "Alt", "Control", "Shift", "Windows", "MainKey", "ApplyButton", "ResetSizeButton", "StartupBox", "Result", "DirectoryLabel",
                "WebsiteIconsBox", "IconSizes", "SmallIcons", "MediumIcons", "LargeIcons", "Placements", "LastPosition", "FollowMouse", "ExitButton" }
                .All(n => settings.FindName(n) is FrameworkElement));
            check("edit-all-required-names", new[] { "EditorBody", "TypeCards", "AppCard", "FileCard", "FolderCard", "UrlCard", "IconPreview", "PreviewGlyph", "PreviewImage",
                "NameBox", "TargetLabel", "PathBox", "UrlHint", "BrowseButton", "LocationChips", "AdvancedOptions", "ShortcutHint", "ArgumentsBox", "WorkingBox", "ElevateBox",
                "Error", "SaveButton", "IconPopup", "PickerSurface", "GlyphGrid", "PaletteGrid", "IconFileButton", "IconResetButton" }.All(n => edit.FindName(n) is FrameworkElement));
            foreach (bool dark in new[] { true, false })
            {
                ThemeSmokeScope.Apply(dark);
                main.ApplyBackdrop(false, false);
                string suffix = dark ? "" : "-light";
                foreach (var dialog in dialogs)
                {
                    dialog.Show(); WindowSmoke.Pump();
                    if (dialog is EditItemDialog editor) await editor.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                    dialog.UpdateLayout();
                    CheckTitleBar(dialog, dark ? "dark" : "light", check);
                    check($"{dialog.GetType().Name}-{dark}-theme-foreground", dialog.Foreground == resources["TextFillColorPrimaryBrush"] && !dialog.AllowsTransparency);
                    string file = dialog == settings ? "settings" : dialog == edit ? "edit-dialog" : "name-dialog";
                    PrototypeSmoke.SaveSnapshot(dialog, Path.Combine(directory, file + suffix + ".png"));
                    if (dialog == settings)
                    {
                        check($"settings-modifiers-fit-{dark}", new[] { settings.Alt, settings.Control, settings.Shift, settings.Windows }
                            .All(box => box.TranslatePoint(new Point(box.ActualWidth, 0), settings).X <= settings.ActualWidth - 24));
                        var popup = (Popup)settings.MainKey.Template.FindName("Popup", settings.MainKey);
                        using var lifetime = new PopupSmokeScope(popup);
                        lifetime.Open(() => settings.MainKey.IsDropDownOpen = true);
                        WindowSmoke.Pump();
                        PopupSmokeScope.Layout((FrameworkElement)popup.Child);
                        await PopupSmokeScope.SettleAnimationAsync((FrameworkElement)popup.Child);
                        CheckPopup(popup.Child, $"combobox-{dark}", check);
                        var surface = PresentationSource.FromVisual(popup.Child)?.RootVisual ?? popup.Child;
                        PrototypeSmoke.SaveSnapshot((FrameworkElement)surface, Path.Combine(directory, "combobox" + suffix + ".png"), includeOverflow: true);
                        lifetime.Close(() => settings.MainKey.IsDropDownOpen = false);
                        check($"combobox-{dark}-closed-before-hiding-dialog", lifetime.Closed && !settings.MainKey.IsDropDownOpen);
                    }
                    dialog.Hide(); WindowSmoke.Pump();
                }
                var tile = (ListBoxItem)main.SelectedList.ItemContainerGenerator.ContainerFromIndex(0);
                var menu = tile.ContextMenu;
                menu.PlacementTarget = tile;
                using (var lifetime = new PopupSmokeScope(menu))
                {
                    lifetime.Open(); WindowSmoke.Pump();
                    PopupSmokeScope.Layout(menu);
                    await PopupSmokeScope.SettleAnimationAsync(menu);
                    CheckPopup(menu, $"context-menu-{dark}", check);
                    PrototypeSmoke.SaveSnapshot(PopupSmokeScope.SnapshotRoot(menu), Path.Combine(directory, "item-menu" + suffix + ".png"), includeOverflow: true);
                    lifetime.Close();
                    check($"context-menu-{dark}-closed", lifetime.Closed);
                }
                var tooltip = new ToolTip { Content = "添加项目（也可拖入文件）", PlacementTarget = main.AddItemButton };
                using (var lifetime = new PopupSmokeScope(tooltip))
                {
                    lifetime.Open(); WindowSmoke.Pump();
                    PopupSmokeScope.Layout(tooltip);
                    await PopupSmokeScope.SettleAnimationAsync(tooltip);
                    CheckPopup(tooltip, $"tooltip-{dark}", check);
                    check($"tooltip-{dark}-uses-text-font", tooltip.FontFamily.Source == main.FontFamily.Source);
                    PrototypeSmoke.SaveSnapshot(PopupSmokeScope.SnapshotRoot(tooltip), Path.Combine(directory, "tooltip" + suffix + ".png"), includeOverflow: true);
                    lifetime.Close();
                    check($"tooltip-{dark}-closed", lifetime.Closed);
                }
            }
            settings.Show(); WindowSmoke.Pump();
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
            check("settings-error-uses-error-brush", settings.Result.Text == "模拟应用失败" && settings.Result.Foreground == resources["SystemFillColorCriticalBrush"]);
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
                check("settings-read-only-disables-edits", !readOnly.IconSizes.IsEnabled && !readOnly.Placements.IsEnabled && !readOnly.ApplyButton.IsEnabled && !readOnly.ResetSizeButton.IsEnabled && !readOnly.StartupBox.IsEnabled && !readOnly.WebsiteIconsBox.IsEnabled);
            }
            finally { readOnly.Close(); WindowSmoke.Pump(); readOnlyModel.Suspend(true); }
        }
        finally
        {
            foreach (var dialog in dialogs) { dialog.Close(); WindowSmoke.Pump(); }
            themeScope.Dispose();
            var preferences = ThemeService.ReadPreferences();
            main.ApplyBackdrop(preferences.HighContrast, preferences.Transparency);
        }
    }

    internal static void CheckTitleBar(Window dialog, string phase, Action<string, bool> check)
    {
        check($"{dialog.GetType().Name}-fluent-mica-{phase}", dialog is Wpf.Ui.Controls.FluentWindow
            { ExtendsContentIntoTitleBar: true, WindowBackdropType: Wpf.Ui.Controls.WindowBackdropType.Mica });
        check($"{dialog.GetType().Name}-title-only-{phase}", dialog.FindName("DialogTitleBar") is Wpf.Ui.Controls.TitleBar
            { Icon: null, ShowMaximize: false, ShowMinimize: false, CanMaximize: false, ShowClose: true } titleBar && titleBar.Title == dialog.Title);
        check($"{dialog.GetType().Name}-owned-dialog-{phase}", dialog.Owner is LauncherWindow && !dialog.ShowInTaskbar
            && dialog.ResizeMode == ResizeMode.NoResize && dialog.WindowStartupLocation == WindowStartupLocation.CenterOwner);
    }

    private static void CheckPopup(DependencyObject root, string name, Action<string, bool> check)
    {
        var source = PresentationSource.FromVisual((Visual)root) as HwndSource;
        check($"{name}-popup-is-transparent-when-created", source is null
            || source is { UsesPerPixelOpacity: true } && source.CompositionTarget.BackgroundColor.A == 0);
        check($"{name}-library-effect-decorator", root is Wpf.Ui.Controls.EffectThicknessDecorator
            || LauncherWindow.Descendants<Wpf.Ui.Controls.EffectThicknessDecorator>(root).Any());
    }
}
