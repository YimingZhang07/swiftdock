using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows;
using Microsoft.Extensions.Logging;
using QuickLaunch.Diagnostics;
using QuickLaunch.Interop;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;

namespace QuickLaunch.Views;

public partial class LauncherWindow : Window, IDisposable
{
    private readonly Dictionary<LaunchGroup, TileListBox> _grids = [];
    private CancellationTokenSource _shutdown = new();
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _toastTimer = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _toastCollapse = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
    private ThemeService? _theme;
    private int _cornerHresult;
    public string BackdropMode { get; private set; } = "solid";
    private bool _closing, _movingWindow;
    private bool _sizeMove, _sizing;
    private Size? _sizeBeforeMove;
    private Point? _positionBeforeMove;
    private int _ownedWindows;
    private int _showGeneration;
    private EventHandler? _renderHandler;
    private DispatcherTimer? _measureTimeout;
    private TaskCompletionSource<double?>? _showCompletion;
    private TaskCompletionSource<double?>? _renderCompletion;
    private bool _warming;
    private bool _activationLogQueued;
    private CancellationTokenSource? _warmupStop;
    public Task<double?> PendingShow => _showCompletion?.Task ?? Task.FromResult<double?>(null);
    public Task<double?> PendingRender => _renderCompletion?.Task ?? Task.FromResult<double?>(null);
    public ILogger<LauncherWindow> Log { get; }
    public LauncherViewModel Launcher { get; }
    public MainViewModel ViewModel => Launcher.State;
    public IconService Icons { get; }
    public IReadOnlyList<LaunchGroup> Groups => ViewModel.Groups;
    public bool CanEdit => ViewModel.CanEdit;
    public LaunchGroup SelectedGroup => Launcher.SelectedGroup;
    internal TileListBox SelectedList => _grids[SelectedGroup];
    public Action? SettingsRequested { get; set; }

    internal LauncherWindow(ILogger<LauncherWindow> log, LauncherViewModel launcher, IconService icons, IDialogService dialogs)
    {
        InitializeComponent();
        WindowDragArea.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(WindowDragMouseDown), handledEventsToo: true);
        Log = log;
        Launcher = launcher;
        Icons = icons;
        Icons.AutoFetchWebsiteIcons = ViewModel.AutoFetchWebsiteIcons;
        _dialogs = dialogs;
        DataContext = launcher;
        QueryContinueDrag += Launcher.QueryContinueDrag;
        Launcher.GroupSelected += ApplySelectedGroup;
        Launcher.FocusSelectionRequested += FocusSelection;
        Launcher.RevealSelectionRequested += RevealSelection;
        Launcher.HideRequested += HidePanel;
        Launcher.ContentChanged += FitContent;
        Launcher.IconsRequested += ReloadIcons;
        Launcher.DuplicateFound += FlashDuplicate;
        Launcher.ToastChanged += UpdateToast;
        Launcher.PropertyChanged += LauncherChanged;
        RebuildGroups();
        ViewModel.Groups.CollectionChanged += GroupsChanged;
        Loaded += (_, _) => _ = LoadIconsAsync();
        PreviewKeyDown += KeyPressed;
        Viewer.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(IconAreaMouseDown), handledEventsToo: true);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Launcher.ExpireToast(); };
        _toastCollapse.Tick += (_, _) => { _toastCollapse.Stop(); Toast.Visibility = Visibility.Collapsed; };
        if (Launcher.ToastVisible) UpdateToast();
        UpdateMinimumSize();
        ViewModel.PropertyChanged += ViewModelChanged;
        IsVisibleChanged += (_, _) => UpdateNonClientBackdrop();
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModelChanged;
            ViewModel.Groups.CollectionChanged -= GroupsChanged;
            Launcher.GroupSelected -= ApplySelectedGroup;
            Launcher.FocusSelectionRequested -= FocusSelection;
            Launcher.RevealSelectionRequested -= RevealSelection;
            Launcher.HideRequested -= HidePanel;
            QueryContinueDrag -= Launcher.QueryContinueDrag;
            Launcher.ContentChanged -= FitContent;
            Launcher.IconsRequested -= ReloadIcons;
            Launcher.DuplicateFound -= FlashDuplicate;
            Launcher.ToastChanged -= UpdateToast;
            Launcher.PropertyChanged -= LauncherChanged;
            if (_theme is not null) _theme.Changed -= UpdateBackdrop;
            _toastTimer.Stop(); _toastCollapse.Stop();
        };
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.BackdropOpacity)) UpdateBackdropTint();
        if (e.PropertyName == nameof(MainViewModel.KeepBackdropWhenInactive)) UpdateNonClientBackdrop();
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
        var desktop = WindowPlacement.ReadDesktop(new WindowInteropHelper(this).EnsureHandle());
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
        Log.WindowInitialized(_cornerHresult, BackdropMode, NativeMethods.GetDpiForWindow(hwnd));
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
        string requested = ViewModel.Backdrop;
        var requestedType = AppearanceOptions.BackdropType(requested);
        bool supported = AppearanceOptions.IsSupported(requestedType);
        var type = AppearanceOptions.EffectiveBackdrop(requested, supported && _cornerHresult >= 0, transparency, highContrast, _warming);
        // The handle overload also applies WPF-UI's immersive-dark state immediately,
        // including before Loaded during silent startup. None still updates that state.
        bool applied = Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(hwnd, type);
        bool material = type != Wpf.Ui.Controls.WindowBackdropType.None && applied;
        if (material)
        {
            Background = Brushes.Transparent;
        }
        else
        {
            Wpf.Ui.Controls.WindowBackdrop.RemoveBackdrop(hwnd);
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush"); Root.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
        }
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = material ? Colors.Transparent : ((SolidColorBrush)FindResource("ApplicationBackgroundBrush")).Color;
        BackdropMode = material ? requested : "solid";
        if (material) UpdateBackdropTint();
        UpdateNonClientBackdrop();
        if (!_warming) _theme?.ReportBackdrop(requested != "solid" && !material);
    }

    private void UpdateBackdropTint()
    {
        if (BackdropMode is not ("acrylic" or "mica")) return;
        var color = ((SolidColorBrush)FindResource("ApplicationBackgroundBrush")).Color;
        var tint = new SolidColorBrush(AppearanceOptions.OverlayColor(color, ViewModel.BackdropOpacity));
        tint.Freeze(); Root.Background = tint;
    }

    private void UpdateNonClientBackdrop()
    {
        // Apply a changed choice even while Settings owns foreground. This paints the
        // non-client state only; never activate the panel or change its WPF IsActive.
        if (!IsVisible || _warming) return;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        bool active = IsActive || AppearanceOptions.KeepNonClientActive(NativeMethods.WmNcActivate,
            ViewModel.KeepBackdropWhenInactive, BackdropMode, IsVisible, _warming);
        NativeMethods.DefWindowProc(hwnd, NativeMethods.WmNcActivate, active ? 1 : 0, 0);
    }

    internal nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x0006 or NativeMethods.WmNcActivate && !_activationLogQueued)
        {
            _activationLogQueued = true;
            // Observe WPF's actual state after WindowChrome/native activation processing;
            // no focus changes or test-only query messages are involved.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _activationLogQueued = false;
                Log.PanelActivation(IsActive, NativeMethods.GetForegroundWindow().ToInt64(),
                    ViewModel.KeepBackdropWhenInactive, _warming, message);
            });
        }
        if (AppearanceOptions.KeepNonClientActive(message, ViewModel.KeepBackdropWhenInactive, BackdropMode, IsVisible, _warming))
        {
            handled = true;
            nint result = NativeMethods.DefWindowProc(hwnd, message, 1, lParam);
            // A FALSE request must return TRUE to allow real deactivation to finish.
            return wParam == 0 ? 1 : result;
        }
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
        Log.WindowDpi(NativeMethods.GetDpiForWindow(new WindowInteropHelper(this).Handle), Left, Top, ActualWidth, ActualHeight);
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
        switch (ToggleDecision(IsVisible, IsActive, Topmost, _ownedWindows > 0,
            Launcher.DragActive, Launcher.MenuOpen, _movingWindow, _sizeMove))
        {
            case ToggleAction.ActivateOwnedDialog:
                OwnedWindows.Cast<Window>().LastOrDefault(w => w.IsVisible)?.Activate();
                break;
            case ToggleAction.Hide: HidePanel(); break;
            case ToggleAction.Show: ShowPanel("hotkey", (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0); break;
        }
    }

    internal enum ToggleAction { Ignore, ActivateOwnedDialog, Hide, Show }

    internal static ToggleAction ToggleDecision(bool visible, bool active, bool topmost, bool ownedDialog = false,
        bool drag = false, bool menu = false, bool moving = false, bool sizeMove = false)
    {
        if (ownedDialog) return ToggleAction.ActivateOwnedDialog;
        if (drag || menu || moving || sizeMove) return ToggleAction.Ignore;
        return visible && (active || topmost) ? ToggleAction.Hide : ToggleAction.Show;
    }

    public void ShowPanel(string source, bool forDrop = false)
    {
        if (_closing || _sizeMove || _movingWindow) return;
        if (_dialogs.ActivateEditor()) return;
        Launcher.ClearAllSelections();
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
            Log.ShowUiProxy(source, watch.Elapsed.TotalMilliseconds, foreground, IsKeyboardFocusWithin, forDrop, false);
            _showCompletion?.TrySetResult(watch.Elapsed.TotalMilliseconds);
            CancelMeasurement();
        };
        CompositionTarget.Rendering += _renderHandler;
        _measureTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _measureTimeout.Tick += (_, _) =>
        {
            if (generation != _showGeneration) return;
            Log.ShowTimeout(source, NativeMethods.GetForegroundWindow() == hwnd, IsKeyboardFocusWithin);
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
        Launcher.RequestDragCancel();
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
            Log.WarmupCompleted(completed, watch.Elapsed.TotalMilliseconds, interrupted);
        }
    }
    private void RestoreWarmupState()
    {
        _warming = false; _warmupStop?.Cancel(); Opacity = 1; UpdateBackdrop();
    }

    private void WindowDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsWindowDragSource(e.OriginalSource as DependencyObject)) return;
        var decision = WindowDragDecision(e.ClickCount, _closing, _movingWindow, _sizeMove, Launcher.DragActive, Launcher.ExternalDragActive);
        if (!decision.Handled) return;
        e.Handled = true;
        if (!decision.Move) return;
        _movingWindow = true;
        try
        {
            // Native movement also handles monitor and DPI transitions.
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
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

    public void SelectGroup(LaunchGroup group, bool preview = false) => Launcher.SetSelectedGroup(group, preview);

    private void ApplySelectedGroup(bool preview)
    {
        var group = SelectedGroup;
        EnsureGrid(group);
        foreach (var pair in _grids) pair.Value.Visibility = pair.Key == group ? Visibility.Visible : Visibility.Collapsed;
        Descendants<Button>(Tabs).FirstOrDefault(b => b.Tag == group)?.BringIntoView();
        Viewer.ScrollToTop();
        if (!preview) FocusSelectionList();
        if (IsVisible && !Launcher.DragActive) FitContent();
        if (IsLoaded) _ = LoadIconsAsync();
    }

    private void GroupsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RebuildGroups();
    internal void ClearTileSelection() => Launcher.ClearSelectionCommand.Execute(null);
    private void FocusSelection() => FocusSelectionList();
    private void ReloadIcons() => _ = LoadIconsAsync();

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
        bool textInput = e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase;
        if (textInput || HasOpenEditor || Launcher.MenuOpen) return;
        var modifiers = e.KeyboardDevice.Modifiers;
        if (!Launcher.DragActive && TileListBox.IsNavigationKey(e.Key, modifiers)) SelectedList.HandleNavigation(e);
        else e.Handled = Launcher.HandleKey(e.Key, modifiers, e.IsRepeat, textInput);
    }

    private void TabsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabsViewport.ScrollToHorizontalOffset(TabsViewport.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void IconsMouseWheel(object sender, MouseWheelEventArgs e) => HandleIconSizeWheel(e, Keyboard.Modifiers);

    internal void HandleIconSizeWheel(MouseWheelEventArgs e, ModifierKeys modifiers)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control) || !CanEdit || Launcher.DragActive || Launcher.ExternalDragActive || e.Delta == 0) return;
        var list = SelectedList;
        ViewModel.SetIconSize(IconSizePresets.Step(ViewModel.IconSize, e.Delta));
        e.Handled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (list == SelectedList) list.RevealSelection(); });
    }

    public void SetStatus(string text, bool toast = true) => Launcher.SetStatus(text, toast);
    private void UpdateToast()
    {
        ShowToast(Launcher.ToastVisible);
        RestartToastTimer();
    }
    private void RestartToastTimer()
    {
        _toastTimer.Stop();
        if (!Launcher.ToastVisible || Launcher.ToastDuration is not { } duration) return;
        _toastTimer.Interval = duration; _toastTimer.Start();
    }
    private void LauncherChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherViewModel.ToastDuration)) RestartToastTimer();
        if (e.PropertyName == nameof(LauncherViewModel.DragActive) && Launcher.DragActive)
        {
            foreach (var tile in Descendants<TileListBoxItem>(GroupHost)) tile.CancelClick();
            foreach (var tab in Descendants<GroupTabButton>(Tabs)) tab.CancelClick();
        }
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
    private void SettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    public void SetSettingsOpen(bool value)
    {
        _ownedWindows = Math.Max(0, _ownedWindows + (value ? 1 : -1));
        if (value) CancelMeasurement();
    }
    private void ItemMenuOpened(object sender, RoutedEventArgs e) => Launcher.MenuOpen = true;
    private void ItemMenuClosed(object sender, RoutedEventArgs e) => Launcher.MenuOpen = false;

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
        Launcher.PrepareExit();
        _warmupStop?.Cancel();
        _dialogs.CloseEditor();
        IsEnabled = false;
        _shutdown.Cancel();
        Launcher.RequestDragCancel();
        CancelMeasurement();
        _toastTimer.Stop(); _toastCollapse.Stop();
    }
    public void ResumeAfterExitFailure()
    {
        _closing = false;
        Launcher.ResumeAfterExitFailure();
        _shutdown.Dispose();
        _shutdown = new();
        IsEnabled = true;
        ShowPanel("save-failed");
    }
    public void Dispose()
    {
        PrepareExit();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing) { e.Cancel = true; HidePanel(); }
        base.OnClosing(e);
    }
}
