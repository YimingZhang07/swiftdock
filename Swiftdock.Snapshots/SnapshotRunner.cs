using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Swiftdock.Snapshots;

internal static class SnapshotRunner
{
    internal const int ImageTimeoutSeconds = 6;
    internal const string CaptureDescription = "96-DPI WPF rendering, composited on ApplicationBackgroundBrush; no DWM material, native border or shadow.";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly ManifestWriter Manifest = new();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 && (args.Length != 3 || args[1] != "--image"))
        { Console.Error.WriteLine("Usage: Swiftdock.Snapshots <output-directory> [--image <filename.png>]"); return 1; }
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var plan = CreatePlan();
        if (args.Length == 3)
        {
            var image = plan.SingleOrDefault(image => image.File == args[2]);
            if (image is null) { Console.Error.WriteLine("Unknown snapshot image."); return 1; }
            try { return ImageRenderer.Run(output, image); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        // No WPF dispatcher here: even a synchronous layout/native/dialog hang
        // cannot stop the deadline or prevent the next image from being attempted.
        var report = new SnapshotReport { Expected = plan.Select(image => image.File).ToArray() };
        try
        {
            SaveReport(output, report);
            using var processJob = new RenderProcessJob();
            foreach (var image in plan)
            {
                report.Current = image.File;
                SaveReport(output, report); // Durable progress before starting the worker.
                try
                {
                    File.Delete(Path.Combine(output, image.File)); // Never accept a stale PNG.
                    RenderInWorker(output, image.File);
                    if (!File.Exists(Path.Combine(output, image.File))) throw new IOException("Worker did not render a PNG.");
                    report.Rendered.Add(image.File);
                }
                catch (Exception error) { report.Failed.Add(new(image.File, error.Message)); }
                report.Current = null;
                SaveReport(output, report);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            foreach (string file in report.Expected.Except(report.Rendered).Except(report.Failed.Select(f => f.Image).OfType<string>()))
                report.Failed.Add(new(file, "Batch stopped: " + error.Message));
            if (report.Failed.Count == 0) report.Failed.Add(new(null, error.Message));
        }
        finally
        {
            report.Current = null;
            report.Completed = true;
            SaveReport(output, report);
        }
        Console.WriteLine($"Rendered {report.Rendered.Count}; failed {report.Failed.Count}. See snapshots.json.");
        if (report.ManifestErrors.Count > 0)
        {
            // Report only after every image/cleanup has been attempted. Preserve the
            // final inventory on stderr even if snapshots.json is still locked.
            Console.Error.WriteLine("Manifest write errors (rendering continued): " + JsonSerializer.Serialize(report, Json));
        }
        return report.Failed.Count == 0 && report.ManifestErrors.Count == 0 ? 0 : 1;
    }

    private static void RenderInWorker(string output, string file)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(output); start.ArgumentList.Add("--image"); start.ArgumentList.Add(file);
        using var worker = Process.Start(start) ?? throw new IOException("Could not start image renderer.");
        Task<string> error = worker.StandardError.ReadToEndAsync();
        try
        {
            if (!worker.WaitForExit(ImageTimeoutSeconds * 1000))
            {
                worker.Kill(entireProcessTree: true); worker.WaitForExit(2000);
                string phase = error.Wait(1000) ? error.Result.Trim() : "Worker output unavailable.";
                throw new TimeoutException($"{file}: exceeded {ImageTimeoutSeconds} seconds (including setup, render and cleanup). {phase}");
            }
            if (worker.ExitCode != 0)
                throw new IOException(error.Wait(1000) ? error.Result.Trim() : $"Renderer exited with code {worker.ExitCode}.");
        }
        finally
        {
            // Termination releases every HWND if the dispatcher cannot close it.
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); worker.WaitForExit(2000); }
        }
    }

    private static void SaveReport(string output, SnapshotReport report)
    {
        report.UpdatedAt = DateTimeOffset.Now;
        string path = Path.Combine(output, "snapshots.json");
        string? error = Manifest.TryWrite(path, JsonSerializer.Serialize(report, Json));
        if (error is not null) report.ManifestErrors.Add($"{report.UpdatedAt:O}, image={report.Current ?? "final"}: {error}");
    }

    internal static List<SnapshotImage> CreatePlan()
    {
        List<SnapshotImage> images = [];
        foreach (string theme in new[] { "dark", "light" })
        foreach (string kind in new[] { "launcher", "launcher-status", "settings-general", "settings-appearance", "settings-panel", "settings-about", "name-dialog", "add-dialog", "add-dialog-file", "add-dialog-folder", "add-dialog-url", "edit-dialog", "icon-picker", "icon-picker-system", "icon-picker-color", "item-menu", "delete-confirm" })
            images.Add(new(kind + (theme == "dark" ? "" : "-light") + ".png", kind, theme));
        foreach (string theme in new[] { "dark", "light" })
        foreach (string accent in new[] { "blue", "purple" })
        foreach (string backdrop in new[] { "acrylic", "mica", "solid" })
        {
            images.Add(new($"appearance-{theme}-{accent}-{backdrop}.png", "launcher", theme, accent, backdrop, Selected: true));
            if (backdrop == "solid") images.Add(new($"appearance-settings-{theme}-{accent}.png", "settings-appearance", theme, accent));
        }
        images.Add(new("panel-polish.png", "launcher", "dark", Selected: true));
        foreach (string theme in new[] { "dark", "light" })
        foreach (int scale in new[] { 80, 150 })
            images.Add(new($"launcher-{scale}-{theme}.png", "launcher", theme, Selected: true, Scale: scale));
        return images;
    }

    private sealed class SnapshotReport
    {
        public string[] Expected { get; init; } = [];
        public List<string> Rendered { get; } = [];
        public List<SnapshotFailure> Failed { get; } = [];
        public List<string> ManifestErrors { get; } = [];
        public string? Current { get; set; }
        public bool Completed { get; set; }
        public bool TimedOut { get; set; }
        public DateTimeOffset GeneratedAt { get; } = DateTimeOffset.Now;
        public DateTimeOffset UpdatedAt { get; set; }
        public int ImageTimeoutSeconds => SnapshotRunner.ImageTimeoutSeconds;
        public string Capture => CaptureDescription;
    }
    private sealed record SnapshotFailure(string? Image, string Error);
}

internal sealed record SnapshotImage(string File, string Kind, string Theme, string Accent = "blue", string Backdrop = "solid", bool Selected = false, int Scale = 100);
