using System.Windows;
using QuickLaunch.Services;

namespace QuickLaunch.Views;

public partial class NameDialog : Window
{
    public string Value => NameBox.Text.Trim();
    public NameDialog(string title, string value, ThemeService? theme = null)
    {
        InitializeComponent(); Title = title; NameBox.Text = value;
        DialogTheme.Attach(this, theme);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }
    private void Accept(object sender, RoutedEventArgs e)
    { if (Value.Length == 0) Error.Text = "请输入名称。"; else DialogResult = true; }
}
