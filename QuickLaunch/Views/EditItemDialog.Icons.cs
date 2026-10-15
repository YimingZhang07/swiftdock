using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;

namespace QuickLaunch.Views;

public partial class EditItemDialog
{
    private void InitializeIconPicker()
    {
        var selection = new SelectionConverter();
        foreach (var glyph in ViewModel.Glyphs)
        {
            var choice = new RadioButton { Tag = glyph.CodePoint, GroupName = "IconGlyph", ToolTip = glyph.Name,
                Style = (Style)FindResource("IconChoice"), Command = ViewModel.SelectGlyphCommand, CommandParameter = glyph.CodePoint,
                Content = new TextBlock { Text = glyph.Text, FontFamily = new FontFamily(IconCatalog.FontName), FontSize = 18, Foreground = Brushes.White } };
            choice.SetBinding(BackgroundProperty, new Binding(nameof(EditItemViewModel.IconColor)));
            choice.SetBinding(RadioButton.IsCheckedProperty, new Binding(nameof(EditItemViewModel.SelectedGlyph))
                { Mode = BindingMode.OneWay, Converter = selection, ConverterParameter = glyph.CodePoint });
            AutomationProperties.SetName(choice, glyph.Name);
            GlyphGrid.Children.Add(choice);
        }
        foreach (var color in ViewModel.Colors)
        {
            var choice = new RadioButton { Tag = color.Hex, GroupName = "IconColor", ToolTip = color.Name,
                Style = (Style)FindResource("IconChoice"), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Hex)),
                Command = ViewModel.SelectColorCommand, CommandParameter = color.Hex };
            choice.SetBinding(RadioButton.IsCheckedProperty, new Binding(nameof(EditItemViewModel.IconColor))
                { Mode = BindingMode.OneWay, Converter = selection, ConverterParameter = color.Hex });
            AutomationProperties.SetName(choice, color.Name);
            PaletteGrid.Children.Add(choice);
        }
    }
    internal void OpenIconPicker() => ViewModel.OpenIconPickerCommand.Execute(null);
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
            IconPopup.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false); e.Handled = true;
            Dispatcher.BeginInvoke(() => { if (!_closed && !IconPopup.IsOpen) IconPreview.Focus(); });
        }
    }
    private static void ExecuteChoice(System.Windows.Controls.Primitives.ButtonBase button)
    {
        if (button.Command?.CanExecute(button.CommandParameter) == true) button.Command.Execute(button.CommandParameter);
    }
    private void PickerKeyDown(object sender, KeyEventArgs e)
    {
        EditorKeyDown(sender, e);
        // Enter activates a picker choice, never the editor's default Save button.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button button)
        { ExecuteChoice(button); e.Handled = true; }
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
        { ExecuteChoice(current); e.Handled = true; }
    }
}