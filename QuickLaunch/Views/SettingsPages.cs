using System.Windows.Controls;
using System.Windows.Input;
using QuickLaunch.ViewModels;

namespace QuickLaunch.Views;

// Pages share one SettingsViewModel; only segmented-control keyboard navigation is view logic.
public class SettingsPage : UserControl
{
    // NavigationView hosts pages in a Frame, which stops window-level inheritance.
    // Keep a resource expression here so an open page follows live theme changes.
    public SettingsPage() => SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

    protected void SegmentKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { CanEdit: true } || e.OriginalSource is not RadioButton current || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var buttons = ((StackPanel)sender).Children.OfType<RadioButton>().ToArray();
        int step = e.Key is Key.Left or Key.Up ? -1 : 1;
        var next = buttons[(Array.IndexOf(buttons, current) + step + buttons.Length) % buttons.Length];
        next.SetCurrentValue(RadioButton.IsCheckedProperty, true); next.Focus(); e.Handled = true;
    }
}

public partial class SettingsGeneralPage : SettingsPage
{
    public SettingsGeneralPage() => InitializeComponent();
}

public partial class SettingsAppearancePage : SettingsPage
{
    public SettingsAppearancePage() => InitializeComponent();
}

public partial class SettingsPanelPage : SettingsPage
{
    public SettingsPanelPage() => InitializeComponent();
}

public partial class SettingsAboutPage : SettingsPage
{
    public SettingsAboutPage() => InitializeComponent();
}

