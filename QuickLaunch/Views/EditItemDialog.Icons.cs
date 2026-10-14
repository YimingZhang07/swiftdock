using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using QuickLaunch.Services;

namespace QuickLaunch.Views;

public partial class EditItemDialog
{
    private string? _customIcon;
    private int _glyphCode = IconCatalog.Glyphs[0].CodePoint;
    private string _iconColor = IconCatalog.Colors[0].Hex;
    internal string? SelectedIcon => _customIcon;

    private void InitializeIconPicker(string? icon)
    {
        _customIcon = IconSpec.Normalize(icon);
        if (IconSpec.Parse(_customIcon) is { IsGlyph: true } spec) { _glyphCode = spec.CodePoint; _iconColor = spec.Color!; }
        foreach (var glyph in IconCatalog.AvailableGlyphs)
        {
            var choice = new RadioButton { Tag = glyph.CodePoint, GroupName = "IconGlyph", ToolTip = glyph.Name,
                Style = (Style)FindResource("IconChoice"), Background = IconBrush(),
                Content = new TextBlock { Text = glyph.Text, FontFamily = new FontFamily(IconCatalog.FontName), FontSize = 18, Foreground = Brushes.White },
                IsChecked = IconSpec.Parse(_customIcon) is { IsGlyph: true } && _glyphCode == glyph.CodePoint };
            AutomationProperties.SetName(choice, glyph.Name);
            choice.Click += (_, _) => { _glyphCode = glyph.CodePoint; SelectGlyph(); };
            GlyphGrid.Children.Add(choice);
        }
        foreach (var color in IconCatalog.Colors)
        {
            var choice = new RadioButton { Tag = color.Hex, GroupName = "IconColor", ToolTip = color.Name,
                Style = (Style)FindResource("IconChoice"), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Hex)),
                IsChecked = _iconColor == color.Hex };
            AutomationProperties.SetName(choice, color.Name);
            choice.Click += (_, _) => { _iconColor = color.Hex; SelectGlyph(); };
            PaletteGrid.Children.Add(choice);
        }
    }

    private Brush IconBrush()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_iconColor)); brush.Freeze(); return brush;
    }
    private void SelectGlyph()
    {
        if (!IconCatalog.AvailableGlyphs.Any(g => g.CodePoint == _glyphCode))
        {
            if (IconCatalog.AvailableGlyphs.FirstOrDefault() is not { } first) return;
            _glyphCode = first.CodePoint;
        }
        _customIcon = $"glyph:{_glyphCode:X4}:{_iconColor}";
        foreach (var choice in GlyphGrid.Children.OfType<RadioButton>())
        { choice.IsChecked = (int)choice.Tag == _glyphCode; choice.Background = IconBrush(); }
        QueueRefresh(false, immediate: true);
    }
    internal void OpenIconPicker() => IconPopup.IsOpen = true;
    private void IconPreviewClick(object sender, RoutedEventArgs e) => OpenIconPicker();
    private void IconPopupOpened(object? sender, EventArgs e)
    {
        var choices = GlyphGrid.Children.OfType<RadioButton>().ToArray();
        Control focus = choices.FirstOrDefault(c => c.IsChecked == true) ?? choices.FirstOrDefault() ?? (Control)IconFileButton;
        focus.Focus(); focus.BringIntoView();
    }
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (IconPopup.IsOpen && e.Key == Key.Escape)
        {
            IconPopup.IsOpen = false; e.Handled = true;
            Dispatcher.BeginInvoke(() => { if (!_closed && !IconPopup.IsOpen) IconPreview.Focus(); });
        }
    }
    private void PickerKeyDown(object sender, KeyEventArgs e)
    {
        EditorKeyDown(sender, e);
        // Enter activates a picker choice, never the editor's default Save button.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button button)
        { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
    }
    private void ChoiceKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Panel panel || e.OriginalSource is not RadioButton current) return;
        var choices = panel.Children.OfType<RadioButton>().ToArray();
        int index = Array.IndexOf(choices, current);
        int next = e.Key switch
        {
            Key.Left => (index + choices.Length - 1) % choices.Length,
            Key.Right => (index + 1) % choices.Length,
            Key.Up => Math.Max(0, index - 8), Key.Down => Math.Min(choices.Length - 1, index + 8),
            Key.Home => 0, Key.End => choices.Length - 1, _ => -1
        };
        if (next >= 0) { choices[next].Focus(); choices[next].BringIntoView(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        { current.IsChecked = true; current.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent)); e.Handled = true; }
    }
    private void IconFileClick(object sender, RoutedEventArgs e)
    {
        IconPopup.IsOpen = false;
        var dialog = new OpenFileDialog { Title = "选择图标", CheckFileExists = true,
            Filter = "图标和图片 (*.ico;*.png;*.jpg;*.bmp;*.exe;*.dll)|*.ico;*.png;*.jpg;*.bmp;*.exe;*.dll" };
        if (dialog.ShowDialog(this) == true) SetFileIcon(dialog.FileName);
    }
    internal void SetFileIcon(string path)
    {
        _customIcon = IconSpec.Normalize("file:" + ItemIdentity.PortablePath(path));
        foreach (var choice in GlyphGrid.Children.OfType<RadioButton>()) choice.IsChecked = false;
        QueueRefresh(false, immediate: true);
    }
    private void IconResetClick(object sender, RoutedEventArgs e)
    {
        _customIcon = null;
        foreach (var choice in GlyphGrid.Children.OfType<RadioButton>()) choice.IsChecked = false;
        QueueRefresh(false, immediate: true);
    }
}
