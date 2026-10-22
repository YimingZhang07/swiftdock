using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using Swiftdock.Interop;
using Swiftdock.Models;
using Swiftdock.Views;

namespace Swiftdock.Diagnostics;

internal static class BenchmarkRunner
{
    public static AppConfig CreateConfig(string directory, bool singleGroup)
    {
        var groups = Enumerable.Range(0, singleGroup ? 1 : 4).Select(i => new GroupConfig { Name = $"分组 {i + 1}" }).ToList();
        string[] paths = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"),
            Path.Combine(directory, "fixture.lnk"), directory, Path.Combine(directory, "fixture.txt"), "https://example.com"];
        LaunchKind[] kinds = [LaunchKind.App, LaunchKind.App, LaunchKind.Folder, LaunchKind.File, LaunchKind.Url];
        for (int i = 0; i < 200; i++) groups[singleGroup ? 0 : i / 50].Items.Add(new($"项目 {i + 1:000}", paths[i % 5], kinds[i % 5], i % 5 == 0 ? $"--sample={i}" : ""));
        return new() { AutoFetchWebsiteIcons = false, Groups = groups, LastGroupId = groups[0].Id };
    }

    private static object Statistics(List<double> values) => new
    {
        samples = values.Count,
        p50 = values.Count == 0 ? (double?)null : values.Order().ElementAt((int)Math.Ceiling(values.Count * .50) - 1),
        p95 = values.Count == 0 ? (double?)null : values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1),
        max = values.Count == 0 ? (double?)null : values.Max()
    };

    public static async Task RunAsync(LauncherWindow window, string output, int idleSeconds, bool interactive = false, bool prewarm = false)
    {
        bool? prewarmCompleted = prewarm ? await window.PrewarmAsync() : null;
        if (interactive)
        {
            window.ShowPanel("benchmark-ready");
            System.Windows.MessageBox.Show(window, "点击确定后开始 100 次显示/隐藏与切组测试。期间请暂时不要操作其他窗口，结束后程序自动退出。", "迅启性能检查");
        }
        window.ShowPanel("benchmark-first");
        double? firstRender = await window.PendingRender;
        double? first = await window.PendingShow;
        await window.LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var warm = new List<double>(); var renders = new List<double>(); var hide = new List<double>(); var groups = new List<double>(); var firstGroups = new List<double>();
        int failures = 0;
        for (int i = 0; i < 100; i++)
        {
            var timer = Stopwatch.StartNew(); window.HidePanel(); hide.Add(timer.Elapsed.TotalMilliseconds);
            await Task.Delay(20);
            window.ShowPanel("benchmark-warm");
            double? render = await window.PendingRender;
            if (render.HasValue) renders.Add(render.Value);
            double? value;
            try { value = await window.PendingShow.WaitAsync(TimeSpan.FromMilliseconds(150)); }
            catch (TimeoutException) { value = null; }
            if (value.HasValue) warm.Add(value.Value); else failures++;
        }
        foreach (var group in window.Groups)
        {
            var timer = Stopwatch.StartNew(); window.SelectGroup(group, preview: true);
            await NextFrame(); firstGroups.Add(timer.Elapsed.TotalMilliseconds);
        }
        for (int i = 0; i < 100; i++)
        {
            var timer = Stopwatch.StartNew();
            window.SelectGroup(window.Groups[i % window.Groups.Count], preview: true);
            await NextFrame(); groups.Add(timer.Elapsed.TotalMilliseconds);
        }
        window.HidePanel();
        await Task.Delay(200);
        var beforeIdle = Memory();
        double? idle = null;
        if (idleSeconds > 0)
        {
            window.Log.BenchmarkIdle(idleSeconds);
            await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
            window.ShowPanel("benchmark-idle"); idle = await window.PendingShow; window.HidePanel();
        }
        var report = new
        {
            dataset = window.Groups.Select(g => g.Items.Count).ToArray(), firstShowProxyMs = first, firstRenderOnlyMs = firstRender, prewarm, prewarmCompleted,
            firstGroupRenderProxyMs = firstGroups, warmRenderOnlyMs = Statistics(renders), interactive,
            warmShowProxyMs = Statistics(warm), hideCallMs = Statistics(hide), groupRenderProxyMs = Statistics(groups), failures,
            idleSeconds, idleShowProxyMs = idle, beforeIdle, afterIdle = Memory(),
            icons = new { window.Icons.MemoryHits, window.Icons.DiskHits, window.Icons.Extractions, window.Icons.CachedCount },
            runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
            logicalCpu = Environment.ProcessorCount, dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX,
            animations = System.Windows.SystemParameters.ClientAreaAnimation,
            physicalPresentationMeasured = false, realHotkeyMeasured = false,
            note = "100 warm samples; first/idle are single observations; single-group switch data is a control, not a cross-group claim. No explicit GC or working-set trimming."
        };
        await File.WriteAllTextAsync(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task NextFrame()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => done.TrySetResult();
        CompositionTarget.Rendering += handler;
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
    private static object Memory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var memory = new NativeMethods.MemoryCounters { Size = (uint)Marshal.SizeOf<NativeMethods.MemoryCounters>() };
        bool available = NativeMethods.GetProcessMemoryInfo(process.Handle, ref memory, memory.Size);
        return new { privateWorkingSetBytes = available ? (ulong?)memory.PrivateWorkingSet : null,
            privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64, managedHeapBytes = GC.GetTotalMemory(false) };
    }
}
