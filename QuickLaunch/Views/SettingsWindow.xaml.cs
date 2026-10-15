using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickLaunch.ViewModels;

namespace QuickLaunch.Views;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    public SettingsViewModel ViewModel { get; }
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent(); DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    private void SegmentKeyDown(object sender, KeyEventArgs e)
    {
        if (!ViewModel.CanEdit || e.OriginalSource is not RadioButton current || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var buttons = ((StackPanel)sender).Children.OfType<RadioButton>().ToArray();
        int step = e.Key is Key.Left or Key.Up ? -1 : 1;
        var next = buttons[(Array.IndexOf(buttons, current) + step + buttons.Length) % buttons.Length];
        next.SetCurrentValue(RadioButton.IsCheckedProperty, true); next.Focus(); e.Handled = true;
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}