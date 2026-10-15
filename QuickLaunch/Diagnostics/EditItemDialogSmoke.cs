using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class EditItemDialogSmoke
{
    public static async Task RunAsync(LauncherWindow main, string directory, string profileDirectory, Action<string, bool> check)
    {
        using var themeScope = new ThemeSmokeScope();
        string fixtures = Path.Combine(profileDirectory, "editor-fixtures");
        var locations = Enum.GetValues<CommonLocation>().ToDictionary(k => k, k => Path.Combine(fixtures, k.ToString()));
        foreach (string path in locations.Values) Directory.CreateDirectory(path);
        string folder = locations[CommonLocation.Documents], file = Path.Combine(fixtures, "note.txt");
        await File.WriteAllTextAsync(file, "Dialog smoke fixture; never launched.");
        string app = Environment.ProcessPath!;
        try
        {
            ThemeSmokeScope.Apply(true);
            var dialog = new EditItemDialog(null, icons: main.Icons) { Owner = main, ResolveLocation = location => locations[location] };
            try
            {
                dialog.Show(); WindowSmoke.Pump();
                check("add-defaults-to-app", dialog.AppCard.IsChecked == true && dialog.SelectedKind == LaunchKind.App && (string)dialog.SaveButton.Content == "添加");
                check("add-advanced-starts-collapsed", !dialog.AdvancedOptions.IsExpanded && dialog.AdvancedOptions.IsVisible);
                check("add-advanced-uses-library-expander", dialog.AdvancedOptions.Style == dialog.FindResource(typeof(Wpf.Ui.Controls.CardExpander)));
                check("add-save-uses-library-primary-button", dialog.SaveButton.Appearance == Wpf.Ui.Controls.ControlAppearance.Primary);
                check("add-icon-preview-is-button", dialog.IconPreview.Focusable && dialog.IconPreview.Width == 56 && dialog.IconPreview.Height == 56);
                dialog.SaveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check("add-empty-validation-keeps-dialog-open", dialog.IsVisible && dialog.Result is null && dialog.Error.Text == "名称和目标不能为空。"
                    && dialog.Error.Foreground == dialog.FindResource("SystemFillColorCriticalBrush"));
                dialog.NameBox.Text = "QuickLaunch"; dialog.PathBox.Text = ItemIdentity.PortablePath(app);
                await CaptureAsync(dialog, directory, "add-app.png");
                check("add-app-preview-uses-icon-service", dialog.PreviewImage.Source is not null && dialog.PreviewGlyph.Visibility == Visibility.Collapsed);
                check("add-type-cards-have-equal-widths", dialog.TypeCards.Children.OfType<RadioButton>().All(c => Math.Abs(c.ActualWidth - dialog.AppCard.ActualWidth) < 1));

                ThemeSmokeScope.Apply(false);
                await CaptureAsync(dialog, directory, "add-app-light.png");
                ThemeSmokeScope.Apply(true);

                dialog.AppCard.Focus();
                dialog.AppCard.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Right)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                check("add-type-cards-support-arrow-key", dialog.FileCard.IsChecked == true);
                await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                check("add-card-selection-does-not-reinfer-path", dialog.SelectedKind == LaunchKind.File);
                dialog.PathBox.Text = file;
                await CaptureAsync(dialog, directory, "add-file.png");
                check("add-file-shows-correct-fields", dialog.TargetLabel.Text == "文件路径" && dialog.BrowseButton.IsVisible && !dialog.AdvancedOptions.IsVisible && !dialog.LocationChips.IsVisible);

                dialog.NameBox.Clear(); dialog.FolderCard.IsChecked = true;
                var chips = dialog.LocationChips.Children.OfType<Button>().ToArray();
                chips[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("add-chip-fills-empty-name-and-portable-path", dialog.NameBox.Text == "桌面" && dialog.PathBox.Text == ItemIdentity.PortablePath(locations[CommonLocation.Desktop]));
                chips[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("add-chip-replaces-auto-name", dialog.NameBox.Text == "下载");
                dialog.NameBox.Text = "我的目录";
                foreach (var chip in chips)
                {
                    chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check($"add-chip-{chip.Tag}-preserves-typed-name", dialog.NameBox.Text == "我的目录"
                        && dialog.PathBox.Text == ItemIdentity.PortablePath(locations[(CommonLocation)chip.Tag]));
                }
                await CaptureAsync(dialog, directory, "add-folder.png");
                ThemeSmokeScope.Apply(false);
                await CaptureAsync(dialog, directory, "add-folder-light.png");
                ThemeSmokeScope.Apply(true);
                check("add-folder-shows-chips", dialog.LocationChips.IsVisible && chips.All(c => c.IsVisible) && !dialog.AdvancedOptions.IsVisible);
                check("add-folder-chips-fit-without-clipping", chips.All(c => c.TranslatePoint(new Point(c.ActualWidth, 0), dialog.LocationChips).X <= dialog.LocationChips.ActualWidth));

                // A single TextBox replacement follows the same TextChanged route as a paste, without global clipboard state.
                dialog.NameBox.Clear(); dialog.AppCard.IsChecked = true; dialog.PathBox.Text = "https://github.com/";
                check("add-url-paste-autoswitches-and-infers-host", dialog.UrlCard.IsChecked == true && dialog.NameBox.Text == "github.com");
                await CaptureAsync(dialog, directory, "add-url.png");
                check("add-url-hides-browse-and-advanced", !dialog.BrowseButton.IsVisible && !dialog.AdvancedOptions.IsVisible && !dialog.ElevateBox.IsEnabled);
                check("add-url-preview-uses-icon-service", dialog.PreviewImage.Source is not null);
                dialog.NameBox.Text = "我的网页"; dialog.PathBox.Text = "https://example.com/download.exe";
                check("add-url-preserves-typed-name", dialog.NameBox.Text == "我的网页");
                dialog.FileCard.IsChecked = true;
                await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                check("add-manual-card-wins-over-url", dialog.SelectedKind == LaunchKind.File);

                dialog.PathBox.Text = folder; dialog.UrlCard.IsChecked = true;
                await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                check("add-card-cancels-pending-path-inference", dialog.SelectedKind == LaunchKind.Url);
                dialog.PathBox.Text = file;
                dialog.UrlCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                check("add-clicking-checked-card-cancels-pending-inference", dialog.SelectedKind == LaunchKind.Url);
                dialog.PathBox.Text = folder + Path.DirectorySeparatorChar;
                await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                check("add-existing-directory-autoswitches", dialog.SelectedKind == LaunchKind.Folder);
                dialog.PathBox.Text = app;
                await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                check("add-existing-application-autoswitches", dialog.SelectedKind == LaunchKind.App);
                dialog.PathBox.Text = file;
                await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                check("add-existing-file-autoswitches", dialog.SelectedKind == LaunchKind.File);
                dialog.UrlCard.IsChecked = true; dialog.PathBox.Clear();
                await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                check("add-empty-url-shows-hint-and-fallback", dialog.PathBox.PlaceholderText == dialog.UrlHint.Text && dialog.PreviewImage.Source is null && dialog.PreviewGlyph.IsVisible);
            }
            finally { dialog.Close(); WindowSmoke.Pump(); }

            foreach (LaunchKind kind in Enum.GetValues<LaunchKind>())
            {
                string path = kind switch { LaunchKind.App => app, LaunchKind.File => file, LaunchKind.Folder => folder, _ => "https://example.com/" };
                var editing = new EditItemDialog(new("已有名称", path, kind), icons: main.Icons) { Owner = main };
                try
                {
                    editing.Show(); WindowSmoke.Pump(); await editing.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                    check($"edit-{kind}-preselects-kind-and-preserves-name", editing.SelectedKind == kind && editing.NameBox.Text == "已有名称" && (string)editing.SaveButton.Content == "保存");
                    check($"edit-{kind}-advanced-defaults-collapsed", !editing.AdvancedOptions.IsExpanded);
                }
                finally { editing.Close(); WindowSmoke.Pump(); }

                var saved = Save(main, null, d =>
                {
                    d.NameBox.Text = "保存检查"; d.PathBox.Text = path;
                    d.TypeCards.Children.OfType<RadioButton>().Single(c => (LaunchKind)c.Tag == kind).IsChecked = true;
                    d.ArgumentsBox.Text = "--test"; d.WorkingBox.Text = folder; d.ElevateBox.IsChecked = true;
                });
                check($"add-{kind}-saves-selected-kind", saved?.Kind == kind && saved.Name == "保存检查"
                    && saved.Path == (kind == LaunchKind.Url ? path : ItemIdentity.PortablePath(path)));
                check($"add-{kind}-saves-only-app-advanced-values", saved is not null && (kind == LaunchKind.App
                    ? saved.Arguments == "--test" && saved.WorkingDirectory == folder && saved.RunAsAdmin
                    : saved.Arguments == "" && saved.WorkingDirectory == "" && !saved.RunAsAdmin));
            }
            foreach (var item in new[] { new LaunchItem("参数", app, LaunchKind.App, "--test"), new("目录", app, LaunchKind.App, WorkingDirectory: folder),
                new("管理员", app, LaunchKind.App, RunAsAdmin: true), new("快捷方式", Path.Combine(fixtures, "example.lnk"), LaunchKind.App, "ignored", "relative", true) })
            {
                var editing = new EditItemDialog(item, icons: main.Icons) { Owner = main };
                try
                {
                    editing.Show(); WindowSmoke.Pump(); await editing.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
                    check($"edit-advanced-{item.Name}-starts-expanded", editing.AdvancedOptions.IsExpanded);
                    if (item.Name == "快捷方式")
                    {
                        check("edit-shortcut-disables-arguments-and-working-only", !editing.ArgumentsBox.IsEnabled && !editing.WorkingBox.IsEnabled && editing.ElevateBox.IsEnabled && editing.ShortcutHint.IsVisible);
                        await CaptureAsync(editing, directory, "edit-shortcut.png");
                    }
                }
                finally { editing.Close(); WindowSmoke.Pump(); }
            }
            var shortcut = Save(main, new("快捷方式", Path.Combine(fixtures, "example.lnk"), LaunchKind.App, "ignored", "relative", true), _ => { });
            check("edit-shortcut-save-drops-ignored-values", shortcut is { Kind: LaunchKind.App, Arguments: "", WorkingDirectory: "", RunAsAdmin: true });
        }
        finally { themeScope.Dispose(); }
    }

    private static async Task CaptureAsync(EditItemDialog dialog, string directory, string name)
    {
        await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
        dialog.UpdateLayout(); WindowSmoke.Pump();
        PrototypeSmoke.SaveSnapshot(dialog, Path.Combine(directory, name));
    }

    internal static LaunchItem? Save(LauncherWindow main, LaunchItem? item, Action<EditItemDialog> configure)
    {
        var dialog = new EditItemDialog(item, icons: main.Icons);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => dialog.Close();
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
        {
            WindowSmoke.Pump(); configure(dialog); dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        timeout.Start();
        try { return main.ShowEditor(dialog) ? dialog.Result : null; }
        finally { timeout.Stop(); dialog.Close(); WindowSmoke.Pump(); }
    }
}
