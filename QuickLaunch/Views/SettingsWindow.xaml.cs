using System.Windows;
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
        SettingsNavigation.SetServiceProvider(new PageProvider(viewModel));
        Loaded += (_, _) => { if (SettingsNavigation.SelectedItem is null) NavigateTo("general"); };
        Closed += (_, _) => viewModel.Dispose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    public void NavigateTo(string page) => SettingsNavigation.Navigate(page);

    private sealed class PageProvider(SettingsViewModel viewModel) : IServiceProvider
    {
        private readonly Dictionary<Type, SettingsPage> _pages = [];
        public object? GetService(Type type)
        {
            if (_pages.TryGetValue(type, out var cached)) return cached;
            SettingsPage? page = type == typeof(SettingsGeneralPage) ? new SettingsGeneralPage()
                : type == typeof(SettingsAppearancePage) ? new SettingsAppearancePage()
                : type == typeof(SettingsPanelPage) ? new SettingsPanelPage()
                : type == typeof(SettingsAboutPage) ? new SettingsAboutPage() : null;
            if (page is null) return null;
            page.DataContext = viewModel;
            _pages.Add(type, page);
            return page;
        }
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
