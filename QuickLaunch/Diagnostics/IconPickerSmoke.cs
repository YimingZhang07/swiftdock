using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        using var themeScope = new ThemeSmokeScope();
        var original = new LaunchItem("开发工具", "https://example.com/code", LaunchKind.Url) { Icon = "glyph:E943:#5B4FD1" };
        var dialog = new EditItemDialog(original, icons: main.Icons, openIconPicker: true) { Owner = main };
        using var picker = new PopupSmokeScope(dialog.IconPopup);
        bool requested = false;
        var openProperty = DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup));
        void OpenChanged(object? sender, EventArgs e) => requested |= dialog.IconPopup.IsOpen;
        openProperty.AddValueChanged(dialog.IconPopup, OpenChanged);
        var group = main.SelectedGroup;
        var samples = new[]
        {
            original, new LaunchItem("云端文件", "https://example.com/cloud", LaunchKind.Url) { Icon = "glyph:E753:#0F7B6C" },
            new LaunchItem("音乐", "https://example.com/music", LaunchKind.Url) { Icon = "glyph:E8D6:#B83280" },
            new LaunchItem("收藏", "https://example.com/star", LaunchKind.Url) { Icon = "glyph:E735:#B7791F" }
        };
        try
        {
            ThemeSmokeScope.Apply(true);
            dialog.Show(); WindowSmoke.Pump();
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            WindowSmoke.Pump();
            PopupSmokeScope.Layout(dialog.PickerSurface);
            check("icon-picker-opens-on-request", requested && ReferenceEquals(dialog.IconPopup.Child, dialog.PickerSurface));
            var glyphs = dialog.GlyphGrid.Children.OfType<RadioButton>().ToArray();
            var colors = dialog.PaletteGrid.Children.OfType<RadioButton>().ToArray();
            check("icon-picker-preselects-glyph-and-color", glyphs.Single(c => c.IsChecked == true).Tag is 0xE943 && (string)colors.Single(c => c.IsChecked == true).Tag == "#5B4FD1");
            check("icon-picker-grid-and-accessible-names", dialog.GlyphGrid.Columns == 8 && glyphs.All(c => c.Width == 36 && c.Height == 36
                && AutomationProperties.GetName(c) == (string)c.ToolTip) && glyphs.Length == IconCatalog.AvailableGlyphs.Count());
            check("icon-picker-dismisses-outside", !dialog.IconPopup.StaysOpen);
            foreach (bool dark in new[] { true, false })
            {
                ThemeSmokeScope.Apply(dark);
                dialog.UpdateLayout(); WindowSmoke.Pump();
                SavePopupSnapshot(dialog, Path.Combine(directory, dark ? "icon-picker.png" : "icon-picker-light.png"));
            }
            ThemeSmokeScope.Apply(true);
            // The full library sheet also makes glyph/name mistakes visible during review.
            await SaveLibraryAsync(main, directory);
            var selected = glyphs.Single(c => c.IsChecked == true);
            selected.Focus();
            var arrow = PressKey(selected, Key.Right, dialog);
            check("icon-picker-arrow-navigation", arrow.Handled);
            PressKey(glyphs[1], Key.Enter, dialog);
            check("icon-picker-enter-selects-without-saving", dialog.Result is null && dialog.SelectedIcon == $"glyph:{(int)glyphs[1].Tag:X4}:#5B4FD1");
            var previous = dialog.PreviewImage.Source;
            colors[3].RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            check("icon-picker-selection-updates-preview", dialog.SelectedIcon == $"glyph:{(int)glyphs[1].Tag:X4}:#C2410C" && dialog.PreviewImage.Source is not null && dialog.PreviewImage.Source != previous);
            picker.Close(() => PressKey(glyphs[1], Key.Escape, dialog));
            check("icon-picker-escape-keeps-editor-open", picker.Closed && dialog.IsVisible);
            picker.Open(dialog.OpenIconPicker);
            dialog.IconResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            check("icon-picker-restore-default", dialog.SelectedIcon is null && dialog.PreviewImage.Source is not null && glyphs.All(c => c.IsChecked != true));
            picker.Close();
            check("icon-picker-closed-before-file-preview", picker.Closed);
            dialog.SetFileIcon(Path.Combine(directory, "icon-picker.png"));
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            check("icon-picker-file-preview", dialog.SelectedIcon == "file:" + ItemIdentity.PortablePath(Path.Combine(directory, "icon-picker.png")) && dialog.PreviewImage.Source is not null);
            dialog.PathBox.Clear();
            glyphs[0].RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            await dialog.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(10));
            check("icon-picker-works-before-target-entry", dialog.PreviewImage.Source is not null);
            dialog.Close(); WindowSmoke.Pump();

            var saved = EditItemDialogSmoke.Save(main, original, d => d.NameBox.Text = "保留图标");
            check("icon-editor-save-preserves-custom-icon", saved?.Icon == original.Icon);
            var reset = EditItemDialogSmoke.Save(main, original, d => d.IconResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            check("icon-editor-save-persists-reset", reset is not null && reset.Icon is null);
            var file = EditItemDialogSmoke.Save(main, original, d => d.SetFileIcon(Path.Combine(directory, "icon-picker.png")));
            check("icon-editor-save-persists-portable-file", file?.Icon == "file:" + ItemIdentity.PortablePath(Path.Combine(directory, "icon-picker.png")));

            foreach (var sample in samples) group.Items.Add(sample);
            main.SelectGroup(group); main.UpdateLayout(); await main.LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            WindowSmoke.Pump();
            check("launcher-renders-custom-icons", samples.All(s => s.Visual.Image is not null && s.ErrorMessage is null));
            PrototypeSmoke.SaveSnapshot(main, Path.Combine(directory, "launcher-custom-icons.png"));
            var tile = LauncherWindow.Descendants<ListBoxItem>(main.GroupHost).First(b => b.DataContext is LaunchItem i && i.Id == original.Id);
            var menu = tile.ContextMenu; menu.PlacementTarget = tile;
            var entries = menu.Items.OfType<MenuItem>().ToArray();
            int edit = Array.FindIndex(entries, e => (string)e.Header == "编辑…");
            InspectItemMenu(main, menu, () => check("icon-context-menu-after-edit", edit >= 0
                && (string)entries[edit + 1].Header == "更换图标…" && entries[edit + 1].IsEnabled && (string)entries[edit + 1].Tag == "edit"));
            check("icon-context-menu-closed-before-read-only", !menu.IsOpen && !PopupSmokeScope.MenuIsOpen(main));
            main.ViewModel.Suspend(true);
            try
            {
                InspectItemMenu(main, menu, () => check("icon-context-menu-disabled-when-not-editable", !entries[edit + 1].IsEnabled));
                check("icon-context-menu-closed-before-restoring-editability", !menu.IsOpen && !PopupSmokeScope.MenuIsOpen(main));
            }
            finally { main.ViewModel.Suspend(false); }
        }
        finally
        {
            picker.Close();
            openProperty.RemoveValueChanged(dialog.IconPopup, OpenChanged);
            dialog.Close(); WindowSmoke.Pump();
            foreach (var sample in samples) group.Items.Remove(sample);
            main.SelectGroup(group);
            themeScope.Dispose();
        }
    }

    private static void InspectItemMenu(LauncherWindow main, ContextMenu menu, Action inspect)
    {
        using var lifetime = new PopupSmokeScope(menu, main);
        lifetime.Open(); lifetime.Close();
        // Also test the menu's production event handlers when native creation is unavailable.
        // Keep this routed-event pair separate from the real popup's observed lifecycle.
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        try { inspect(); }
        finally { lifetime.Close(() => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent))); }
    }

    private static KeyEventArgs PressKey(UIElement element, Key key, Window owner)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element) ?? PresentationSource.FromVisual(owner),
            Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(e);
        return e;
    }

    private static void SavePopupSnapshot(EditItemDialog dialog, string path)
    {
        // Popup has a separate presentation source. Composite both actual WPF visuals at their screen-relative DIP positions.
        var popup = dialog.PickerSurface;
        PopupSmokeScope.Layout(popup);
        Point location = PresentationSource.FromVisual(popup) is not null ? dialog.PointFromScreen(popup.PointToScreen(new Point()))
            : dialog.IconPreview.TranslatePoint(new Point(dialog.IconPopup.HorizontalOffset, dialog.IconPreview.ActualHeight + dialog.IconPopup.VerticalOffset), dialog);
        var bounds = new Rect(0, 0, dialog.ActualWidth, dialog.ActualHeight);
        var popupBounds = new Rect(location, popup.RenderSize); bounds.Union(popupBounds); bounds.Inflate(24, 24);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)dialog.FindResource("ApplicationBackgroundBrush"), null, new Rect(0, 0, bounds.Width, bounds.Height));
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
        var sheet = new System.Windows.Controls.Primitives.UniformGrid { Columns = 8, Background = (Brush)main.FindResource("ApplicationBackgroundBrush"), Width = 704 };
        foreach (var glyph in IconCatalog.AvailableGlyphs)
        {
            var cell = new StackPanel { Width = 88, Height = 84 };
            cell.Children.Add(new Image { Source = await main.Icons.GetAsync(new LaunchItem("", "", LaunchKind.Url) { Icon = $"glyph:{glyph.CodePoint:X4}:#2F6FDB" }, 48), Width = 40, Height = 40, Margin = new Thickness(0, 4, 0, 4) });
            cell.Children.Add(new TextBlock { Text = glyph.Name, Foreground = (Brush)main.FindResource("TextFillColorPrimaryBrush"), TextAlignment = TextAlignment.Center, FontSize = 12 });
            cell.Children.Add(new TextBlock { Text = $"{glyph.CodePoint:X4}", Foreground = (Brush)main.FindResource("TextFillColorSecondaryBrush"), TextAlignment = TextAlignment.Center, FontSize = 10 });
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
