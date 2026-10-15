using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickLaunch.DragDrop;
using QuickLaunch.Interop;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;

namespace QuickLaunch.Views;

public partial class LauncherWindow : Window
{
    private readonly Dictionary<LaunchGroup, TileListBox> _grids = [];
    private CancellationTokenSource _shutdown = new();
    private readonly Func<LaunchItem, bool, Task<LaunchResult>> _startLaunch;
    private readonly DispatcherTimer _externalWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _toastTimer = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _toastCollapse = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
    private ThemeService? _theme;
    private int _cornerHresult;
    public string BackdropMode { get; private set; } = "solid";
    private Point _externalPoint;
    private bool _externalAccepts;
    private bool _externalDrag, _menuOpen, _closing, _movingWindow;
    private bool _sizeMove, _sizing;
    private Size? _sizeBeforeMove;
    private Point? _positionBeforeMove;
    private int _ownedWindows;
    private CancellationTokenSource? _importStop;
    private int _showGeneration;
    private EventHandler? _renderHandler;
    private DispatcherTimer? _measureTimeout;
    private TaskCompletionSource<double?>? _showCompletion;
    private TaskCompletionSource<double?>? _renderCompletion;
    private bool _warming;
    private CancellationTokenSource? _warmupStop;
    public Task<double?> PendingShow => _showCompletion?.Task ?? Task.FromResult<double?>(null);
    public Task<double?> PendingRender => _renderCompletion?.Task ?? Task.FromResult<double?>(null);
    public DiagnosticLog Log { get; }
    public Grid CaptureRoot => Root;
    public MainViewModel ViewModel { get; }
    public IconService Icons { get; }
    public IReadOnlyList<LaunchGroup> Groups => ViewModel.Groups;
    public bool CanEdit => ViewModel.CanEdit;
    public static readonly DependencyProperty SelectedGroupProperty = DependencyProperty.Register(
        nameof(SelectedGroup), typeof(LaunchGroup), typeof(LauncherWindow));
    public LaunchGroup SelectedGroup { get => (LaunchGroup)GetValue(SelectedGroupProperty); private set => SetValue(SelectedGroupProperty, value); }
    internal TileListBox SelectedList => _grids[SelectedGroup];
    public DragCoordinator DragSession { get; }
    public bool Importing { get; private set; }
    public Action? SettingsRequested { get; set; }
    public string StatusText { get; private set; } = "";
    internal Action<MouseButtonEventArgs> StartWindowMove { get; set; }
    internal Func<nint, WindowPlacement.Desktop> GetPlacementDesktop { get; set; } = WindowPlacement.ReadDesktop;

    public LauncherWindow(DiagnosticLog log, MainViewModel viewModel, IconService icons)
        : this(log, viewModel, icons, LaunchService.StartAsync) { }

    internal LauncherWindow(DiagnosticLog log, MainViewModel viewModel, IconService icons,
        Func<LaunchItem, bool, Task<LaunchResult>> startLaunch)
    {
        InitializeComponent();
        StartWindowMove = e => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        WindowDragArea.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(WindowDragMouseDown), handledEventsToo: true);
        Log = log;
        ViewModel = viewModel;
        Icons = icons;
        Icons.AutoFetchWebsiteIcons = viewModel.AutoFetchWebsiteIcons;
        _startLaunch = startLaunch;
        DataContext = viewModel;
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == viewModel.LastGroupId) ?? Groups[0];
        DragSession = new(this);
        RebuildGroups();
        ViewModel.Groups.CollectionChanged += (_, _) => RebuildGroups();
        Loaded += (_, _) => _ = LoadIconsAsync();
        PreviewKeyDown += KeyPressed;
        Viewer.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(IconAreaMouseDown), handledEventsToo: true);
        PreviewDragEnter += ExternalOver;
        PreviewDragOver += ExternalOver;
        PreviewDragLeave += ExternalLeave;
        PreviewDrop += ExternalDrop;
        _externalWatch.Tick += (_, _) =>
        {
            bool held = ((NativeMethods.GetAsyncKeyState(1) | NativeMethods.GetAsyncKeyState(2)) & 0x8000) != 0;
            if (!_externalDrag || !held || !IsVisible || _closing)
            {
                _externalWatch.Stop(); _externalDrag = false;
                ShowInsertion(null); HighlightDropTab(null);
            }
            else if (_externalAccepts) { AutoScroll(_externalPoint); ShowInsertion(SlotAt(_externalPoint)); }
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); if (!Importing) ShowToast(false); };
        _toastCollapse.Tick += (_, _) => { _toastCollapse.Stop(); Toast.Visibility = Visibility.Collapsed; };
        if (viewModel.IsReadOnly) SetStatus("当前为只读配置，可启动项目，不能添加或排序。");
        UpdateMinimumSize();
        ViewModel.PropertyChanged += ViewModelChanged;
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModelChanged;
            if (_theme is not null) _theme.Changed -= UpdateBackdrop;
            _toastTimer.Stop(); _toastCollapse.Stop();
        };
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AutoFetchWebsiteIcons))
        {
            Icons.AutoFetchWebsiteIcons = ViewModel.AutoFetchWebsiteIcons;
            foreach (var item in Groups.SelectMany(g => g.Items).Where(IconService.UsesFavicon))
            {
                item.Visual.BeginRequest(0); item.Visual.Image = null;
            }
            _ = LoadIconsAsync();
        }
        if (e.PropertyName == nameof(MainViewModel.Placement) && IsVisible && !_warming && !_sizeMove) PlacePanel();
        if (e.PropertyName == nameof(MainViewModel.IconSize) && IsLoaded) _ = LoadIconsAsync();
        if (e.PropertyName == nameof(MainViewModel.CellSize))
        {
            UpdateMinimumSize();
            if (IsVisible && !_sizeMove && ViewModel.WindowSize is null)
                WindowPlacement.ResizeInPlace(this, CellSize, SelectedGroup.Items.Count, ViewModel.Columns);
        }
    }

    private void UpdateMinimumSize()
    {
        var size = WindowPlacement.MinimumSize(CellSize);
        MinWidth = Math.Min(size.Width, ViewModel.WindowSize?.Width ?? size.Width);
        MinHeight = Math.Min(size.Height, ViewModel.WindowSize?.Height ?? size.Height);
    }

    public void ResetWindowSize()
    {
        if (!CanEdit) return;
        ViewModel.SetWindowSize(null);
        ViewModel.SetWindowPosition(null);
        UpdateMinimumSize();
        if (IsVisible && !_sizeMove) PlacePanel(defaultOnCurrentMonitor: true);
    }

    private void PlacePanel(bool defaultOnCurrentMonitor = false, bool followCursor = false)
    {
        var desktop = GetPlacementDesktop(new WindowInteropHelper(this).EnsureHandle());
        WindowPlacement.PlacePanel(this, desktop, defaultOnCurrentMonitor || followCursor ? "cursor" : ViewModel.Placement,
            defaultOnCurrentMonitor || followCursor ? null : ViewModel.WindowPosition, ViewModel.Columns, CellSize,
            SelectedGroup.Items.Count, ViewModel.WindowSize, defaultOnCurrentMonitor);
    }

    public void InitializeNative(ThemeService? theme = null)
    {
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        int rounded = 2;
        _cornerHresult = NativeMethods.DwmSetWindowAttribute(hwnd, 33, in rounded, sizeof(int));
        _theme = theme;
        if (_theme is not null) _theme.Changed += UpdateBackdrop;
        UpdateBackdrop();
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowMessage);
        Log.Write("window.initialized", new { cornerHresult = _cornerHresult, backdrop = BackdropMode, dpi = NativeMethods.GetDpiForWindow(hwnd) });
    }

    private void UpdateBackdrop()
    {
        var preferences = _theme is null ? ThemeService.ReadPreferences() : (_theme.IsDark, _theme.HighContrast, _theme.TransparencyEnabled);
        ApplyBackdrop(preferences.Item2, preferences.Item3);
    }

    internal void ApplyBackdrop(bool highContrast, bool transparency)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        bool supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            && Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Acrylic);
        bool acrylic = CanUseAcrylic(supported, transparency, highContrast, _warming) && _cornerHresult >= 0;
        // The handle overload also applies WPF-UI's immersive-dark state immediately,
        // including before Loaded during silent startup. None still updates that state.
        bool applied = Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(hwnd,
            acrylic ? Wpf.Ui.Controls.WindowBackdropType.Acrylic : Wpf.Ui.Controls.WindowBackdropType.None);
        acrylic &= applied;
        if (acrylic)
        {
            Background = Brushes.Transparent; Root.Background = Brushes.Transparent;
        }
        else
        {
            Wpf.Ui.Controls.WindowBackdrop.RemoveBackdrop(hwnd);
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush"); Root.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
        }
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = acrylic ? Colors.Transparent : ((SolidColorBrush)FindResource("ApplicationBackgroundBrush")).Color;
        BackdropMode = acrylic ? "acrylic" : "solid";
    }

    internal static bool CanUseAcrylic(bool supported, bool transparency, bool highContrast, bool warming = false) =>
        supported && transparency && !highContrast && !warming;

    internal nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Equivalent to SystemThemeWatcher, including transparency/high-contrast setting changes.
        // Keep the hook active even before a silent launcher's first Loaded event.
        if (message is 0x001A or 0x0015 or 0x031A or 0x0320) _theme?.QueueUpdate();
        // Suppress native cues and the captionless system-menu loop entered by Alt release.
        if (message is NativeMethods.WmChangeUiState or NativeMethods.WmUpdateUiState
            || message == NativeMethods.WmSysCommand && (wParam.ToInt64() & 0xFFF0) == NativeMethods.ScKeyMenu)
        { handled = true; return 0; }
        if (message == NativeMethods.WmEnterSizeMove)
            BeginSizeMove();
        else if (message == NativeMethods.WmSizing) MarkSizing();
        else if (message == NativeMethods.WmExitSizeMove) CompleteSizeMove();
        // WPF handles the native suggested rectangle; log the actual post-change DPI.
        if (message == NativeMethods.WmDpiChanged)
            Dispatcher.BeginInvoke(RefreshDpi);
        return 0;
    }

    internal void RefreshDpi()
    {
        WindowPlacement.UpdateWorkAreaLimits(this);
        Log.Write("window.dpi", new { dpi = NativeMethods.GetDpiForWindow(new WindowInteropHelper(this).Handle), Left, Top, ActualWidth, ActualHeight });
        _ = LoadIconsAsync();
    }

    internal void BeginSizeMove()
    {
        _sizeMove = true; _sizing = false;
        WindowPlacement.UpdateWorkAreaLimits(this);
        nint hwnd = new WindowInteropHelper(this).Handle;
        _sizeBeforeMove = NativeSize(hwnd);
        _positionBeforeMove = NativeMethods.GetWindowRect(hwnd, out var rect) ? new(rect.Left, rect.Top) : null;
    }

    internal void MarkSizing() { if (_sizeMove) _sizing = true; }

    internal void CompleteSizeMove()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (_sizeMove && !_closing && !_warming)
        {
            // WM_SIZING distinguishes a resize from a move that happens to change DPI.
            var size = NativeSize(hwnd);
            bool resized = _sizing && _sizeBeforeMove is { } before && size is { } after && after != before;
            if (resized) ViewModel.SetWindowSize(size);
            if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                var position = new Point(rect.Left, rect.Top);
                if (resized || _positionBeforeMove is { } previous && previous != position) ViewModel.SetWindowPosition(position);
            }
        }
        _sizeMove = false; _sizing = false;
        WindowPlacement.UpdateWorkAreaLimits(this);
    }

    private static Size? NativeSize(nint hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return null;
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(hwnd)) / 96.0;
        return new Size((rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Maximized) { _sizing = false; WindowState = WindowState.Normal; }
        base.OnStateChanged(e);
    }

    public void FitContent()
    {
        if (!_sizeMove) WindowPlacement.FitContent(this, CellSize, SelectedGroup.Items.Count, ViewModel.WindowSize);
    }

    public void Toggle()
    {
        if (_ownedWindows > 0) { OwnedWindows.Cast<Window>().LastOrDefault(w => w.IsVisible)?.Activate(); return; }
        if (DragSession.Active || _menuOpen || _movingWindow || _sizeMove) return;
        if (IsVisible && IsActive) HidePanel();
        else ShowPanel("hotkey", (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0);
    }

    public void ShowPanel(string source, bool forDrop = false)
    {
        if (_closing || _sizeMove || _movingWindow) return;
        if (_editor?.IsVisible == true) { _editor.Activate(); return; }
        foreach (var list in _grids.Values) list.SelectedItem = null;
        if (_warming && source != "prewarm") RestoreWarmupState();
        var watch = Stopwatch.StartNew();
        CancelMeasurement();
        _showCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _renderCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation = ++_showGeneration;
        ShowActivated = !forDrop;
        if (source != "prewarm") PlacePanel();
        Show();
        FocusSelectionList(acquireKeyboard: !forDrop);
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (!forDrop)
        {
            Activate();
            NativeMethods.SetForegroundWindow(hwnd);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != _showGeneration || !IsVisible) return;
            _ = LoadIconsAsync(); // Revisit expired favicon entries lazily after the window is shown.
            if (forDrop) return;
            FocusSelectionList();
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
        if (_sizeMove || _movingWindow) return;
        ++_showGeneration;
        CancelMeasurement();
        _externalWatch.Stop();
        _externalDrag = false;
        DragSession.Cancel();
        Hide();
    }

    public async Task<bool> PrewarmAsync()
    {
        if (_closing || IsVisible || _warming) return false;
        _warming = true; UpdateBackdrop(); Opacity = 0;
        var cancellation = new CancellationTokenSource(); _warmupStop = cancellation;
        var watch = Stopwatch.StartNew();
        PlacePanel(followCursor: true);
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
        _warming = false; _warmupStop?.Cancel(); Opacity = 1; UpdateBackdrop();
    }

    private void WindowDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsWindowDragSource(e.OriginalSource as DependencyObject)) return;
        var decision = WindowDragDecision(e.ClickCount, _closing, _movingWindow, _sizeMove, DragSession.Active, _externalDrag);
        if (!decision.Handled) return;
        e.Handled = true;
        if (!decision.Move) return;
        _movingWindow = true;
        try
        {
            // Native movement also handles monitor and DPI transitions.
            StartWindowMove(e);
        }
        finally
        {
            _movingWindow = false;
        }
    }

    internal static (bool Handled, bool Move) WindowDragDecision(int clickCount, bool closing, bool movingWindow,
        bool sizeMove, bool dragActive, bool externalDrag)
    {
        bool handled = !(closing || movingWindow || sizeMove || dragActive || externalDrag);
        return (handled, handled && clickCount <= 1);
    }

    internal bool IsWindowDragSource(DependencyObject? source) => source is Visual
        && (ReferenceEquals(source, WindowDragArea) || WindowDragArea.IsAncestorOf(source))
        && Ancestor<System.Windows.Controls.Primitives.ButtonBase>(source) is null;

    public void SelectGroup(LaunchGroup group, bool preview = false)
    {
        ClearPreview();
        EnsureGrid(group);
        SelectedGroup = group;
        foreach (var pair in _grids) pair.Value.Visibility = pair.Key == group ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button tab in Tabs.Children)
            TabState.SetIsSelected(tab, tab.Tag == group);
        Tabs.Children.OfType<Button>().FirstOrDefault(b => b.Tag == group)?.BringIntoView();
        Viewer.ScrollToTop();
        foreach (var list in _grids.Values) list.SelectedItem = null;
        if (!preview) FocusSelectionList();
        if (!preview) ViewModel.CommitGroup(group.Id);
        if (IsVisible && !DragSession.Active) FitContent();
        if (IsLoaded) _ = LoadIconsAsync();
    }

    internal void ClearTileSelection()
    {
        SelectedList.SelectedItem = null;
        FocusSelectionList();
    }

    private void FocusSelectionList(bool acquireKeyboard = true)
    {
        FocusManager.SetFocusedElement(this, SelectedList);
        if (acquireKeyboard && IsVisible) SelectedList.Focus();
    }

    private void IconAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (Ancestor<ListBoxItem>(source) is null && Ancestor<System.Windows.Controls.Primitives.ScrollBar>(source) is null)
            ClearTileSelection();
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase || _editor?.IsVisible == true) return;
        var item = SelectedList.SelectedItem as LaunchItem;
        var modifiers = e.KeyboardDevice.Modifiers;
        if (!DragSession.Active && TileListBox.IsNavigationKey(e.Key, modifiers))
            SelectedList.HandleNavigation(e);
        else if (e.Key == Key.Escape)
        {
            if (_menuOpen) return; // Let the menu consume Esc before the panel's hide shortcut.
            if (DragSession.Active) DragSession.Cancel(); else HidePanel();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control) && !DragSession.Active)
        {
            int step = modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            SelectGroup(Groups[(Groups.ToList().IndexOf(SelectedGroup) + step + Groups.Count) % Groups.Count]);
            SelectedList.SelectFirst();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D9 && !DragSession.Active)
        {
            int index = e.Key - Key.D1;
            if (index < Groups.Count) { SelectGroup(Groups[index]); SelectedList.SelectFirst(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Z && modifiers == ModifierKeys.Control && !DragSession.Active)
        { SetStatus(ViewModel.UndoDelete() ? "已恢复删除的条目。" : "没有可恢复的删除记录，或已超过 5 秒。"); e.Handled = true; }
        else if (e.Key == Key.F2 && !DragSession.Active)
        { if (item is not null) RenameItem(item); e.Handled = true; }
        else if (e.Key == Key.Delete && !DragSession.Active)
        { if (item is not null) DeleteItem(item); e.Handled = true; }
        else if (e.Key == Key.Enter && !DragSession.Active)
        {
            if (item is not null && !e.IsRepeat) _ = LaunchAsync(item);
            e.Handled = true;
        }
    }

    private void TabsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabsViewport.ScrollToHorizontalOffset(TabsViewport.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void IconsMouseWheel(object sender, MouseWheelEventArgs e) => HandleIconSizeWheel(e, Keyboard.Modifiers);

    internal void HandleIconSizeWheel(MouseWheelEventArgs e, ModifierKeys modifiers)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control) || !CanEdit || DragSession.Active || _externalDrag || e.Delta == 0) return;
        var list = SelectedList;
        ViewModel.SetIconSize(IconSizePresets.Step(ViewModel.IconSize, e.Delta));
        e.Handled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (list == SelectedList) list.RevealSelection(); });
    }

    public void SetStatus(string text, bool toast = true)
    {
        StatusText = text;
        if (!toast) return;
        Status.Text = text;
        ShowToast(true);
        RestartToastTimer();
    }
    private void RestartToastTimer()
    {
        _toastTimer.Stop();
        if (Importing || _closing) return;
        _toastTimer.Interval = TimeSpan.FromSeconds(new[] { "失败", "错误", "无法", "未能", "尚未保存", "不可", "不能" }.Any(Status.Text.Contains) ? 6 : 4);
        _toastTimer.Start();
    }
    internal void SetImporting(bool value)
    {
        Importing = value;
        CancelImportButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        RestartToastTimer();
    }
    private void ShowToast(bool visible)
    {
        _toastCollapse.Stop();
        double from = Toast.Opacity;
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = visible ? 1 : 0;
        if (visible) Toast.Visibility = Visibility.Visible;
        if (!SystemParameters.ClientAreaAnimation) { Toast.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; return; }
        var fade = new DoubleAnimation(from, visible ? 1 : 0, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.Stop };
        Toast.BeginAnimation(OpacityProperty, fade);
        // Animation completion can pause for an occluded HWND; expiry must still collapse the toast.
        if (!visible) _toastCollapse.Start();
    }
    public async Task LaunchAsync(LaunchItem item, bool elevate = false)
    {
        if (item.IsPending) return;
        var result = await _startLaunch(item, elevate);
        if (_closing) return;
        await DragSession.WhenIdle;
        if (_closing) return;
        ViewModel.RecordLaunch(item, result);
        SetStatus($"{item.Name}：{result.Message}", toast: !result.Accepted || result.Cancelled);
        Log.Write("launch.result", new { item.Kind, result.Accepted, result.Cancelled, result.Message });
    }

    private void HideClick(object sender, RoutedEventArgs e) => HidePanel();
    private void SettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    public void SetSettingsOpen(bool value)
    {
        _ownedWindows = Math.Max(0, _ownedWindows + (value ? 1 : -1));
        if (value) CancelMeasurement();
    }
    private void ItemMenuOpened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        if (sender is ContextMenu menu && menu.PlacementTarget is ListBoxItem { DataContext: LaunchItem item })
        {
            ((MenuItem)menu.Items[1]).IsEnabled = item.Kind == LaunchKind.App && !item.IsPending;
            ((MenuItem)menu.Items[2]).IsEnabled = item.Kind != LaunchKind.Url && !item.IsPending;
            foreach (var entry in menu.Items.OfType<MenuItem>().Where(m => m.Tag as string == "edit")) entry.IsEnabled = CanEdit && !item.IsPending && !DragSession.Active;
        }
    }
    private void ItemMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
    }
    private void OpenMenuClick(object sender, RoutedEventArgs e) => LaunchFromMenu(sender, false);
    private void AdminMenuClick(object sender, RoutedEventArgs e) => LaunchFromMenu(sender, true);
    private void LaunchFromMenu(object sender, bool admin)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: ListBoxItem { DataContext: LaunchItem item } } })
            _ = LaunchAsync(item, admin);
    }

    private static DragDropEffects Allowed(DragEventArgs e) => e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    private void ExternalOver(object sender, DragEventArgs e)
    {
        _externalDrag = true;
        _externalWatch.Start();
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
            SetImporting(true);
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
            if (target == SelectedGroup) FitContent();
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
            SetImporting(false);
            _externalDrag = false; ShowInsertion(null);
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
        ? Ancestor<ListBoxItem>(hit)?.DataContext as LaunchItem : null;
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
                if (item.Visual.Image is not null && item.Visual.RequestedPixels == pixels
                    && (!IconService.UsesFavicon(item) || !Icons.AutoFetchWebsiteIcons || item.Visual.WebsiteIconExpires > DateTimeOffset.UtcNow)) return;
                int generation = item.Visual.BeginRequest(pixels);
                if (IconService.UsesFavicon(item))
                {
                    item.Visual.Complete(generation, IconService.Globe(pixels));
                    item.Visual.BeginWebsiteIcon();
                    _ = ApplyFaviconAsync(item, pixels, generation, cancellation);
                    return;
                }
                var image = await Icons.GetAsync(item, pixels, cancellation);
                if (_closing || cancellation.IsCancellationRequested) return;
                item.Visual.Complete(generation, image);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }));
    }
    private async Task ApplyFaviconAsync(LaunchItem item, int pixels, int generation, CancellationToken cancellation)
    {
        try
        {
            var image = await Icons.GetFaviconAsync(item, pixels, cancellation);
            if (!_closing && !cancellation.IsCancellationRequested && Icons.AutoFetchWebsiteIcons)
                item.Visual.CompleteWebsiteIcon(generation, image, Icons.FaviconExpires(item, pixels));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
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
        _externalWatch.Stop();
        _toastTimer.Stop(); _toastCollapse.Stop();
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
