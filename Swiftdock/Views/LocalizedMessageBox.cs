using System.Windows;
using System.Windows.Automation;
using Swiftdock.Services;

namespace Swiftdock.Views;

// WPF buttons and content follow the app language, including an open error dialog.
internal static class LocalizedMessageBox
{
    internal static async Task<MessageBoxResult> ShowAsync(UiText content, UiText title, bool choices = false)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            MaxWidth = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI")
        };
        L.LocalizeChrome(dialog);
        L.Bind(dialog, Window.TitleProperty, title);
        L.Bind(dialog, AutomationProperties.NameProperty, title);
        L.Bind(dialog, System.Windows.Controls.ContentControl.ContentProperty, content);
        L.Bind(dialog, Wpf.Ui.Controls.MessageBox.CloseButtonTextProperty, L.Text(choices ? "Cancel" : "Ok"));
        if (choices)
        {
            L.Bind(dialog, Wpf.Ui.Controls.MessageBox.PrimaryButtonTextProperty, L.Text("Yes"));
            L.Bind(dialog, Wpf.Ui.Controls.MessageBox.SecondaryButtonTextProperty, L.Text("No"));
        }
        var result = await dialog.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary ? MessageBoxResult.Yes
            : result == Wpf.Ui.Controls.MessageBoxResult.Secondary ? MessageBoxResult.No
            : choices ? MessageBoxResult.Cancel : MessageBoxResult.OK;
    }
}
