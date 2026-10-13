using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using QuickLaunch.Diagnostics;
using QuickLaunch.Services;
using QuickLaunch.Views;

namespace QuickLaunch;

public partial class App : Application
{
    private DiagnosticLog? _log;
    private LauncherWindow? _window;
    private HotkeyService? _hotkey;
    private TaskbarIcon? _tray;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string directory = Argument(e.Args, "--diagnostics-dir")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickLaunch", "M0");
            _log = new(directory);
            _window = new(_log) { ExitRequested = () => _ = ExitAsync() };
            MainWindow = _window;
            _window.InitializeNative();
            _hotkey = new(new WindowInteropHelper(_window).Handle, _window.Toggle);
            _log.Write("hotkey.registered", new { _hotkey.Registered, _hotkey.Error });
            CreateTray();
            using var identity = WindowsIdentity.GetCurrent();
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            _log.Write("startup", new { runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
                elevated, log = _log.FilePath, milestone = "M0", configurationPersisted = false });
            if (elevated) _window.SetStatus("当前以管理员权限运行；请用普通权限重启以验证外部拖入。");
            if (!_hotkey.Registered) _window.SetStatus($"Alt+Space 注册失败（{_hotkey.Error}），可点击托盘显示。M1 将提供热键设置。");
            SessionEnding += (_, _) => _window.PrepareExit();
            if (e.Args.Contains("--smoke-test"))
            {
                bool passed = await PrototypeSmoke.RunAsync(_window, _hotkey, directory);
                await ExitAsync(passed ? 0 : 1);
            }
            else if (!e.Args.Contains("--silent")) _window.ShowPanel("startup");
        }
        catch (Exception ex)
        {
            _log?.Write("startup.failed", new { ex.Message, type = ex.GetType().Name });
            if (!e.Args.Contains("--smoke-test")) MessageBox.Show(ex.Message, "QuickLaunch 无法启动");
            await ExitAsync(1);
        }
    }

    private static string? Argument(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void CreateTray()
    {
        // H.NotifyIcon supports GeneratedIconSource, but not arbitrary WPF DrawingImage.
        var icon = new GeneratedIconSource
        {
            Text = "Q", FontSize = 32, Foreground = new SolidColorBrush(Color.FromRgb(117, 219, 197)),
            Background = new SolidColorBrush(Color.FromRgb(23, 29, 39))
        };
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "显示 QuickLaunch", Command = new RelayCommand(() => _window!.ShowPanel("tray-menu")) });
        menu.Items.Add(new MenuItem { Header = "保持显示以添加", Command = new RelayCommand(() => { _window!.Pinned = true; _window.ShowPanel("tray-add"); }) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "退出", Command = new RelayCommand(() => _ = ExitAsync()) });
        _tray = new TaskbarIcon
        {
            ToolTipText = "QuickLaunch · Alt+Space", IconSource = icon, ContextMenu = menu,
            LeftClickCommand = new RelayCommand(() => _window!.ShowPanel("tray"))
        };
        _tray.ForceCreate(enablesEfficiencyMode: false);
        _log!.Write("tray.created");
    }

    private async Task ExitAsync(int code = 0)
    {
        if (_exiting) return;
        _exiting = true;
        _window?.PrepareExit();
        _hotkey?.Dispose();
        _tray?.Dispose();
        if (_log is not null)
        {
            _log.Write("shutdown", new { code });
            try { await _log.DisposeAsync(); } catch (TimeoutException) { }
        }
        _window?.Close();
        Shutdown(code);
    }
}
