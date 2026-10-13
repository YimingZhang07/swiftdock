using System.IO;
using System.Windows.Interop;
using QuickLaunch.Interop;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

// Exercise the real window/launch continuation with controlled Shell outcomes,
// without opening the user's applications or requiring foreground permission.
internal static class WindowVisibilitySmoke
{
    private sealed class ProbeWindow(DiagnosticLog log, MainViewModel model, IconService icons,
        Func<LaunchItem, bool, Task<LaunchResult>> startLaunch) : LauncherWindow(log, model, icons, startLaunch)
    {
        public void NotifyDeactivated() => OnDeactivated(EventArgs.Empty);
    }

    public static async Task RunAsync(LauncherWindow main, string directory, Action<string, bool> check)
    {
        await using var store = ConfigService.Open(Path.Combine(directory, "visibility-probe"));
        var model = new MainViewModel(store);
        TaskCompletionSource<LaunchResult>? completion = null;
        var probe = new ProbeWindow(main.Log, model, main.Icons, (_, _) => completion!.Task);
        try
        {
            probe.InitializeNative();
            probe.ShowPanel("visibility-probe", forDrop: true);
            await BenchmarkRunner.NextFrame();
            nint hwnd = new WindowInteropHelper(probe).Handle;
            bool Visible() => probe.IsVisible && NativeMethods.IsWindowVisible(hwnd);
            probe.NotifyDeactivated();
            check("deactivation-keeps-panel-visible", !probe.IsActive && Visible());

            foreach (var (name, result) in new[]
            {
                ("accepted", new LaunchResult(true, false, "模拟启动成功")),
                ("failed", new LaunchResult(false, false, "模拟启动失败")),
                ("cancelled", new LaunchResult(false, true, "模拟取消提权"))
            })
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var launch = probe.LaunchAsync(model.Groups[0].Items[0]);
                check($"launch-{name}-pending-keeps-panel-visible", !launch.IsCompleted && Visible());
                probe.NotifyDeactivated();
                completion.SetResult(result);
                await launch.WaitAsync(TimeSpan.FromSeconds(2));
                check($"launch-{name}-completion-keeps-panel-visible", Visible() && probe.StatusText.Contains(result.Message));
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = probe.LaunchAsync(model.Groups[0].Items[0]);
            probe.HidePanel();
            completion.SetResult(new(true, false, "模拟延迟完成"));
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            check("launch-completion-respects-explicit-hide", !probe.IsVisible && !NativeMethods.IsWindowVisible(hwnd));
        }
        finally
        {
            probe.PrepareExit();
            probe.Close();
            await model.FlushAsync();
            model.Suspend(true);
        }
    }
}
