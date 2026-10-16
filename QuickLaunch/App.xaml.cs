using System.IO;
using System.Security.Principal;
using System.Windows.Controls;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using QuickLaunch.Diagnostics;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;
using Serilog;

namespace QuickLaunch;

public partial class App : Application
{
    private ILogger<App> _log = NullLogger<App>.Instance;
    private IHost? _host;
    private Serilog.Core.Logger? _sessionLogger;
    private Task? _loggingInitialization;
    private Task? _disposeTask;
    private LauncherWindow? _window;
    private IDialogService? _dialogs;
    private HotkeyService? _hotkey;
    private TaskbarIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private SingleInstanceService? _instance;
    private ConfigService? _store;
    private MainViewModel? _viewModel;
    private StartupService? _startup;
    private volatile bool _exiting;
    private bool _pendingShow, _performanceRun;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool benchmark = e.Args.Contains("--benchmark");
        bool startupProbe = e.Args.Contains("--startup-probe");
        bool exitRequested = e.Args.Contains("--exit");
        _performanceRun = benchmark || startupProbe;
        try
        {
            string? explicitConfig = Argument(e.Args, "--config-dir");
            string? launchLog = Argument(e.Args, "--e2e-launch-log");
            if (launchLog is not null && explicitConfig is null)
                throw new ArgumentException("--e2e-launch-log requires an isolated --config-dir.");
            if (_performanceRun && explicitConfig is null) throw new ArgumentException("性能检查必须指定独立 --config-dir，禁止修改用户配置。");
            var paths = DataPaths.Resolve(explicitConfig, Argument(e.Args, "--diagnostics-dir"));
            if (exitRequested)
            {
                Shutdown(await SingleInstanceService.RequestExitAsync(paths.ConfigDirectory));
                return;
            }
            bool readOnly = false;
            if (!DataPaths.CanWrite(paths.ConfigDirectory))
            {
                if (_performanceRun) readOnly = true;
                else
                {
                    var answer = MessageBox.Show((paths.Portable ? "便携 Data 目录不可写。" : "所选配置目录不可写。") + "\n是：使用默认目录\n否：只读运行\n取消：退出", "QuickLaunch", MessageBoxButton.YesNoCancel);
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
                    if (_window is null) _pendingShow = true;
                    else ShowMain("second-instance");
                });
            }, () => !_exiting && !Dispatcher.HasShutdownStarted,
                () => Dispatcher.BeginInvoke(() => _ = ExitAsync()));
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
            if (benchmark)
            {
                string file = Path.Combine(paths.ConfigDirectory, "config.json");
                if (File.Exists(file)) throw new InvalidOperationException("性能检查必须使用新的空配置目录。");
                new AtomicConfigWriter().Write(file, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    BenchmarkRunner.CreateConfig(paths.ConfigDirectory, e.Args.Contains("--single-group")), ConfigJsonContext.Default.AppConfig));
            }
            // Keep host/logging/DI construction after the client-only exit and instance paths.
            var hostWatch = System.Diagnostics.Stopwatch.StartNew();
            var diagnostics = new LoggingDiagnostics();
            // Logger initialization and empty-host setup are independent. Neither creates
            // a WPF service; the first file sink is still created by Serilog's batch worker.
            _loggingInitialization = Task.Run(() => _sessionLogger = SessionLogging.Create(paths.LogDirectory, diagnostics));
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = typeof(App).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });
            double emptyBuilderMs = hostWatch.Elapsed.TotalMilliseconds;
            builder.Services.AddSingleton(diagnostics);
            builder.Services.AddLauncher(paths, readOnly, _performanceRun, launchLog, Dispatcher, _instance, OpenSettings, () =>
            {
                if (_exiting) return;
                if (_dialogs?.ActivateSettings() != true) _window?.Toggle();
            }, UpdateTrayText, () => ExitAsync());
            await _loggingInitialization;
            if (_exiting) return; // An IPC/OS exit may have arrived during initialization.
            builder.Services.AddSerilog(_sessionLogger!, dispose: false);
            double registrationMs = hostWatch.Elapsed.TotalMilliseconds;
            _host = builder.Build();
            _host.Services.GetRequiredService<SingleInstanceService>();
            _instance = null; // The host now owns the lease, including failed startup cleanup.
            _log = _host.Services.GetRequiredService<ILogger<App>>();
            await _host.StartAsync();
            double hostStartedMs = hostWatch.Elapsed.TotalMilliseconds;
            _window = _host.Services.GetRequiredService<LauncherWindow>();
            _store = _host.Services.GetRequiredService<ConfigService>();
            _viewModel = _host.Services.GetRequiredService<MainViewModel>();
            _startup = _host.Services.GetRequiredService<StartupService>();
            _dialogs = _host.Services.GetRequiredService<IDialogService>();
            MainWindow = _window;
            _hotkey = _host.Services.GetRequiredService<HotkeyService>();
            _log.HotkeyRegistered(_hotkey.Registered, _hotkey.Error, _viewModel.HotkeyDisplay);
            try { CreateTray(); }
            catch (InvalidOperationException ex) when (_performanceRun && e.Args.Contains("--allow-unavailable-tray"))
            {
                _log.TrayUnavailable(LogPrivacy.Message(ex.Message));
            }
            _ = _window.LoadIconsAsync();
            using var identity = WindowsIdentity.GetCurrent();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            _log.Startup(Environment.Version.ToString(), Environment.OSVersion.VersionString, elevated,
                $"v{typeof(App).Assembly.GetName().Version?.ToString(3)}", (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds,
                paths.ConfigDirectory, _store.IsReadOnly, _store.Notice is null ? null : LogPrivacy.Message(_store.Notice));
            _log.HostTiming(emptyBuilderMs, registrationMs - emptyBuilderMs, hostStartedMs - registrationMs);
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
            else if (!e.Args.Contains("--silent") || _pendingShow) ShowMain(_pendingShow ? "pending-instance" : "startup");
            else _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => _ = _window.PrewarmAsync());
            _pendingShow = false;
        }
        catch (Exception ex)
        {
            _log.StartupFailed(LogPrivacy.Message(ex.Message), ex.GetType().Name);
            if (!_performanceRun && !exitRequested) MessageBox.Show(ex is OperationCanceledException ? "已有实例未及时响应，请稍后重新启动。" : ex.Message, "QuickLaunch 无法启动");
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
        if (_dialogs?.ActivateSettings() != true) _window?.ShowPanel(source);
    }

    public void OpenSettings()
    {
        if (!_exiting && _window is not null) _dialogs?.ShowSettings(_window);
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
        _tray = new TaskbarIcon { ContextMenu = menu, NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => ShowMain("tray")) };
        UpdateTrayIcon();
        _tray.TrayIcon.MessageWindow.DpiChanged += TrayDisplayChanged;
        _tray.TrayIcon.MessageWindow.TaskbarCreated += TrayDisplayChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += TrayDisplayChanged;
        UpdateTrayText();
        _tray.ForceCreate(enablesEfficiencyMode: false);
        _log.TrayCreated();
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
        _dialogs?.CloseSettings();
        _window?.PrepareExit();
        _viewModel?.Suspend(true);
        if (_viewModel is not null)
        {
            while (true)
            {
                try { await _viewModel.FlushAsync(); break; }
                catch (Exception ex)
                {
                    _log.SaveExitFailed(LogPrivacy.Message(ex.Message));
                    if (_performanceRun) { code = 1; break; }
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
        _log.Shutdown(code, _store?.SavedRevision);
        await DisposeServicesAsync();
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
                _log.SessionEnding(saved);
            }
            catch (Exception ex) { _log.SessionEndingFailed(LogPrivacy.Message(ex.Message)); }
        }
        _dialogs?.CloseSettings();
        // UI-affine disposal runs first, before the async config/IPC/log drains.
        // The remaining continuations never require this dispatcher during OS shutdown.
        try { DisposeServicesAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }

    private Task DisposeServicesAsync() => _disposeTask ??= DisposeCoreAsync();

    private async Task DisposeCoreAsync()
    {
        DisposeTray();
        try
        {
            if (_host is not null)
            {
                try { await _host.StopAsync().ConfigureAwait(false); }
                finally { await ((IAsyncDisposable)_host).DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            if (_instance is not null) await _instance.DisposeAsync().ConfigureAwait(false);
            // A failure while setting up the builder can precede the logger's completion.
            if (_loggingInitialization is not null)
            {
                try { await _loggingInitialization.ConfigureAwait(false); }
                catch (Exception) { /* Initialization failure was handled by startup. */ }
            }
            if (_sessionLogger is not null) await _sessionLogger.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Covers Application.Shutdown as well as the normal async exit and session end.
        _exiting = true;
        if (_disposeTask is null)
        {
            _dialogs?.CloseSettings();
            _window?.PrepareExit();
            _viewModel?.Suspend(true);
        }
        try { DisposeServicesAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        base.OnExit(e);
    }
}
