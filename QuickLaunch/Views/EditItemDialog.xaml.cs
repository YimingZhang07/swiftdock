using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using QuickLaunch.Models;
using QuickLaunch.Services;

namespace QuickLaunch.Views;

public partial class EditItemDialog : Wpf.Ui.Controls.FluentWindow
{
    private IconService? _icons;
    private CancellationTokenSource? _refresh;
    private bool _ready, _syncing, _closed, _settingName, _autoName;
    public LaunchItem? Result { get; private set; }
    internal LaunchKind SelectedKind { get; private set; }
    internal Task TargetRefresh { get; private set; } = Task.CompletedTask;
    internal Func<CommonLocation, string> ResolveLocation { get; set; } = CommonLocations.Resolve;

    public EditItemDialog(LaunchItem? item, IconService? icons = null, bool openIconPicker = false)
    {
        InitializeComponent(); _icons = icons; Title = item is null ? "添加项目" : "编辑项目";
        InitializeIconPicker(item?.Icon);
        NameBox.Text = item?.Name ?? ""; PathBox.Text = item?.Path ?? "";
        ArgumentsBox.Text = item?.Arguments ?? ""; WorkingBox.Text = item?.WorkingDirectory ?? "";
        ElevateBox.IsChecked = item?.RunAsAdmin ?? false;
        SaveButton.Content = item is null ? "添加" : "保存";
        AdvancedOptions.IsExpanded = item is { Kind: LaunchKind.App }
            && (item.Arguments.Length > 0 || item.WorkingDirectory.Length > 0 || item.RunAsAdmin);
        _ready = true;
        SelectKind(item?.Kind ?? LaunchKind.App);
        Closed += (_, _) => { _closed = true; IconPopup.IsOpen = false; CancelRefresh(); };
        Loaded += (_, _) =>
        {
            _icons ??= (Owner as LauncherWindow)?.Icons ?? (Application.Current.MainWindow as LauncherWindow)?.Icons;
            QueueRefresh(false); NameBox.Focus(); NameBox.SelectAll();
            if (openIconPicker) Dispatcher.BeginInvoke(OpenIconPicker);
        };
    }

    private void NameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingName) _autoName = false;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_ready) QueueRefresh(false, immediate: true);
    }
    private void FillName(string name)
    {
        if (!string.IsNullOrWhiteSpace(NameBox.Text) && !_autoName) return;
        _settingName = true;
        try { NameBox.Text = name[..Math.Min(name.Length, NameBox.MaxLength)]; _autoName = true; }
        finally { _settingName = false; }
    }
    private void SelectKind(LaunchKind kind)
    {
        _syncing = true;
        try
        {
            SelectedKind = kind;
            foreach (var card in TypeCards.Children.OfType<RadioButton>()) card.IsChecked = (LaunchKind)card.Tag == kind;
        }
        finally { _syncing = false; }
        UpdateFields();
    }
    private void TypeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing || sender is not RadioButton { Tag: LaunchKind kind }) return;
        SelectedKind = kind; Error.Text = ""; UpdateFields();
        // A card selection cancels pending text inference and never starts new inference.
        QueueRefresh(false);
    }
    private void TypeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not RadioButton current || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var cards = TypeCards.Children.OfType<RadioButton>().ToArray();
        int step = e.Key is Key.Left or Key.Up ? -1 : 1;
        var next = cards[(Array.IndexOf(cards, current) + step + cards.Length) % cards.Length];
        next.IsChecked = true; next.Focus(); e.Handled = true;
    }
    private void PathChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        Error.Text = "";
        if (ItemEditorValidation.InferUrlName(PathBox.Text) is { } host)
        {
            SelectKind(LaunchKind.Url); FillName(host);
        }
        UpdateFields(); QueueRefresh(true);
    }
    private void UpdateFields()
    {
        bool application = SelectedKind == LaunchKind.App;
        bool shortcut = Path.GetExtension(ItemEditorValidation.NormalizeTarget(PathBox.Text)).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        TargetLabel.Text = SelectedKind switch { LaunchKind.App => "程序路径", LaunchKind.File => "文件路径", LaunchKind.Folder => "文件夹路径", _ => "网址" };
        BrowseButton.Visibility = SelectedKind == LaunchKind.Url ? Visibility.Collapsed : Visibility.Visible;
        PathBox.PlaceholderText = SelectedKind == LaunchKind.Url ? UrlHint.Text : TargetLabel.Text;
        LocationChips.Visibility = SelectedKind == LaunchKind.Folder ? Visibility.Visible : Visibility.Collapsed;
        AdvancedOptions.Visibility = application ? Visibility.Visible : Visibility.Collapsed;
        ArgumentsBox.IsEnabled = WorkingBox.IsEnabled = application && !shortcut;
        ElevateBox.IsEnabled = application;
        ShortcutHint.Visibility = shortcut ? Visibility.Visible : Visibility.Collapsed;
        PreviewGlyph.Text = SelectedKind switch { LaunchKind.File => "\uE8A5", LaunchKind.Folder => "\uE8B7", LaunchKind.Url => "\uE774", _ => "\uE71D" };
    }
    private void CancelRefresh()
    {
        _refresh?.Cancel(); _refresh?.Dispose(); _refresh = null;
    }
    private void QueueRefresh(bool inferKind, bool immediate = false)
    {
        CancelRefresh();
        PreviewImage.Source = null; PreviewGlyph.Visibility = Visibility.Visible;
        if (_closed) return;
        _refresh = new();
        TargetRefresh = RefreshTargetAsync(PathBox.Text, inferKind, immediate, _refresh.Token);
    }
    private async Task RefreshTargetAsync(string text, bool inferKind, bool immediate, CancellationToken cancellation)
    {
        try
        {
            if (!immediate) await Task.Delay(200, cancellation);
            string path = ItemEditorValidation.NormalizeTarget(text);
            bool web = LaunchService.IsWebUrl(path);
            if (!web) path = Environment.ExpandEnvironmentVariables(path);
            var facts = !inferKind && _customIcon is not null ? (File: false, Directory: false)
                : await Task.Run(() => (File: !web && File.Exists(path), Directory: !web && Directory.Exists(path)), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (inferKind && ItemEditorValidation.InferKind(path, facts.File, facts.Directory) is { } kind) SelectKind(kind);
            if (_icons is null || (_customIcon is null && !(SelectedKind == LaunchKind.Url ? web : facts.File || facts.Directory))) return;
            int pixels = IconService.PixelSize(40, VisualTreeHelper.GetDpi(this).DpiScaleX);
            var target = new LaunchItem("", path, SelectedKind) { Icon = _customIcon };
            var image = IconService.UsesFavicon(target) ? IconService.Globe(pixels) : await _icons.GetAsync(target, pixels, cancellation);
            cancellation.ThrowIfCancellationRequested();
            PreviewImage.Source = image;
            PreviewGlyph.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            if (IconService.UsesFavicon(target))
            {
                var favicon = await _icons.GetFaviconAsync(target, pixels, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (_icons.AutoFetchWebsiteIcons && favicon is not null) PreviewImage.Source = favicon;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ArgumentException or IOException or UnauthorizedAccessException)
        { /* Typing invalid or unavailable paths keeps the kind glyph visible. */ }
    }
    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        if (SelectedKind == LaunchKind.Folder)
        {
            var dialog = new OpenFolderDialog { Title = "选择文件夹" };
            if (dialog.ShowDialog(this) == true) FillPath(dialog.FolderName, Path.GetFileName(Path.TrimEndingDirectorySeparator(dialog.FolderName)));
        }
        else
        {
            var dialog = new OpenFileDialog { Title = SelectedKind == LaunchKind.App ? "选择程序" : "选择文件", CheckFileExists = true,
                Filter = SelectedKind == LaunchKind.App ? "程序 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd" : "所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true) FillPath(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
        }
    }
    private void FillPath(string path, string name)
    {
        PathBox.Text = ItemIdentity.PortablePath(path); FillName(name.Length > 0 ? name : path);
    }
    private void LocationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CommonLocation location } button) return;
        try { FillPath(ResolveLocation(location), (string)button.Content); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Error.Text = "无法读取该常用位置。"; }
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled) return;
        string name = NameBox.Text.Trim(), path = ItemEditorValidation.NormalizeTarget(PathBox.Text);
        string arguments = ArgumentsBox.Text, working = WorkingBox.Text.Trim(); bool admin = ElevateBox.IsChecked == true;
        LaunchKind selected = SelectedKind;
        if (name.Length == 0 || path.Length == 0) { Error.Text = "名称和目标不能为空。"; return; }
        string? icon = _customIcon;
        IconPopup.IsOpen = false;
        CancelRefresh(); EditorBody.IsEnabled = SaveButton.IsEnabled = false; Error.Text = "正在检查目标…";
        try
        {
            var item = await Task.Run(() =>
            {
                string expanded = selected == LaunchKind.Url ? path : Environment.ExpandEnvironmentVariables(path);
                var kind = ItemEditorValidation.Validate(selected, expanded, selected != LaunchKind.Url && Directory.Exists(expanded),
                    Environment.ExpandEnvironmentVariables(working));
                if (kind != LaunchKind.Url) path = ItemIdentity.PortablePath(expanded);
                bool shortcut = kind == LaunchKind.App && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
                return new LaunchItem(name, path, kind, kind == LaunchKind.App && !shortcut ? arguments : "",
                    kind == LaunchKind.App && !shortcut ? working : "", kind == LaunchKind.App && admin) { Icon = icon };
            });
            if (_closed) return;
            Result = item; DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { if (!_closed) { Error.Text = ex.Message; EditorBody.IsEnabled = SaveButton.IsEnabled = true; } }
    }
}
