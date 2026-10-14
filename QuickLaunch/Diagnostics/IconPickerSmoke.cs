using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class IconPickerSmoke
{
    public static async Task RunAsync(LauncherWindow main, string directory, Action<string, bool> check)
    {
        var resources = Application.Current.Resources;
        var palette = resources.Keys.OfType<string>().Where(k => resources[k] is SolidColorBrush || k == "PopupShadowOpacity").ToDictionary(k => k, k => resources[k]);
        var original = new LaunchItem("开发工具", "https://example.com/code", LaunchKind.Url) { Icon = "glyph:E943:#5B4FD1" };
        var dialog = new EditItemDialog(original, icons: main.Icons, openIconPicker: true) { Owner = main };
        var group = main.SelectedGroup;
        var samples = new[]
        {
            original, new LaunchItem("云端文件", "https://example.com/cloud", LaunchKind.Url) { Icon = "glyph:E753:#0F7B6C" },
            new LaunchItem("音乐", "https://example.com/music", LaunchKind.Url) { Icon = "glyph:E8D6:#B83280" },
            new LaunchItem("收藏", "https://example.com/star", LaunchKind.Url) { Icon = "glyph:E735:#B7791F" }
        };
        try
        {
            ThemeService.ApplyPalette(resources, true, Colors.Teal);
            dialog.Show();
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            await BenchmarkRunner.NextFrame();
            check("icon-picker-opens-on-request", dialog.IconPopup.IsOpen && dialog.PickerSurface.IsVisible);
            var glyphs = dialog.GlyphGrid.Children.OfType<RadioButton>().ToArray();
            var colors = dialog.PaletteGrid.Children.OfType<RadioButton>().ToArray();
            check("icon-picker-preselects-glyph-and-color", glyphs.Single(c => c.IsChecked == true).Tag is 0xE943 && (string)colors.Single(c => c.IsChecked == true).Tag == "#5B4FD1");
            check("icon-picker-grid-and-accessible-names", dialog.GlyphGrid.Columns == 8 && glyphs.All(c => c.Width == 36 && c.Height == 36
                && AutomationProperties.GetName(c) == (string)c.ToolTip) && glyphs.Length == IconCatalog.AvailableGlyphs.Count());
            check("icon-picker-dismisses-outside", !dialog.IconPopup.StaysOpen);
            foreach (bool dark in new[] { true, false })
            {
                ThemeService.ApplyPalette(resources, dark, Colors.Teal); DialogTheme.Apply(dialog, dark);
                dialog.UpdateLayout(); await BenchmarkRunner.NextFrame();
                SavePopupSnapshot(dialog, Path.Combine(directory, dark ? "icon-picker.png" : "icon-picker-light.png"));
            }
            ThemeService.ApplyPalette(resources, true, Colors.Teal);
            // The full library sheet also makes glyph/name mistakes visible during review.
            await SaveLibraryAsync(main, directory);
            var selected = glyphs.Single(c => c.IsChecked == true);
            selected.Focus(); PressKey(selected, Key.Right);
            check("icon-picker-arrow-navigation", glyphs[1].IsKeyboardFocused);
            PressKey(glyphs[1], Key.Enter);
            check("icon-picker-enter-selects-without-saving", dialog.Result is null && dialog.SelectedIcon == $"glyph:{(int)glyphs[1].Tag:X4}:#5B4FD1");
            var previous = dialog.PreviewImage.Source;
            colors[3].RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            check("icon-picker-selection-updates-preview", dialog.SelectedIcon == $"glyph:{(int)glyphs[1].Tag:X4}:#C2410C" && dialog.PreviewImage.Source is not null && dialog.PreviewImage.Source != previous);
            PressKey(glyphs[1], Key.Escape);
            await BenchmarkRunner.NextFrame();
            check("icon-picker-escape-keeps-editor-open", !dialog.IconPopup.IsOpen && dialog.IsVisible && dialog.IconPreview.IsKeyboardFocused);
            dialog.OpenIconPicker();
            dialog.IconResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            check("icon-picker-restore-default", dialog.SelectedIcon is null && dialog.PreviewImage.Source is not null && glyphs.All(c => c.IsChecked != true));
            dialog.IconPopup.IsOpen = false;
            dialog.SetFileIcon(Path.Combine(directory, "icon-picker.png"));
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            check("icon-picker-file-preview", dialog.SelectedIcon == "file:" + ItemIdentity.PortablePath(Path.Combine(directory, "icon-picker.png")) && dialog.PreviewImage.Source is not null);
            dialog.PathBox.Clear();
            glyphs[0].RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            check("icon-picker-works-before-target-entry", dialog.PreviewImage.Source is not null);
            dialog.Close();

            var saved = EditItemDialogSmoke.Save(main, original, d => d.NameBox.Text = "保留图标");
            check("icon-editor-save-preserves-custom-icon", saved?.Icon == original.Icon);
            var reset = EditItemDialogSmoke.Save(main, original, d => d.IconResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            check("icon-editor-save-persists-reset", reset is not null && reset.Icon is null);
            var file = EditItemDialogSmoke.Save(main, original, d => d.SetFileIcon(Path.Combine(directory, "icon-picker.png")));
            check("icon-editor-save-persists-portable-file", file?.Icon == "file:" + ItemIdentity.PortablePath(Path.Combine(directory, "icon-picker.png")));

            foreach (var sample in samples) group.Items.Add(sample);
            main.SelectGroup(group); main.UpdateLayout(); await main.LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await BenchmarkRunner.NextFrame();
            check("launcher-renders-custom-icons", samples.All(s => s.Visual.Image is not null && s.ErrorMessage is null));
            PrototypeSmoke.SaveSnapshot(main, Path.Combine(directory, "launcher-custom-icons.png"));
            var tile = LauncherWindow.Descendants<Button>(main.GroupHost).First(b => b.DataContext is LaunchItem i && i.Id == original.Id);
            var menu = tile.ContextMenu; menu.PlacementTarget = tile; menu.IsOpen = true;
            var entries = menu.Items.OfType<MenuItem>().ToArray();
            int edit = Array.FindIndex(entries, e => (string)e.Header == "编辑…");
            check("icon-context-menu-after-edit", edit >= 0 && (string)entries[edit + 1].Header == "更换图标…" && entries[edit + 1].IsEnabled && (string)entries[edit + 1].Tag == "edit");
            menu.IsOpen = false;
            main.ViewModel.Suspend(true);
            try
            {
                menu.IsOpen = true;
                check("icon-context-menu-disabled-when-not-editable", !entries[edit + 1].IsEnabled);
            }
            finally { menu.IsOpen = false; main.ViewModel.Suspend(false); }
        }
        finally
        {
            dialog.Close();
            foreach (var sample in samples) group.Items.Remove(sample);
            main.SelectGroup(group);
            foreach (var (key, value) in palette) resources[key] = value;
        }
    }

    private static void PressKey(UIElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(element), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static void SavePopupSnapshot(EditItemDialog dialog, string path)
    {
        // Popup has a separate presentation source. Composite both actual WPF visuals at their screen-relative DIP positions.
        var popup = dialog.PickerSurface;
        Point location = dialog.PointFromScreen(popup.PointToScreen(new Point()));
        var bounds = new Rect(0, 0, dialog.ActualWidth, dialog.ActualHeight);
        var popupBounds = new Rect(location, popup.RenderSize); bounds.Union(popupBounds); bounds.Inflate(24, 24);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)dialog.FindResource("WindowBrush"), null, new Rect(0, 0, bounds.Width, bounds.Height));
            drawing.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            var dialogBounds = new Rect(0, 0, dialog.ActualWidth, dialog.ActualHeight);
            drawing.DrawRectangle(new VisualBrush(dialog) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = dialogBounds }, null, dialogBounds);
            var viewbox = new Rect(popup.RenderSize); viewbox.Inflate(12, 12); popupBounds.Inflate(12, 12);
            drawing.DrawRectangle(new VisualBrush(popup) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = viewbox }, null, popupBounds); drawing.Pop();
        }
        var image = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); Save(image, path);
    }

    private static async Task SaveLibraryAsync(LauncherWindow main, string directory)
    {
        var sheet = new System.Windows.Controls.Primitives.UniformGrid { Columns = 8, Background = (Brush)main.FindResource("WindowBrush"), Width = 704 };
        foreach (var glyph in IconCatalog.AvailableGlyphs)
        {
            var cell = new StackPanel { Width = 88, Height = 84 };
            cell.Children.Add(new Image { Source = await main.Icons.GetAsync(new LaunchItem("", "", LaunchKind.Url) { Icon = $"glyph:{glyph.CodePoint:X4}:#2F6FDB" }, 48), Width = 40, Height = 40, Margin = new Thickness(0, 4, 0, 4) });
            cell.Children.Add(new TextBlock { Text = glyph.Name, Foreground = (Brush)main.FindResource("TextBrush"), TextAlignment = TextAlignment.Center, FontSize = 12 });
            cell.Children.Add(new TextBlock { Text = $"{glyph.CodePoint:X4}", Foreground = (Brush)main.FindResource("MutedBrush"), TextAlignment = TextAlignment.Center, FontSize = 10 });
            sheet.Children.Add(cell);
        }
        sheet.Measure(new Size(704, double.PositiveInfinity)); sheet.Arrange(new Rect(sheet.DesiredSize)); sheet.UpdateLayout();
        PrototypeSmoke.SaveSnapshot(sheet, Path.Combine(directory, "icon-library.png"));
    }
    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
}
