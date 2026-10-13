using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuickLaunch.DragDrop;
using QuickLaunch.Interop;
using QuickLaunch.Models;
using QuickLaunch.Services;

namespace QuickLaunch.Views;

public partial class LauncherWindow : Window
{
    private readonly Dictionary<LaunchGroup, ItemsControl> _grids = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _dropWait = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _externalDrag, _menuOpen, _closing, _awaitingDrop;
    private DateTime _dropDeadline;
    private int _showGeneration;
    private EventHandler? _renderHandler;
    private DispatcherTimer? _measureTimeout;
    public DiagnosticLog Log { get; }
    public Grid CaptureRoot => Root;
    public IReadOnlyList<LaunchGroup> Groups { get; }
    public LaunchGroup SelectedGroup { get; private set; }
    public DragCoordinator DragSession { get; }
    public bool Importing { get; private set; }
    public bool Pinned { get => KeepVisible.IsChecked == true; set => KeepVisible.IsChecked = value; }
    public Action? ExitRequested { get; set; }
    public string StatusText => Status.Text;

    public LauncherWindow(DiagnosticLog log)
    {
        InitializeComponent();
        Log = log;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Groups = [
            new("常用", [new("记事本", Path.Combine(windows, "System32", "notepad.exe"), LaunchKind.App),
                new("用户目录", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), LaunchKind.Folder),
                new("GitHub", "https://github.com", LaunchKind.Url),
                new("计算器", Path.Combine(windows, "System32", "calc.exe"), LaunchKind.App)]),
            new("工作", [new("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), LaunchKind.Folder),
                new("Microsoft Learn", "https://learn.microsoft.com/dotnet/", LaunchKind.Url)])
        ];
        SelectedGroup = Groups[0];
        DragSession = new(this);
        foreach (var group in Groups)
        {
            var tab = new Button { Content = group.Name, Tag = group, MinWidth = 70 };
            tab.Click += (_, _) => { if (!DragSession.Active) SelectGroup(group); };
            Tabs.Children.Add(tab);
            var grid = new ItemsControl
            {
                ItemsSource = group.Items, ItemTemplate = (DataTemplate)FindResource("TileTemplate"),
                ItemsPanel = (ItemsPanelTemplate)FindResource("TilePanel"), Visibility = Visibility.Collapsed,
                Focusable = true
            };
            KeyboardNavigation.SetDirectionalNavigation(grid, KeyboardNavigationMode.Contained);
            _grids.Add(group, grid);
            GroupHost.Children.Add(grid);
        }
        SelectGroup(SelectedGroup);
        PreviewKeyDown += KeyPressed;
        Deactivated += (_, _) => MaybeHide();
        PreviewDragEnter += ExternalOver;
        PreviewDragOver += ExternalOver;
        PreviewDragLeave += ExternalLeave;
        PreviewDrop += ExternalDrop;
        _dropWait.Tick += (_, _) =>
        {
            if (!_externalDrag && (DateTime.UtcNow >= _dropDeadline || (NativeMethods.GetAsyncKeyState(1) & 0x8000) == 0))
            {
                _awaitingDrop = false;
                _dropWait.Stop();
                MaybeHide();
            }
        };
        SetStatus("拖入项目可测试启动；添加和排序仅保留在本次 M0 会话。");
    }

    public void InitializeNative()
    {
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        int rounded = 2;
        int result = NativeMethods.DwmSetWindowAttribute(hwnd, 33, in rounded, sizeof(int));
        HwndSource.FromHwnd(hwnd)?.AddHook(DpiMessage);
        Log.Write("window.initialized", new { cornerHresult = result, dpi = NativeMethods.GetDpiForWindow(hwnd) });
    }

    private nint DpiMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // WPF handles the native suggested rectangle; log the actual post-change DPI.
        if (message == NativeMethods.WmDpiChanged)
            Dispatcher.BeginInvoke(() => Log.Write("window.dpi", new { dpi = NativeMethods.GetDpiForWindow(hwnd), Left, Top, ActualWidth, ActualHeight }));
        return 0;
    }

    public void Toggle()
    {
        if (DragSession.Active || _menuOpen) return;
        if (IsVisible && IsActive) HidePanel();
        else ShowPanel("hotkey", (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0);
    }

    public void ShowPanel(string source, bool forDrop = false)
    {
        var watch = Stopwatch.StartNew();
        CancelMeasurement();
        int generation = ++_showGeneration;
        _awaitingDrop = forDrop;
        if (forDrop) { _dropDeadline = DateTime.UtcNow.AddSeconds(2); _dropWait.Start(); }
        ShowActivated = !forDrop;
        WindowPlacement.AtCursor(this);
        Show();
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (!forDrop)
        {
            Activate();
            NativeMethods.SetForegroundWindow(hwnd);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != _showGeneration || !IsVisible || forDrop || !IsActive) return;
            FocusFirst();
        });
        _renderHandler = (_, _) =>
        {
            if (generation != _showGeneration) { CancelMeasurement(); return; }
            bool foreground = NativeMethods.GetForegroundWindow() == hwnd;
            if (!forDrop && (!foreground || !IsKeyboardFocusWithin)) return;
            Log.Write("show.ui-proxy", new { source, milliseconds = watch.Elapsed.TotalMilliseconds,
                foreground, focus = IsKeyboardFocusWithin, forDrop, physicalPresentationMeasured = false });
            CancelMeasurement();
        };
        CompositionTarget.Rendering += _renderHandler;
        _measureTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _measureTimeout.Tick += (_, _) =>
        {
            Log.Write("show.timeout", new { source, foreground = NativeMethods.GetForegroundWindow() == hwnd, focus = IsKeyboardFocusWithin });
            SetStatus("激活未确认，可点击面板继续；详情已写入诊断日志。");
            CancelMeasurement();
        };
        _measureTimeout.Start();
    }

    private void CancelMeasurement()
    {
        if (_renderHandler is not null) CompositionTarget.Rendering -= _renderHandler;
        _renderHandler = null;
        _measureTimeout?.Stop();
        _measureTimeout = null;
    }

    public void HidePanel()
    {
        ++_showGeneration;
        CancelMeasurement();
        _dropWait.Stop();
        _awaitingDrop = _externalDrag = false;
        DragSession.Cancel();
        Hide();
    }

    private void MaybeHide()
    {
        if (!_closing && IsVisible && !IsActive && !Pinned && !DragSession.Active && !_externalDrag && !_awaitingDrop && !_menuOpen)
            HidePanel();
    }

    public void SelectGroup(LaunchGroup group)
    {
        SelectedGroup = group;
        foreach (var pair in _grids) pair.Value.Visibility = pair.Key == group ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button tab in Tabs.Children) tab.Foreground = (LaunchGroup)tab.Tag == group
            ? (Brush)FindResource("AccentBrush") : Brushes.LightGray;
        Viewer.ScrollToTop();
    }

    private void FocusFirst()
    {
        var grid = _grids[SelectedGroup];
        grid.UpdateLayout();
        var first = Descendants<Button>(grid).FirstOrDefault();
        if (first is not null) first.Focus(); else grid.Focus();
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DragSession.Active) DragSession.Cancel(); else HidePanel();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control && !DragSession.Active)
        {
            SelectGroup(Groups[(Groups.ToList().IndexOf(SelectedGroup) + 1) % Groups.Count]);
            FocusFirst();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is Button { DataContext: LaunchItem item } && !DragSession.Active)
        { _ = LaunchAsync(item); e.Handled = true; }
    }

    public void SetStatus(string text) => Status.Text = text;
    public async Task LaunchAsync(LaunchItem item, bool elevate = false)
    {
        var watch = Stopwatch.StartNew();
        HidePanel();
        Log.Write("launch.hide-call", new { milliseconds = watch.Elapsed.TotalMilliseconds, item.Kind });
        var result = await LaunchService.StartAsync(item, elevate);
        if (_closing) return;
        SetStatus($"{item.Name}：{result.Message}");
        Log.Write("launch.result", new { item.Kind, result.Accepted, result.Cancelled, result.Message });
    }

    private void ItemClick(object sender, RoutedEventArgs e)
    { if (!DragSession.Active && sender is Button { DataContext: LaunchItem item }) _ = LaunchAsync(item); }
    private void HideClick(object sender, RoutedEventArgs e) => HidePanel();
    private void ExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    private void ItemMenuOpened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        if (sender is ContextMenu menu && menu.PlacementTarget is Button { DataContext: LaunchItem item })
            ((MenuItem)menu.Items[1]).IsEnabled = item.Kind == LaunchKind.App;
    }
    private void ItemMenuClosed(object sender, RoutedEventArgs e) { _menuOpen = false; MaybeHide(); }
    private void OpenMenuClick(object sender, RoutedEventArgs e) => LaunchFromMenu(sender, false);
    private void AdminMenuClick(object sender, RoutedEventArgs e) => LaunchFromMenu(sender, true);
    private void LaunchFromMenu(object sender, bool admin)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: Button { DataContext: LaunchItem item } } })
            _ = LaunchAsync(item, admin);
    }

    private static DragDropEffects Allowed(DragEventArgs e) => e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    private void ExternalOver(object sender, DragEventArgs e)
    {
        _externalDrag = true;
        _awaitingDrop = false;
        _dropWait.Stop();
        var point = e.GetPosition(Root);
        bool target = GroupAt(point) is not null || SlotAt(point) is not null;
        e.Effects = !DragSession.Active && !Importing && target && DropParser.Supports(e.Data) ? Allowed(e) : DragDropEffects.None;
        ShowInsertion(e.Effects != DragDropEffects.None ? SlotAt(point) : null);
        e.Handled = true;
    }
    private void ExternalLeave(object sender, DragEventArgs e)
    {
        Point point = e.GetPosition(Root);
        if (new Rect(Root.RenderSize).Contains(point)) return;
        _externalDrag = false;
        ShowInsertion(null);
        Dispatcher.BeginInvoke(() => MaybeHide());
    }
    private async void ExternalDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (Importing || DragSession.Active) { _externalDrag = false; return; }
        var point = e.GetPosition(Root);
        var tab = GroupAt(point);
        var target = tab ?? SelectedGroup;
        int? slot = tab is not null ? tab.Items.Count : SlotAt(point);
        try
        {
            if (slot is null || Allowed(e) == DragDropEffects.None || !DropParser.Supports(e.Data)) return;
            var watch = Stopwatch.StartNew();
            var snapshot = DropParser.Capture(e.Data);
            Log.Write("drop.capture", new { snapshot.Formats, paths = snapshot.Paths.Length, milliseconds = watch.Elapsed.TotalMilliseconds });
            e.Effects = Allowed(e);
            Importing = true;
            SetStatus("正在解析拖入项目…");
            _externalDrag = false;
            ShowInsertion(null);
            var resolution = await DropParser.ResolveAsync(snapshot, _shutdown.Token);
            if (_closing) return;
            int insert = Math.Min(slot.Value, target.Items.Count);
            foreach (var item in resolution.Items) target.Items.Insert(insert++, item);
            SetStatus($"已添加 {resolution.Items.Count} 项。{string.Join("；", resolution.Errors)}");
            Log.Write("drop.resolved", new { count = resolution.Items.Count, errors = resolution.Errors.Count });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { SetStatus($"拖入失败：{ex.Message}"); Log.Write("drop.failed", new { ex.Message }); }
        finally { Importing = false; _externalDrag = false; ShowInsertion(null); MaybeHide(); }
    }

    public LaunchGroup? GroupAt(Point point)
    {
        foreach (Button tab in Tabs.Children)
            if (new Rect(tab.TranslatePoint(new Point(), Root), tab.RenderSize).Contains(point)) return (LaunchGroup)tab.Tag;
        return null;
    }
    public LaunchItem? ItemAt(Point point) => Root.InputHitTest(point) is DependencyObject hit
        ? Ancestor<Button>(hit)?.DataContext as LaunchItem : null;
    public int? SlotAt(Point point)
    {
        Point p = Root.TranslatePoint(point, Viewer);
        if (p.X < 0 || p.Y < 0 || p.X >= Viewer.ViewportWidth || p.Y >= Viewer.ViewportHeight) return null;
        int columns = Math.Max(1, (int)(Viewer.ViewportWidth / 88));
        return ReorderMath.Slot(p.X, p.Y + Viewer.VerticalOffset, columns, SelectedGroup.Items.Count);
    }
    public void ShowInsertion(int? slot)
    {
        Insertion.Visibility = slot.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (!slot.HasValue) return;
        int columns = Math.Max(1, (int)(Viewer.ViewportWidth / 88));
        Insertion.RenderTransform = new TranslateTransform(slot.Value % columns * 88, slot.Value / columns * 88 - Viewer.VerticalOffset + 4);
    }
    public void ShowGhost(string name, Point point)
    {
        GhostText.Text = name;
        Ghost.Visibility = Visibility.Visible;
        Canvas.SetLeft(Ghost, point.X + 14);
        Canvas.SetTop(Ghost, point.Y + 12);
    }
    public void HideGhost() => Ghost.Visibility = Visibility.Collapsed;
    public static T? Ancestor<T>(DependencyObject? value) where T : DependencyObject
    {
        while (value is not null)
        {
            if (value is T found) return found;
            value = value is Visual || value is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
        }
        return null;
    }
    public static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    public void PrepareExit()
    {
        _closing = true;
        _shutdown.Cancel();
        DragSession.Cancel();
        CancelMeasurement();
        _dropWait.Stop();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing) { e.Cancel = true; HidePanel(); }
        base.OnClosing(e);
    }
}
