using System.IO;
using System.Windows;
using Microsoft.Win32;
using QuickLaunch.Models;
using QuickLaunch.Services;

namespace QuickLaunch.Views;

public partial class EditItemDialog : Window
{
    private readonly LaunchItem? _original;
    private bool _closed;
    public LaunchItem? Result { get; private set; }
    public EditItemDialog(LaunchItem? item)
    {
        InitializeComponent(); _original = item; Title = item is null ? "添加条目" : "编辑条目";
        NameBox.Text = item?.Name ?? ""; PathBox.Text = item?.Path ?? "";
        ArgumentsBox.Text = item?.Arguments ?? ""; WorkingBox.Text = item?.WorkingDirectory ?? "";
        ElevateBox.IsChecked = item?.RunAsAdmin ?? false;
        Closed += (_, _) => _closed = true;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        UpdateFields();
    }
    private void PathChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (ArgumentsBox is not null) UpdateFields(); }
    private void UpdateFields()
    {
        string extension = Path.GetExtension(PathBox.Text.Trim());
        bool application = !LaunchService.IsWebUrl(PathBox.Text.Trim()) && new[] { ".exe", ".bat", ".cmd", ".lnk" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        ArgumentsBox.IsEnabled = WorkingBox.IsEnabled = application && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        ElevateBox.IsEnabled = application;
    }
    private void ChooseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择程序或文件", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { PathBox.Text = dialog.FileName; if (NameBox.Text.Length == 0) NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName); }
    }
    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择文件夹" };
        if (dialog.ShowDialog(this) == true) { PathBox.Text = dialog.FolderName; if (NameBox.Text.Length == 0) NameBox.Text = Path.GetFileName(dialog.FolderName); }
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim(), path = PathBox.Text.Trim().Trim('"');
        string arguments = ArgumentsBox.Text, working = WorkingBox.Text.Trim(); bool admin = ElevateBox.IsChecked == true;
        if (name.Length == 0 || path.Length == 0) { Error.Text = "名称和目标不能为空。"; return; }
        SaveButton.IsEnabled = false; Error.Text = "正在检查目标…";
        try
        {
            var item = await Task.Run(() =>
            {
                LaunchKind kind;
                if (LaunchService.IsWebUrl(path)) kind = LaunchKind.Url;
                else
                {
                    string expanded = Environment.ExpandEnvironmentVariables(path);
                    if (!Path.IsPathFullyQualified(expanded) || expanded.Contains('"')) throw new ArgumentException("请输入绝对路径，或有效 http/https 网址。");
                    if (Directory.Exists(expanded)) kind = LaunchKind.Folder;
                    else if (new[] { ".exe", ".lnk", ".bat", ".cmd" }.Contains(Path.GetExtension(expanded), StringComparer.OrdinalIgnoreCase)) kind = LaunchKind.App;
                    else kind = _original?.Path == path ? _original.Kind : LaunchKind.File;
                    path = ItemIdentity.PortablePath(expanded);
                }
                bool shortcut = kind == LaunchKind.App && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
                if (kind == LaunchKind.App && !shortcut && working.Length > 0
                    && (!Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(working)) || working.Contains('"')))
                    throw new ArgumentException("工作目录必须是有效绝对路径。");
                return new LaunchItem(name, path, kind, kind == LaunchKind.App && !shortcut ? arguments : "",
                    kind == LaunchKind.App && !shortcut ? working : "", kind == LaunchKind.App && admin);
            });
            if (_closed) return;
            Result = item; DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { if (!_closed) { Error.Text = ex.Message; SaveButton.IsEnabled = true; } }
    }
}
