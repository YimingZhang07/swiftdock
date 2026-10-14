using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using QuickLaunch.Diagnostics;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch;

public partial class App : Application
{
    private DiagnosticLog? _log;
    private LauncherWindow? _window;
    private SettingsWindow? _settings;
    private HotkeyService? _hotkey;
    private TaskbarIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private SingleInstanceService? _instance;
    private ConfigService? _store;
    private MainViewModel? _viewModel;
    private IconService? _icons;
    private ThemeService? _theme;
    private StartupService? _startup;
    private volatile bool _exiting;
    private bool _pendingShow, _smoke;
    public int InstanceShowRequests { get; private set; }
    internal TaskbarIcon? Tray => _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool benchmark = e.Args.Contains("--benchmark");
        bool startupProbe = e.Args.Contains("--startup-probe");
        _smoke = e.Args.Contains("--smoke-test") || benchmark || startupProbe;
        try
        {
            string? explicitConfig = Argument(e.Args, "--config-dir");
            if (_smoke && explicitConfig is null) throw new ArgumentException("冒烟检查必须指定独立 --config-dir，禁止修改用户配置。");
            var paths = DataPaths.Resolve(explicitConfig, Argument(e.Args, "--diagnostics-dir"));
            bool readOnly = false;
            if (!DataPaths.CanWrite(paths.ConfigDirectory))
            {
                if (_smoke) readOnly = true;
                else
                {
                    var answer = MessageBox.Show("所选配置目录不可写。\n是：使用默认目录\n否：只读运行\n取消：退出", "QuickLaunch", MessageBoxButton.YesNoCancel);
                    if (answer == MessageBoxResult.Cancel) { Shutdown(); return; }
                    if (answer == MessageBoxResult.Yes) paths = DataPaths.Resolve(DataPaths.DefaultConfigDirectory, Argument(e.Args, "--diagnostics-dir"));
                    else readOnly = true;
                }
            }
            _instance = new(paths.ConfigDirectory, () =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
                {
                    if (_exiting) return;
                    InstanceShowRequests++;
                    if (_window is null) _pendingShow = true;
                    else ShowMain("second-instance");
                });
            }, () => !_exiting && !Dispatcher.HasShutdownStarted);
            if (!_instance.IsPrimary)
            {
                await _instance.NotifyPrimaryAsync();
                if (!_instance.IsPrimary)
                {
                    await _instance.DisposeAsync();
                    _instance = null;
                    Shutdown();
                    return;
                }
            }
            try { _log = new(paths.LogDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickLaunch", "logs"));
                _log.Write("log.fallback", new { ex.Message });
            }
            if (benchmark)
            {
                string file = Path.Combine(paths.ConfigDirectory, "config.json");
                if (File.Exists(file)) throw new InvalidOperationException("性能检查必须使用新的空配置目录。");
                new AtomicConfigWriter().Write(file, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    BenchmarkRunner.CreateConfig(paths.ConfigDirectory, e.Args.Contains("--single-group")), ConfigJsonContext.Default.AppConfig));
            }
            _store = ConfigService.Open(paths.ConfigDirectory, readOnly);
            _viewModel = new(_store);
            _theme = new(Dispatcher);
            _startup = new(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "QuickLaunch.exe"), paths.ConfigDirectory);
            _icons = new(_smoke ? Path.Combine(paths.LogDirectory, "icons") : paths.IconDirectory, _log, _smoke ? new FaviconFixtureHandler() : null);
            _window = new(_log, _viewModel, _icons) { SettingsRequested = OpenSettings };
            MainWindow = _window;
            _window.InitializeNative(_theme);
            _hotkey = new(new WindowInteropHelper(_window).Handle, () =>
            {
                if (_exiting) return;
                if (_settings?.IsVisible == true) _settings.Activate();
                else _window.Toggle();
            }, _viewModel.Hotkey);
            _log.Write("hotkey.registered", new { _hotkey.Registered, _hotkey.Error, hotkey = _viewModel.HotkeyDisplay });
            bool trayAvailable = true;
            try { CreateTray(); }
            catch (InvalidOperationException ex) when (e.Args.Contains("--smoke-test"))
            {
                trayAvailable = false;
                _log.Write("smoke.tray-unavailable", new { ex.Message });
            }
            _ = _window.LoadIconsAsync();
            using var identity = WindowsIdentity.GetCurrent();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            _log.Write("startup", new { runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
                elevated, milestone = $"v{typeof(App).Assembly.GetName().Version?.ToString(3)}", processToReadyMs = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds,
                configDirectory = paths.ConfigDirectory, _store.IsReadOnly, _store.Notice });
            var notices = new List<string>();
            if (_store.Notice is not null) notices.Add(_store.Notice);
            if (elevated) notices.Add("当前以管理员权限运行；请用普通权限重启以接收外部拖入。");
            if (!_hotkey.Registered) notices.Add($"{_viewModel.HotkeyDisplay} 注册失败（{_hotkey.Error}），请从设置修改热键；托盘仍可呼出。");
            if (notices.Count > 0) _window.SetStatus(string.Join("\n", notices));
            SessionEnding += OnSessionEnding;
            if (startupProbe)
            {
                await ExitAsync();
            }
            else if (benchmark)
            {
                int idle = int.TryParse(Argument(e.Args, "--idle-seconds"), out int seconds) ? Math.Clamp(seconds, 0, 300) : 0;
                await BenchmarkRunner.RunAsync(_window, paths.LogDirectory, idle, e.Args.Contains("--benchmark-interactive"), e.Args.Contains("--prewarm"));
                await ExitAsync();
            }
            else if (_smoke)
            {
                bool passed = await PrototypeSmoke.RunAsync(_window, _hotkey, _store, this, paths.LogDirectory, trayAvailable);
                await ExitAsync(passed ? 0 : 1);
            }
            else if (!e.Args.Contains("--silent") || _pendingShow) ShowMain(_pendingShow ? "pending-instance" : "startup");
            else _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => _ = _window.PrewarmAsync());
            _pendingShow = false;
        }
        catch (Exception ex)
        {
            _log?.Write("startup.failed", new { ex.Message, type = ex.GetType().Name });
            if (!_smoke) MessageBox.Show(ex is OperationCanceledException ? "已有实例未及时响应，请稍后重新启动。" : ex.Message, "QuickLaunch 无法启动");
            await ExitAsync(1);
        }
    }

    private static string? Argument(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{key} 缺少参数。");
        return args[index + 1];
    }

    private void ShowMain(string source)
    {
        if (_exiting) return;
        if (_settings?.IsVisible == true) _settings.Activate();
        else _window?.ShowPanel(source);
    }

    public void OpenSettings()
    {
        if (_exiting || _window is null || _viewModel is null || _store is null || _hotkey is null) return;
        if (_window.HasOpenEditor) { _window.ShowPanel("existing-editor"); return; }
        if (_settings is not null) { _settings.Activate(); return; }
        if (!_window.IsVisible) _window.ShowPanel("settings");
        _window.SetSettingsOpen(true);
        _settings = new(_viewModel, _store.DirectoryPath,
            _hotkey.Registered ? $"当前已注册：{_hotkey.Current!.Display}" : $"注册失败，错误码 {_hotkey.Error}；请选择其他组合。",
            candidate =>
            {
                if (!_viewModel.CanEdit) return ("配置只读，未修改。", true);
                if (!_hotkey.TryChange(candidate)) return ($"新组合被占用或不可用（{_hotkey.Error}）；原有热键和配置已保留。", true);
                _viewModel.ApplySettings(candidate);
                UpdateTrayText();
                _log?.Write("hotkey.changed", new { hotkey = candidate.Display });
                return ("已应用，配置将自动保存。", false);
            }, _startup!, _window.ResetWindowSize, () => _ = ExitAsync(), _theme, !_hotkey.Registered) { Owner = _window };
        _settings.Closed += (_, _) => { _settings = null; _window.SetSettingsOpen(false); };
        _settings.Show();
    }

    private void UpdateTrayText()
    {
        if (_tray is not null) _tray.ToolTipText = $"QuickLaunch · {_viewModel!.HotkeyDisplay}";
    }
    private void CreateTray()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "显示 QuickLaunch", Command = new RelayCommand(() => ShowMain("tray-menu")) });
        menu.Items.Add(new MenuItem { Header = "设置", Command = new RelayCommand(OpenSettings) });
        menu.Items.Add(new MenuItem { Header = "打开配置目录", Command = new RelayCommand(() =>
            _ = LaunchService.StartAsync(new("配置目录", _store!.DirectoryPath, LaunchKind.Folder))) });
        var startupItem = new MenuItem { Header = "登录时自动启动", IsCheckable = true, IsEnabled = _viewModel!.CanEdit };
        menu.Opened += (_, _) =>
        {
            try { startupItem.IsChecked = _startup!.IsEnabled; } catch (Exception ex) { _window!.SetStatus(ex.Message); }
        };
        startupItem.Click += (_, _) =>
        {
            try { _startup!.SetEnabled(startupItem.IsChecked); }
            catch (Exception ex) { startupItem.IsChecked = false; _window!.SetStatus($"自启设置失败：{ex.Message}"); ShowMain("startup-error"); }
        };
        menu.Items.Add(startupItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "退出", Command = new RelayCommand(() => _ = ExitAsync()) });
        _tray = new TaskbarIcon { ContextMenu = menu,
            LeftClickCommand = new RelayCommand(() => ShowMain("tray")) };
        UpdateTrayIcon();
        _tray.TrayIcon.MessageWindow.DpiChanged += TrayDisplayChanged;
        _tray.TrayIcon.MessageWindow.TaskbarCreated += TrayDisplayChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += TrayDisplayChanged;
        UpdateTrayText();
        _tray.ForceCreate(enablesEfficiencyMode: false);
        _log!.Write("tray.created");
    }

    private void UpdateTrayIcon()
    {
        if (_exiting || _tray is null) return;
        var icon = AppIcon.Load(AppIcon.TrayPixelSize());
        var previous = _trayIcon;
        _tray.Icon = icon;
        _trayIcon = icon;
        previous?.Dispose();
    }

    private void TrayDisplayChanged(object? sender, EventArgs e)
    {
        if (!_exiting && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(UpdateTrayIcon);
    }

    private void DisposeTray()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= TrayDisplayChanged;
        if (_tray is not null)
        {
            _tray.TrayIcon.MessageWindow.DpiChanged -= TrayDisplayChanged;
            _tray.TrayIcon.MessageWindow.TaskbarCreated -= TrayDisplayChanged;
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private async Task ExitAsync(int code = 0)
    {
        if (_exiting) return;
        _exiting = true;
        _settings?.Close();
        _window?.PrepareExit();
        _viewModel?.Suspend(true);
        if (_viewModel is not null)
        {
            while (true)
            {
                try { await _viewModel.FlushAsync(); break; }
                catch (Exception ex)
                {
                    _log?.Write("save.exit-failed", new { ex.Message });
                    if (_smoke) { code = 1; break; }
                    var answer = MessageBox.Show($"配置尚未保存：{ex.Message}\n是：重试\n否：放弃未保存修改并退出\n取消：返回程序", "QuickLaunch", MessageBoxButton.YesNoCancel);
                    if (answer == MessageBoxResult.Yes) continue;
                    if (answer == MessageBoxResult.No) break;
                    _exiting = false;
                    _viewModel.Suspend(false);
                    _window?.ResumeAfterExitFailure();
                    return;
                }
            }
        }
        _hotkey?.Dispose();
        DisposeTray();
        _icons?.Dispose();
        _theme?.Dispose();
        if (_store is not null) await _store.DisposeAsync();
        if (_instance is not null) await _instance.DisposeAsync();
        if (_log is not null)
        {
            _log.Write("shutdown", new { code, savedRevision = _store?.SavedRevision });
            try { await _log.DisposeAsync(); } catch (TimeoutException) { }
        }
        _window?.Close();
        Shutdown(code);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (_exiting) return;
        _exiting = true;
        _window?.PrepareExit();
        _viewModel?.Suspend(true);
        if (_store is { IsReadOnly: false } && _viewModel is not null)
        {
            try
            {
                // This completion depends only on the IO worker, never the UI dispatcher.
                // OS shutdown gets a bounded best-effort flush, unlike normal async exit.
                bool saved = _store.SaveAsync(_viewModel.Snapshot(), _viewModel.Revision).Wait(TimeSpan.FromMilliseconds(800));
                _log?.Write("session-ending", new { saved });
            }
            catch (Exception ex) { _log?.Write("session-ending.failed", new { ex.Message }); }
        }
        _hotkey?.Dispose();
        DisposeTray();
        // Do not cancel OS shutdown or await IPC/logging indefinitely; OS releases remaining handles.
    }
}
