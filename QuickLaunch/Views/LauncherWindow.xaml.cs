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
using QuickLaunch.ViewModels;

namespace QuickLaunch.Views;

public partial class LauncherWindow : Window
{
    private readonly Dictionary<LaunchGroup, ItemsControl> _grids = [];
    private CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _dropWait = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _externalWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Point _externalPoint;
    private bool _externalAccepts;
    private bool _externalDrag, _menuOpen, _closing, _awaitingDrop;
    private int _ownedWindows;
    private CancellationTokenSource? _importStop;
    private DateTime _dropDeadline;
    private int _showGeneration;
    private string? _focusedItemId;
    private EventHandler? _renderHandler;
    private DispatcherTimer? _measureTimeout;
    private TaskCompletionSource<double?>? _showCompletion;
    private TaskCompletionSource<double?>? _renderCompletion;
    private bool _warming, _pinBeforeWarmup;
    private CancellationTokenSource? _warmupStop;
    public Task<double?> PendingShow => _showCompletion?.Task ?? Task.FromResult<double?>(null);
    public Task<double?> PendingRender => _renderCompletion?.Task ?? Task.FromResult<double?>(null);
    public DiagnosticLog Log { get; }
    public Grid CaptureRoot => Root;
    public MainViewModel ViewModel { get; }
    public IconService Icons { get; }
    public IReadOnlyList<LaunchGroup> Groups => ViewModel.Groups;
    public bool CanEdit => ViewModel.CanEdit;
    public LaunchGroup SelectedGroup { get; private set; }
    public DragCoordinator DragSession { get; }
    public bool Importing { get; private set; }
    public bool Pinned { get => KeepVisible.IsChecked == true; set => KeepVisible.IsChecked = value; }
    public Action? ExitRequested { get; set; }
    public Action? SettingsRequested { get; set; }
    public string StatusText => Status.Text;

    public LauncherWindow(DiagnosticLog log, MainViewModel viewModel, IconService icons)
    {
        InitializeComponent();
        Log = log;
        ViewModel = viewModel;
        Icons = icons;
        DataContext = viewModel;
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == viewModel.LastGroupId) ?? Groups[0];
        DragSession = new(this);
        RebuildGroups();
        ViewModel.Groups.CollectionChanged += (_, _) => RebuildGroups();
        Loaded += (_, _) => _ = LoadIconsAsync();
        PreviewKeyDown += KeyPressed;
        PreviewGotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is Button { DataContext: LaunchItem item }) _focusedItemId = item.Id;
        };
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
        _externalWatch.Tick += (_, _) =>
        {
            bool held = ((NativeMethods.GetAsyncKeyState(1) | NativeMethods.GetAsyncKeyState(2)) & 0x8000) != 0;
            if (!_externalDrag || !held || !IsVisible || _closing)
            {
                _externalWatch.Stop(); _externalDrag = false;
                ShowInsertion(null); HighlightDropTab(null); MaybeHide();
            }
            else if (_externalAccepts) { AutoScroll(_externalPoint); ShowInsertion(SlotAt(_externalPoint)); }
        };
        SetStatus(viewModel.IsReadOnly ? "当前为只读配置，可启动项目，不能添加或排序。" : "添加和排序会自动保存；热键可在设置中修改。");
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
            Dispatcher.BeginInvoke(() => { Log.Write("window.dpi", new { dpi = NativeMethods.GetDpiForWindow(hwnd), Left, Top, ActualWidth, ActualHeight }); _ = LoadIconsAsync(); });
        return 0;
    }

    public void Toggle()
    {
        if (_ownedWindows > 0) { OwnedWindows.Cast<Window>().LastOrDefault(w => w.IsVisible)?.Activate(); return; }
        if (DragSession.Active || _menuOpen) return;
        if (IsVisible && IsActive) HidePanel();
        else ShowPanel("hotkey", (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0);
    }

    public void ShowPanel(string source, bool forDrop = false)
    {
        if (_closing) return;
        if (_editor?.IsVisible == true) { _editor.Activate(); return; }
        if (_warming && source != "prewarm") RestoreWarmupState();
        var watch = Stopwatch.StartNew();
        CancelMeasurement();
        _showCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _renderCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation = ++_showGeneration;
        _awaitingDrop = forDrop;
        if (forDrop) { _dropDeadline = DateTime.UtcNow.AddSeconds(2); _dropWait.Start(); }
        ShowActivated = !forDrop;
        if (source != "prewarm") WindowPlacement.AtCursor(this, ViewModel.Columns, CellSize, SelectedGroup.Items.Count);
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
            if (generation != _showGeneration) return;
            _renderCompletion?.TrySetResult(watch.Elapsed.TotalMilliseconds);
            bool foreground = NativeMethods.GetForegroundWindow() == hwnd;
            if (!forDrop && (!foreground || !IsKeyboardFocusWithin)) return;
            Log.Write("show.ui-proxy", new { source, milliseconds = watch.Elapsed.TotalMilliseconds,
                foreground, focus = IsKeyboardFocusWithin, forDrop, physicalPresentationMeasured = false });
            _showCompletion?.TrySetResult(watch.Elapsed.TotalMilliseconds);
            CancelMeasurement();
        };
        CompositionTarget.Rendering += _renderHandler;
        _measureTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _measureTimeout.Tick += (_, _) =>
        {
            if (generation != _showGeneration) return;
            Log.Write("show.timeout", new { source, foreground = NativeMethods.GetForegroundWindow() == hwnd, focus = IsKeyboardFocusWithin });
            SetStatus("激活未确认，可点击面板继续；详情已写入诊断日志。");
            CancelMeasurement();
        };
        _measureTimeout.Start();
    }

    private void CancelMeasurement()
    {
        _showCompletion?.TrySetResult(null);
        _renderCompletion?.TrySetResult(null);
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
        _externalWatch.Stop();
        _awaitingDrop = _externalDrag = false;
        DragSession.Cancel();
        Hide();
    }

    public async Task<bool> PrewarmAsync()
    {
        if (_closing || IsVisible || _warming) return false;
        _warming = true; _pinBeforeWarmup = Pinned; Pinned = true; Opacity = 0;
        var cancellation = new CancellationTokenSource(); _warmupStop = cancellation;
        var watch = Stopwatch.StartNew();
        WindowPlacement.AtCursor(this, ViewModel.Columns, CellSize, SelectedGroup.Items.Count);
        nint handle = new WindowInteropHelper(this).Handle;
        int width = (int)Math.Ceiling(Width * VisualTreeHelper.GetDpi(this).DpiScaleX);
        NativeMethods.SetWindowPos(handle, 0, NativeMethods.GetSystemMetrics(76) - width - 64,
            NativeMethods.GetSystemMetrics(77), 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE, outside virtual desktop
        ShowPanel("prewarm", forDrop: true);
        int generation = _showGeneration;
        bool completed = false;
        try
        {
            if (!NativeMethods.GetWindowRect(handle, out var hiddenBounds) || hiddenBounds.Right > NativeMethods.GetSystemMetrics(76)) return false;
            // Draw opaque content only after verifying the HWND is outside the entire virtual desktop.
            Opacity = 1;
            await PendingRender.WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token);
            await LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation == _showGeneration && _warming && Content is Visual content)
            {
                // Force the actual content subtree to draw, not just a transparent HWND render callback.
                var warmBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)ActualWidth),
                    Math.Max(1, (int)ActualHeight), 96, 96, PixelFormats.Pbgra32);
                warmBitmap.Render(content);
            }
            completed = true;
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return false; }
        finally
        {
            bool interrupted = generation != _showGeneration || !_warming;
            if (!_closing && _warming && generation == _showGeneration) { HidePanel(); RestoreWarmupState(); }
            if (ReferenceEquals(_warmupStop, cancellation)) _warmupStop = null;
            cancellation.Dispose();
            Log.Write("warmup.completed", new { completed, milliseconds = watch.Elapsed.TotalMilliseconds, interrupted });
        }
    }
    private void RestoreWarmupState()
    {
        _warming = false; _warmupStop?.Cancel(); Opacity = 1; Pinned = _pinBeforeWarmup;
    }

    private void MaybeHide()
    {
        if (!_closing && ViewModel.HideOnDeactivate && IsVisible && !IsActive && !Pinned && !DragSession.Active && !_externalDrag && !_awaitingDrop && !_menuOpen && _ownedWindows == 0)
            HidePanel();
    }

    public void SelectGroup(LaunchGroup group, bool preview = false)
    {
        ClearPreview();
        EnsureGrid(group);
        SelectedGroup = group;
        foreach (var pair in _grids) pair.Value.Visibility = pair.Key == group ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button tab in Tabs.Children)
        {
            bool selected = (LaunchGroup)tab.Tag == group;
            tab.SetResourceReference(ForegroundProperty, selected ? "AccentBrush" : "TextBrush");
            tab.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            if (selected) tab.SetResourceReference(BorderBrushProperty, "AccentBrush"); else tab.BorderBrush = Brushes.Transparent;
        }
        Tabs.Children.OfType<Button>().FirstOrDefault(b => b.Tag == group)?.BringIntoView();
        Viewer.ScrollToTop();
        if (!preview) ViewModel.CommitGroup(group.Id);
        if (IsVisible && !DragSession.Active) WindowPlacement.FitContent(this, CellSize, group.Items.Count);
        if (IsLoaded) _ = LoadIconsAsync();
    }

    private void FocusFirst()
    {
        var grid = _grids[SelectedGroup];
        grid.UpdateLayout();
        var buttons = Descendants<Button>(grid).ToList();
        var first = buttons.FirstOrDefault(b => b.DataContext is LaunchItem item && item.Id == _focusedItemId) ?? buttons.FirstOrDefault();
        if (first is not null) { first.Focus(); first.BringIntoView(); } else grid.Focus();
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase || _editor?.IsVisible == true) return;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var item = focused is null ? null : Ancestor<Button>(focused)?.DataContext as LaunchItem;
        if (e.Key == Key.Escape)
        {
            if (_menuOpen) return; // Let the menu consume Esc before the panel's hide shortcut.
            if (DragSession.Active) DragSession.Cancel(); else HidePanel();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !DragSession.Active)
        {
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            SelectGroup(Groups[(Groups.ToList().IndexOf(SelectedGroup) + step + Groups.Count) % Groups.Count]);
            FocusFirst();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D9 && !DragSession.Active)
        {
            int index = e.Key - Key.D1;
            if (index < Groups.Count) { SelectGroup(Groups[index]); FocusFirst(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !DragSession.Active)
        { SetStatus(ViewModel.UndoDelete() ? "已恢复删除的条目。" : "没有可恢复的删除记录，或已超过 5 秒。"); e.Handled = true; }
        else if (e.Key == Key.F2 && item is not null && !DragSession.Active) { RenameItem(item); e.Handled = true; }
        else if (e.Key == Key.Delete && item is not null && !DragSession.Active) { DeleteItem(item); e.Handled = true; }
        else if (e.Key == Key.Enter && item is not null && !DragSession.Active)
        { _ = LaunchAsync(item); e.Handled = true; }
    }

    public void SetStatus(string text) => Status.Text = text;
    public async Task LaunchAsync(LaunchItem item, bool elevate = false)
    {
        if (item.IsPending) return;
        var watch = Stopwatch.StartNew();
        HidePanel();
        Log.Write("launch.hide-call", new { milliseconds = watch.Elapsed.TotalMilliseconds, item.Kind });
        var result = await LaunchService.StartAsync(item, elevate);
        if (_closing) return;
        await DragSession.WhenIdle;
        if (_closing) return;
        ViewModel.RecordLaunch(item, result);
        SetStatus($"{item.Name}：{result.Message}");
        Log.Write("launch.result", new { item.Kind, result.Accepted, result.Cancelled, result.Message });
    }

    private void ItemClick(object sender, RoutedEventArgs e)
    { if (!DragSession.Active && sender is Button { DataContext: LaunchItem item }) _ = LaunchAsync(item); }
    private void HideClick(object sender, RoutedEventArgs e) => HidePanel();
    private void ExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    private void SettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    public void SetSettingsOpen(bool value)
    {
        _ownedWindows = Math.Max(0, _ownedWindows + (value ? 1 : -1));
        if (value) CancelMeasurement();
        if (!value) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => MaybeHide());
    }
    private void ItemMenuOpened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        if (sender is ContextMenu menu && menu.PlacementTarget is Button { DataContext: LaunchItem item })
        {
            ((MenuItem)menu.Items[1]).IsEnabled = item.Kind == LaunchKind.App && !item.IsPending;
            ((MenuItem)menu.Items[2]).IsEnabled = item.Kind != LaunchKind.Url && !item.IsPending;
            foreach (var entry in menu.Items.OfType<MenuItem>().Where(m => m.Tag as string == "edit")) entry.IsEnabled = CanEdit && !item.IsPending && !DragSession.Active;
        }
    }
    private void ItemMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => MaybeHide());
    }
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
        _externalWatch.Start();
        _awaitingDrop = false;
        _dropWait.Stop();
        var point = e.GetPosition(Root);
        _externalPoint = point;
        HighlightDropTab(GroupAt(point));
        bool target = GroupAt(point) is not null || SlotAt(point) is not null;
        e.Effects = CanEdit && !DragSession.Active && !Importing && target && DropParser.Supports(e.Data) ? Allowed(e) : DragDropEffects.None;
        _externalAccepts = e.Effects != DragDropEffects.None;
        ShowInsertion(e.Effects != DragDropEffects.None ? SlotAt(point) : null);
        e.Handled = true;
    }
    private void ExternalLeave(object sender, DragEventArgs e)
    {
        Point point = e.GetPosition(Root);
        if (new Rect(Root.RenderSize).Contains(point)) return;
        _externalDrag = false;
        _externalWatch.Stop(); HighlightDropTab(null);
        ShowInsertion(null);
        Dispatcher.BeginInvoke(() => MaybeHide());
    }
    private async void ExternalDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!CanEdit || Importing || DragSession.Active) { _externalDrag = false; return; }
        var point = e.GetPosition(Root);
        var tab = GroupAt(point);
        var target = tab ?? SelectedGroup;
        int? slot = tab is not null ? tab.Items.Count : SlotAt(point);
        string[] pending = [];
        _importStop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try
        {
            if (slot is null || Allowed(e) == DragDropEffects.None || !DropParser.Supports(e.Data)) return;
            var watch = Stopwatch.StartNew();
            var snapshot = DropParser.Capture(e.Data);
            Log.Write("drop.capture", new { snapshot.Formats, paths = snapshot.Paths.Length, milliseconds = watch.Elapsed.TotalMilliseconds });
            e.Effects = Allowed(e);
            Importing = true;
            CancelImportButton.Visibility = Visibility.Visible;
            pending = ViewModel.BeginImport(target, slot.Value, Math.Max(1, snapshot.Paths.Length));
            SetStatus("正在解析拖入项目…");
            _externalDrag = false;
            _externalWatch.Stop(); HighlightDropTab(null);
            ShowInsertion(null);
            var cancellation = _importStop.Token;
            var resolution = await DropParser.ResolveAsync(snapshot, cancellation).WaitAsync(cancellation);
            await DragSession.WhenIdle.WaitAsync(cancellation);
            if (_closing || cancellation.IsCancellationRequested) return;
            var committed = ViewModel.CompleteImport(target, pending, resolution.Items);
            if (target == SelectedGroup) WindowPlacement.FitContent(this, CellSize, target.Items.Count);
            _ = LoadIconsAsync();
            if (committed.Duplicate is not null && target == SelectedGroup) FlashDuplicate(committed.Duplicate);
            SetStatus($"已添加 {committed.Added} 项。{(committed.Duplicate is null ? "" : "重复项目已跳过。")} {string.Join("；", resolution.Errors)}");
            Log.Write("drop.resolved", new { count = committed.Added, errors = resolution.Errors.Count });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { SetStatus($"拖入失败：{ex.Message}"); Log.Write("drop.failed", new { ex.Message }); }
        finally
        {
            ViewModel.CancelImport(target, pending); _importStop?.Dispose(); _importStop = null;
            Importing = false; CancelImportButton.Visibility = Visibility.Collapsed;
            _externalDrag = false; ShowInsertion(null); MaybeHide();
        }
    }
    private void CancelImportClick(object sender, RoutedEventArgs e) { _importStop?.Cancel(); SetStatus("已取消导入。"); }

    public LaunchGroup? GroupAt(Point point)
    {
        if (!new Rect(TabsViewport.TranslatePoint(new Point(), Root), TabsViewport.RenderSize).Contains(point)) return null;
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
        int columns = Math.Max(1, (int)(Viewer.ViewportWidth / CellSize));
        return ReorderMath.Slot(p.X, p.Y + Viewer.VerticalOffset, columns, SelectedGroup.Items.Count, CellSize);
    }
    public void ShowInsertion(int? slot)
    {
        Insertion.Visibility = slot.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (!slot.HasValue) return;
        int columns = Math.Max(1, (int)(Viewer.ViewportWidth / CellSize));
        Insertion.Height = CellSize - 8;
        Insertion.RenderTransform = new TranslateTransform(slot.Value % columns * CellSize, slot.Value / columns * CellSize - Viewer.VerticalOffset + 4);
    }
    public void ShowGhost(string name, Point point, System.Windows.Media.Imaging.BitmapSource? image = null)
    {
        _ghostGeneration++;
        Ghost.BeginAnimation(Canvas.LeftProperty, null); Ghost.BeginAnimation(Canvas.TopProperty, null);
        GhostImage.Source = image; GhostImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        GhostText.Text = name;
        Ghost.Visibility = Visibility.Visible;
        Canvas.SetLeft(Ghost, point.X + 14);
        Canvas.SetTop(Ghost, point.Y + 12);
    }
    public void HideGhost()
    {
        _ghostGeneration++; Ghost.BeginAnimation(Canvas.LeftProperty, null); Ghost.BeginAnimation(Canvas.TopProperty, null);
        Ghost.Visibility = Visibility.Collapsed;
    }
    public async Task LoadIconsAsync()
    {
        if (_closing) return;
        int pixels = IconService.PixelSize(ViewModel.IconSize, VisualTreeHelper.GetDpi(this).DpiScaleX);
        var cancellation = _shutdown.Token;
        var items = SelectedGroup.Items.Concat(Groups.Where(g => g != SelectedGroup).SelectMany(g => g.Items)).Where(i => !i.IsPending).ToArray();
        await Task.WhenAll(items.Select(async item =>
        {
            try
            {
                if (item.Visual.Image is not null && item.Visual.RequestedPixels == pixels) return;
                int generation = item.Visual.BeginRequest(pixels);
                var image = await Icons.GetAsync(item, pixels, cancellation);
                if (_closing || cancellation.IsCancellationRequested) return;
                item.Visual.Complete(generation, image);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }));
    }
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
        _warmupStop?.Cancel();
        _editor?.Close();
        IsEnabled = false;
        _shutdown.Cancel();
        DragSession.Cancel();
        CancelMeasurement();
        _dropWait.Stop();
        _externalWatch.Stop();
    }
    public void ResumeAfterExitFailure()
    {
        _closing = false;
        _shutdown.Dispose();
        _shutdown = new();
        IsEnabled = true;
        ShowPanel("save-failed");
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing) { e.Cancel = true; HidePanel(); }
        base.OnClosing(e);
    }
}
