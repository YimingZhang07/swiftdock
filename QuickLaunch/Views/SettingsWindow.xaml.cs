using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickLaunch.Models;
using QuickLaunch.Services;

namespace QuickLaunch.Views;

public partial class SettingsWindow : Window
{
    private readonly Func<HotkeyConfig, bool, string> _apply;
    private readonly StartupService _startup;
    public SettingsWindow(HotkeyConfig hotkey, bool hide, bool canEdit, string directory, string registration,
        Func<HotkeyConfig, bool, string> apply, StartupService startup)
    {
        InitializeComponent();
        _apply = apply;
        _startup = startup;
        Alt.IsChecked = hotkey.Modifiers.Contains("Alt");
        Control.IsChecked = hotkey.Modifiers.Contains("Control");
        Shift.IsChecked = hotkey.Modifiers.Contains("Shift");
        Windows.IsChecked = hotkey.Modifiers.Contains("Windows");
        MainKey.ItemsSource = ConfigValidation.Keys;
        MainKey.SelectedItem = hotkey.Key;
        HideOnDeactivate.IsChecked = hide;
        Result.Text = canEdit ? registration : "当前配置只读，不能修改。";
        DirectoryLabel.Text = $"配置目录：{directory}";
        ApplyButton.IsEnabled = canEdit;
        StartupBox.IsEnabled = canEdit;
        try { StartupBox.IsChecked = _startup.IsEnabled; } catch (Exception ex) { Result.Text = ex.Message; StartupBox.IsEnabled = false; }
        foreach (Control control in new Control[] { Alt, Control, Shift, Windows, MainKey, HideOnDeactivate }) control.IsEnabled = canEdit;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        var modifiers = new[] { ("Alt", Alt), ("Control", Control), ("Shift", Shift), ("Windows", Windows) }
            .Where(pair => pair.Item2.IsChecked == true).Select(pair => pair.Item1).ToArray();
        var config = new HotkeyConfig { Modifiers = modifiers, Key = MainKey.SelectedItem as string ?? "" };
        try { ConfigValidation.ValidateHotkey(config); Result.Text = _apply(config, HideOnDeactivate.IsChecked == true); }
        catch (Exception ex) { Result.Text = ex.Message; }
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void StartupClick(object sender, RoutedEventArgs e)
    {
        try { _startup.SetEnabled(StartupBox.IsChecked == true); Result.Text = "自启设置已更新。"; }
        catch (Exception ex) { StartupBox.IsChecked = false; Result.Text = ex.Message; }
    }
}
