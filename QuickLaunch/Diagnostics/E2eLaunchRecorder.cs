using System.IO;
using System.Text.Json;
using QuickLaunch.Models;
using QuickLaunch.Services;

namespace QuickLaunch.Diagnostics;

// Opt-in from --e2e-launch-log only. Keep the real input/launch-result path, but never invoke the Shell.
internal sealed class E2eLaunchRecorder(string path)
{
    public Task<LaunchResult> RecordAsync(LaunchItem item, bool elevate)
    {
        string line = JsonSerializer.Serialize(new { id = item.Id, name = item.Name, elevated = elevate || item.RunAsAdmin });
        File.AppendAllText(path, line + Environment.NewLine);
        return Task.FromResult(new LaunchResult(true, false, "E2E launch recorded"));
    }
}
