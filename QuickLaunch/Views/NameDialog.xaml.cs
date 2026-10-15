using QuickLaunch.ViewModels;

namespace QuickLaunch.Views;

public partial class NameDialog : Wpf.Ui.Controls.FluentWindow
{
    public NameDialogViewModel ViewModel { get; }
    public NameDialog(NameDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent(); DataContext = viewModel;
        viewModel.Accepted += Accept;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        Closed += (_, _) => viewModel.Accepted -= Accept;
    }
    private void Accept() => DialogResult = true;
}