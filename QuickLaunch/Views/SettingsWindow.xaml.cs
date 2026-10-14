using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;

namespace QuickLaunch.Views;

public partial class SettingsWindow : Window
{
    private readonly Func<HotkeyConfig, (string Message, bool Error)> _apply;
    private readonly Action _resetSize, _exit;
    private readonly StartupService _startup;
    private readonly MainViewModel _model;
    private bool _syncing;
    public SettingsWindow(MainViewModel model, string directory, string registration,
        Func<HotkeyConfig, (string Message, bool Error)> apply, StartupService startup, Action resetSize, Action exit,
        ThemeService? theme = null, bool registrationError = false)
    {
        InitializeComponent();
        DialogTheme.Attach(this, theme);
        _apply = apply; _resetSize = resetSize; _exit = exit; _startup = startup; _model = model;
        var hotkey = model.Hotkey;
        Alt.IsChecked = hotkey.Modifiers.Contains("Alt");
        Control.IsChecked = hotkey.Modifiers.Contains("Control");
        Shift.IsChecked = hotkey.Modifiers.Contains("Shift");
        Windows.IsChecked = hotkey.Modifiers.Contains("Windows");
        MainKey.ItemsSource = ConfigValidation.Keys;
        MainKey.SelectedItem = hotkey.Key;
        SetResult(model.CanEdit ? registration : "当前配置只读，不能修改。", !model.CanEdit || registrationError);
        DirectoryLabel.Text = $"配置目录：{directory}";
        foreach (Control control in new Control[] { Alt, Control, Shift, Windows, MainKey, ApplyButton, ResetSizeButton, StartupBox, WebsiteIconsBox })
            control.IsEnabled = model.CanEdit;
        IconSizes.IsEnabled = model.CanEdit;
        Placements.IsEnabled = model.CanEdit;
        SyncIconSize();
        SyncPlacement();
        WebsiteIconsBox.IsChecked = model.AutoFetchWebsiteIcons;
        _model.PropertyChanged += ModelChanged;
        Closed += (_, _) => _model.PropertyChanged -= ModelChanged;
        try { StartupBox.IsChecked = _startup.IsEnabled; }
        catch (Exception ex) { SetResult(ex.Message, true); StartupBox.IsEnabled = false; }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    private void SetResult(string message, bool error = false)
    {
        Result.Text = message;
        Result.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "AccentBrush");
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IconSize)) SyncIconSize();
        if (e.PropertyName == nameof(MainViewModel.Placement)) SyncPlacement();
        if (e.PropertyName == nameof(MainViewModel.AutoFetchWebsiteIcons)) WebsiteIconsBox.IsChecked = _model.AutoFetchWebsiteIcons;
    }
    private void WebsiteIconsClick(object sender, RoutedEventArgs e) => _model.SetAutoFetchWebsiteIcons(WebsiteIconsBox.IsChecked == true);
    private void SyncIconSize()
    {
        _syncing = true;
        foreach (var button in IconSizes.Children.OfType<RadioButton>()) button.IsChecked = int.Parse((string)button.Tag) == _model.IconSize;
        _syncing = false;
    }
    private void IconSizeChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string size } || !_model.CanEdit) return;
        _model.SetIconSize(int.Parse(size));
    }
    private void SyncPlacement()
    {
        _syncing = true;
        foreach (var button in Placements.Children.OfType<RadioButton>()) button.IsChecked = (string)button.Tag == _model.Placement;
        _syncing = false;
    }
    private void PlacementChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string placement } || !_model.CanEdit) return;
        _model.SetPlacement(placement);
    }
    private void SegmentKeyDown(object sender, KeyEventArgs e)
    {
        if (!_model.CanEdit || e.OriginalSource is not RadioButton current || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var buttons = ((StackPanel)sender).Children.OfType<RadioButton>().ToArray();
        int step = e.Key is Key.Left or Key.Up ? -1 : 1;
        var next = buttons[(Array.IndexOf(buttons, current) + step + buttons.Length) % buttons.Length];
        next.IsChecked = true; next.Focus(); e.Handled = true;
    }
    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        var modifiers = new[] { ("Alt", Alt), ("Control", Control), ("Shift", Shift), ("Windows", Windows) }
            .Where(pair => pair.Item2.IsChecked == true).Select(pair => pair.Item1).ToArray();
        var config = new HotkeyConfig { Modifiers = modifiers, Key = MainKey.SelectedItem as string ?? "" };
        try { ConfigValidation.ValidateHotkey(config); var result = _apply(config); SetResult(result.Message, result.Error); }
        catch (Exception ex) { SetResult(ex.Message, true); }
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void ExitClick(object sender, RoutedEventArgs e) => _exit();
    private void ResetSizeClick(object sender, RoutedEventArgs e)
    {
        if (!_model.CanEdit) return;
        try { _resetSize(); SetResult("已恢复默认位置和大小，配置将自动保存。"); }
        catch (Exception ex) { SetResult(ex.Message, true); }
    }
    private void StartupClick(object sender, RoutedEventArgs e)
    {
        try { _startup.SetEnabled(StartupBox.IsChecked == true); SetResult("自启设置已更新。"); }
        catch (Exception ex) { StartupBox.IsChecked = false; SetResult(ex.Message, true); }
    }
}
