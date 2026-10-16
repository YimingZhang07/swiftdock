using System.IO;
using System.Net.Http;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickLaunch.Diagnostics;
using QuickLaunch.Models;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Services;

internal static class ApplicationServices
{
    internal static IServiceCollection AddLauncher(this IServiceCollection services, DataPaths paths, bool readOnly,
        bool performanceRun, string? launchLog, Dispatcher dispatcher, SingleInstanceService instance,
        Action openSettings, Action hotkeyPressed, Action? hotkeyChanged = null, Func<Task>? exit = null)
    {
        services.AddSingleton(paths);
        services.AddSingleton(_ => instance); // Adopted after the early single-instance handshake.
        services.AddSingleton<IHostLifetime>(_ => new WpfHostLifetime());
        services.AddSingleton<IConfigWriter>(_ => new AtomicConfigWriter());
        services.AddSingleton(sp => ConfigService.Open(paths.ConfigDirectory, readOnly, sp.GetRequiredService<IConfigWriter>()));
        services.AddSingleton(sp => new MainViewModel(sp.GetRequiredService<ConfigService>()));
        services.AddSingleton(sp => new ThemeService(dispatcher, sp.GetRequiredService<MainViewModel>()));
        services.AddSingleton(_ => new StartupService(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "QuickLaunch.exe"), paths.ConfigDirectory, portable: paths.Portable));
        services.AddIconServices(performanceRun || launchLog is not null ? Path.Combine(paths.LogDirectory, "icons") : paths.IconDirectory);
        services.AddSingleton<ISettingsState>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<ISettingsActions>(sp => new SettingsActions(sp.GetRequiredService<LauncherWindow>().ResetWindowSize,
            hotkeyChanged ?? (() => { }), exit ?? (() => Task.CompletedTask), () =>
            {
                Directory.CreateDirectory(paths.LogDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.LogDirectory) { UseShellExecute = true });
            }));
        services.AddDialogs();
        services.AddSingleton<Func<LaunchItem, bool, Task<LaunchResult>>>(_ => launchLog is null
            ? LaunchService.StartAsync : new E2eLaunchRecorder(launchLog).RecordAsync);
        services.AddSingleton<ILauncherDialogs>(sp => new LauncherDialogs(sp.GetRequiredService<IDialogService>(),
            () => sp.GetRequiredService<LauncherWindow>()));
        services.AddSingleton<ILauncherActions, LauncherActions>();
        services.AddSingleton(sp => new LauncherViewModel(sp.GetRequiredService<MainViewModel>(),
            sp.GetRequiredService<ILauncherDialogs>(), sp.GetRequiredService<ILauncherActions>(), sp.GetRequiredService<ILogger<LauncherViewModel>>()));
        services.AddSingleton(sp =>
        {
            // Resolve UI dependencies in lifetime order: the window closes before its
            // theme/icons, and config drains before the single-instance lease is released.
            var model = sp.GetRequiredService<LauncherViewModel>();
            var theme = sp.GetRequiredService<ThemeService>();
            var window = new LauncherWindow(sp.GetRequiredService<ILogger<LauncherWindow>>(), model,
                sp.GetRequiredService<IconService>(), sp.GetRequiredService<IDialogService>()) { SettingsRequested = openSettings };
            try { window.InitializeNative(theme); return window; }
            catch { window.Dispose(); throw; }
        });
        services.AddSingleton(sp => new HotkeyService(new WindowInteropHelper(sp.GetRequiredService<LauncherWindow>()).Handle,
            hotkeyPressed, sp.GetRequiredService<MainViewModel>().Hotkey));
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<HotkeyService>());
        return services;
    }

    internal static IServiceCollection AddDialogs(this IServiceCollection services)
    {
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IItemEditorEnvironment, ItemEditorEnvironment>();
        services.AddScoped<DialogFilePicker>();
        services.AddScoped<IFilePicker>(sp => sp.GetRequiredService<DialogFilePicker>());
        services.AddScoped<SettingsViewModel>();
        services.AddScoped<EditItemViewModel>();
        services.AddScoped<NameDialogViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<EditItemDialog>();
        services.AddTransient<NameDialog>();
        services.AddTransient<Wpf.Ui.Controls.MessageBox>();
        return services;
    }

    internal static IServiceCollection AddIconServices(this IServiceCollection services, string directory, Func<HttpMessageHandler>? handler = null)
    {
        services.AddSingleton(_ => new IconThread());
        services.AddSingleton(sp => new FaviconService(directory, sp.GetRequiredService<ILogger<FaviconService>>(),
            sp.GetRequiredService<IconThread>().RenderFaviconAsync, handler?.Invoke()));
        services.AddSingleton(sp => new IconService(directory, sp.GetRequiredService<ILogger<IconService>>(),
            sp.GetRequiredService<FaviconService>(), sp.GetRequiredService<IconThread>()));
        return services;
    }

    // WPF owns the message loop and shutdown signals; do not install ConsoleLifetime.
    private sealed class WpfHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
