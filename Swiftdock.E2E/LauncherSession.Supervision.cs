using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Sdk;

namespace Swiftdock.E2E;

internal sealed partial class LauncherSession
{
    private const int ScenarioTimeoutMilliseconds = 60_000;
    private string AbortFile => Path.Combine(_temp, "abort");

    // UIA timeouts are best effort, not cancellation. Execute the existing test
    // assembly in a disposable host; the supervising host never creates UIA objects.
    private void RunIsolated(string method)
    {
        Directory.CreateDirectory(_temp);
        File.WriteAllText(Path.Combine(_temp, "run.id"), Environment.GetEnvironmentVariable("SWIFTDOCK_E2E_RUN_ID") ?? "direct");
        using (var current = Process.GetCurrentProcess())
            File.WriteAllText(Path.Combine(_temp, "supervisor.json"), JsonSerializer.Serialize(new
                { Pid = current.Id, Started = current.StartTime.ToUniversalTime().Ticks }));
        string artifacts = Path.Combine(_repo, "artifacts", "e2e", _test + "-" + Path.GetFileName(_temp));
        Directory.CreateDirectory(artifacts);
        var info = new ProcessStartInfo(Path.Combine(_repo, ".tools", "dotnet", "dotnet.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _repo
        };
        foreach (string argument in new[] { "vstest", typeof(LauncherSession).Assembly.Location,
            $"/TestCaseFilter:FullyQualifiedName=Swiftdock.E2E.LauncherTests.{method}",
            "/Logger:trx;LogFileName=scenario.trx", "/ResultsDirectory:" + artifacts }) info.ArgumentList.Add(argument);
        info.Environment["SWIFTDOCK_E2E_SCENARIO"] = _test;
        info.Environment["SWIFTDOCK_E2E_PROFILE"] = _temp;
        using var runner = Process.Start(info) ?? throw new XunitException("Could not start isolated E2E scenario.");
        Task<string> stdout = runner.StandardOutput.ReadToEndAsync(), stderr = runner.StandardError.ReadToEndAsync();
        var failures = new List<string>();
        try
        {
            if (!runner.WaitForExit(ScenarioTimeoutMilliseconds))
            {
                File.WriteAllText(AbortFile, "Scenario deadline expired; do not send further input.");
                failures.Add($"Scenario {_test} exceeded 60 seconds. A UIA/provider call may be blocked.");
                // Native HWND inventory and screen capture cannot share the blocked UIA thread.
                var capture = Task.Run(() => CaptureTimeoutArtifacts(artifacts));
                if (!capture.Wait(2000)) failures.Add("Timeout diagnostics exceeded their independent 2-second budget.");
            }
            else if (runner.ExitCode != 0) failures.Add($"Scenario worker exited with code {runner.ExitCode}; see scenario.trx.");
            else if (!File.Exists(Path.Combine(_temp, "worker.pid"))) failures.Add("The selected scenario did not execute (empty/skipped discovery).");
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally
        {
            // Run even when diagnostics, assertions or provider disposal fail. No UIA,
            // WaitForInputIdle, or window lookup may precede this shutdown request.
            try { File.WriteAllText(AbortFile, "Supervisor cleanup"); }
            catch (Exception error) { failures.Add("Abort marker: " + error.Message); }
            try { StopFromSupervisor(failures); }
            catch (Exception error) { failures.Add("Supervisor cleanup: " + error); }
            try { StopRemainingWorker(failures); }
            catch (Exception error) { failures.Add("Worker teardown: " + error.Message); }
            try
            {
                if (!runner.HasExited)
                {
                    runner.Kill(entireProcessTree: true); // Product received --exit first; also closes fixture HWNDs.
                    if (!runner.WaitForExit(2000)) failures.Add("Isolated test host did not terminate within 2 seconds.");
                }
            }
            catch (Exception error) { failures.Add("Test-host teardown: " + error.Message); }
            try { ReleaseWorkerInput(); }
            catch (Exception error) { failures.Add("Input teardown: " + error.Message); }
            try
            {
                string focusLog = Path.Combine(_temp, "focus-phases.log");
                if (File.Exists(focusLog)) File.Copy(focusLog, Path.Combine(artifacts, "focus-phases.log"), overwrite: true);
            }
            catch (Exception error) { failures.Add("Focus log copy: " + error.Message); }
            foreach (var (task, file) in new[] { (stdout, "worker.stdout.log"), (stderr, "worker.stderr.log") })
            {
                try { File.WriteAllText(Path.Combine(artifacts, file), task.Wait(1000) ? task.Result : "Output drain exceeded 1 second."); }
                catch (Exception error) { failures.Add("Output drain: " + error.Message); }
            }
        }
        Diagnostic($"Isolated scenario artifacts: {artifacts}; profile: {_temp}");
        if (failures.Count > 0)
        {
            File.WriteAllLines(Path.Combine(artifacts, "supervisor-failure.txt"), failures);
            throw new XunitException(string.Join(Environment.NewLine, failures) + Environment.NewLine + artifacts);
        }
        string ownedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Swiftdock.E2E")) + Path.DirectorySeparatorChar;
        string completedProfile = Path.GetFullPath(_temp);
        if (!completedProfile.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(completedProfile), "N", out _))
            throw new XunitException("Refusing to remove a profile outside the generated E2E temp directory.");
        Directory.Delete(completedProfile, recursive: true);
    }

    private void StopFromSupervisor(List<string> failures)
    {
        Process? instance = null;
        string record = Path.Combine(_temp, "instance.json");
        try
        {
            if (File.Exists(record))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(record));
                instance = Process.GetProcessById(json.RootElement.GetProperty("Pid").GetInt32());
                if (instance.StartTime.ToUniversalTime().Ticks != json.RootElement.GetProperty("Started").GetInt64())
                { instance.Dispose(); instance = null; } // Never kill a recycled PID.
            }
        }
        catch (ArgumentException) { }
        catch (Exception error) { failures.Add("Instance identity record: " + error.Message); }
        using (instance)
        using (var request = StartProcess("--exit", "--config-dir", Path.GetDirectoryName(ConfigFile)!))
        {
            bool clientExited = request.WaitForExit(4000);
            if (!clientExited)
            {
                failures.Add("--exit client timed out; last-resort kill of that test-owned client.");
                request.Kill(); request.WaitForExit(2000);
            }
            // The worker normally already completed --exit. Code 2 means no
            // instance, so judge cleanup by the tracked process's lifetime,
            // not by a second client's acknowledgement. A live PID still fails.
            if (instance is not null && !instance.WaitForExit(4000))
            {
                string result = clientExited ? $"returned {request.ExitCode}" : "timed out";
                failures.Add($"--exit client {result}; Swiftdock PID {instance.Id} is still running; LAST-RESORT KILL required.");
                instance.Kill();
                if (!instance.WaitForExit(2000)) failures.Add("Swiftdock remained alive after kill.");
            }
            else if (clientExited && request.ExitCode != 0)
                Diagnostic($"Cleanup --exit returned {request.ExitCode}; tracked instance has already exited.");
        }
    }

    private void StopRemainingWorker(List<string> failures)
    {
        string pidFile = Path.Combine(_temp, "worker.pid"), timeFile = Path.Combine(_temp, "worker.started");
        if (!File.Exists(pidFile) || !File.Exists(timeFile)) return;
        if (!int.TryParse(File.ReadAllText(pidFile), out int pid) || !long.TryParse(File.ReadAllText(timeFile), out long started)) return;
        Process worker;
        try { worker = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (worker)
        {
            if (worker.HasExited || worker.StartTime.ToUniversalTime().Ticks != started) return;
            failures.Add($"Forcibly terminating remaining isolated test host PID {pid} AFTER the product --exit attempt (closes fixture windows).");
            worker.Kill(entireProcessTree: true);
            if (!worker.WaitForExit(2000)) failures.Add("Isolated test host still alive after kill.");
        }
    }

    private void CaptureTimeoutArtifacts(string artifacts)
    {
        var dump = new StringBuilder().AppendLine(DateTimeOffset.UtcNow.ToString("O"));
        dump.AppendLine(Desktop.FocusDescription());
        File.WriteAllText(Path.Combine(artifacts, "timeout.windows.txt"), dump.ToString());
        var pids = new HashSet<int>();
        string workerFile = Path.Combine(_temp, "worker.pid");
        if (File.Exists(workerFile) && int.TryParse(File.ReadAllText(workerFile), out int worker)) pids.Add(worker);
        string record = Path.Combine(_temp, "instance.json");
        if (File.Exists(record))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(record));
            pids.Add(json.RootElement.GetProperty("Pid").GetInt32());
        }
        foreach (int pid in pids)
        foreach (nint hwnd in Desktop.ProcessWindowHandles(pid))
        {
            bool visible = Desktop.IsWindowVisible(hwnd);
            bool bounds = Desktop.GetWindowRect(hwnd, out var rect);
            dump.AppendLine($"PID={pid} HWND=0x{hwnd:X} visible={visible} bounds={rect} {Desktop.FocusDescription(hwnd)}");
            // Always save the dump before attempting any GDI capture.
            File.WriteAllText(Path.Combine(artifacts, "timeout.windows.txt"), dump.ToString());
            if (visible && bounds && rect.Width > 0 && rect.Height > 0)
            {
                try
                {
                    using var image = Capture.Rectangle(rect.Rectangle);
                    image.Bitmap.Save(Path.Combine(artifacts, $"timeout-{pid}-{hwnd:X}.png"));
                }
                catch (Exception error) { dump.AppendLine("Capture: " + error.Message); }
            }
        }
        File.WriteAllText(Path.Combine(artifacts, "timeout.windows.txt"), dump.ToString());
    }

    private void CheckAbort()
    {
        if (File.Exists(AbortFile)) throw new OperationCanceledException("E2E scenario supervisor requested teardown.");
    }

    private void SaveInputOwnership()
    {
        Directory.CreateDirectory(_temp);
        lock (InputLock)
            File.AppendAllText(Path.Combine(_temp, "input.jsonl"), JsonSerializer.Serialize(new
            { Keys = HeldKeys.Select(k => (int)k).ToArray(), Buttons = HeldButtons.Select(b => b.ToString()).ToArray(), Cursor = _cursor }) + "\n");
    }

    private void ReleaseWorkerInput()
    {
        string path = Path.Combine(_temp, "input.jsonl");
        if (!File.Exists(path)) return;
        // Ignore a final partial record if the worker was terminated during a write.
        // Ownership is recorded before key-down, so an incomplete new record cannot
        // represent input that was already sent.
        string? last = File.ReadAllText(path).Split('\n').SkipLast(1).LastOrDefault();
        if (last is null) return;
        using var json = JsonDocument.Parse(last);
        foreach (var key in json.RootElement.GetProperty("Keys").EnumerateArray()) Keyboard.Release((VirtualKeyShort)key.GetInt32());
        foreach (var button in json.RootElement.GetProperty("Buttons").EnumerateArray()) Mouse.Up(Enum.Parse<MouseButton>(button.GetString()!));
        if (json.RootElement.GetProperty("Cursor") is { ValueKind: JsonValueKind.Object } cursor)
            Mouse.Position = new Point(cursor.GetProperty("X").GetInt32(), cursor.GetProperty("Y").GetInt32());
    }
}
