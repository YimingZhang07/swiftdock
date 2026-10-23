using System.IO;
using System.Text.Json;
using Swiftdock.Models;
using Swiftdock.Services;

namespace Swiftdock.Diagnostics;

// Opt-in from --e2e-launch-log only. Keep the real input/launch-result path, but never invoke the Shell.
internal sealed class E2eLaunchRecorder(string path)
{
    public Task<LaunchResult> RecordAsync(LaunchItem item, bool elevate, string? browser = null)
    {
        var plan = LaunchService.Plan(item, elevate, browser);
        string line = JsonSerializer.Serialize(new { id = item.Id, name = item.Name, elevated = elevate || item.RunAsAdmin,
            program = plan.StartInfo.UseShellExecute ? "shell" : plan.StartInfo.FileName,
            arguments = plan.StartInfo.ArgumentList.ToArray() });
        File.AppendAllText(path, line + Environment.NewLine);
        return Task.FromResult(new LaunchResult(true, false, plan.SuccessText) { Notice = plan.BrowserUnavailable });
    }
}
