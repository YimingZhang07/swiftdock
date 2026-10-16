using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Sdk;

namespace QuickLaunch.E2E;

internal sealed partial class LauncherSession
{
    // This collection runs serially, but its test thread can change between scenarios.
    // Keep ownership and history across sessions so a startup failure includes the prior test.
    private static readonly object InputLock = new();
    private static readonly Queue<string> InputHistory = new();
    private static readonly HashSet<VirtualKeyShort> UsedKeys = [], HeldKeys = [];
    private static readonly HashSet<MouseButton> UsedButtons = [], HeldButtons = [];
    private const int InputHistoryLimit = 128;

    private void RecordInput(string action)
    {
        string entry = $"{DateTimeOffset.UtcNow:O} {_test}/{Path.GetFileName(_temp)[..8]} "
            + $"foreground=0x{Desktop.GetForegroundWindow():X} {action}";
        lock (InputLock)
        {
            InputHistory.Enqueue(entry);
            while (InputHistory.Count > InputHistoryLimit) InputHistory.Dequeue();
        }
    }

    private static string RecentInputs()
    {
        lock (InputLock) return string.Join(Environment.NewLine, InputHistory);
    }

    private void PressKey(VirtualKeyShort key, string reason = "scenario")
    {
        CheckAbort();
        // Record ownership before SendInput: a partially failed call still needs cleanup.
        lock (InputLock) { UsedKeys.Add(key); HeldKeys.Add(key); }
        SaveInputOwnership();
        RecordInput($"SendInput key down {key} (0x{(int)key:X2}); {reason}");
        Keyboard.Press(key);
    }

    private void TypeOwnedText(string text)
    {
        CheckAbort();
        var packet = (VirtualKeyShort)0xE7; // VK_PACKET is used by Unicode SendInput.
        lock (InputLock) { UsedKeys.Add(packet); HeldKeys.Add(packet); }
        SaveInputOwnership();
        RecordInput("Unicode SendInput to the verified test-owned EDIT");
        try { Keyboard.Type(text); }
        finally { ReleaseKey(packet); }
    }

    private void ReleaseKey(VirtualKeyShort key, string reason = "scenario finally")
    {
        RecordInput($"SendInput key up {key} (0x{(int)key:X2}); {reason}");
        Keyboard.Release(key);
        lock (InputLock) HeldKeys.Remove(key);
        SaveInputOwnership();
    }

    private void PressMouse(MouseButton button)
    {
        CheckAbort();
        lock (InputLock) { UsedButtons.Add(button); HeldButtons.Add(button); }
        SaveInputOwnership();
        RecordInput($"SendInput mouse down {button} at {Mouse.Position}");
        Mouse.Down(button);
    }

    private void ReleaseMouse(MouseButton button, string reason = "scenario finally")
    {
        RecordInput($"SendInput mouse up {button} at {Mouse.Position}; {reason}");
        Mouse.Up(button);
        lock (InputLock) HeldButtons.Remove(button);
        SaveInputOwnership();
    }

    private void MoveMouse(Point point, string reason = "scenario")
    {
        CheckAbort();
        RecordInput($"Mouse.Position {point}; {reason}");
        Mouse.Position = point;
    }

    private static int[] DownInputs() => Enumerable.Range(1, 254)
        .Where(key => (Desktop.GetAsyncKeyState(key) & 0x8000) != 0).ToArray();

    private static string DescribeDownInputs() => string.Join(", ", DownInputs().Select(key => $"{(VirtualKeyShort)key} (0x{key:X2})"));

    private void ReleaseAndDrainInput(string phase, bool releaseAllUsed = false)
    {
        RecordInput($"Input boundary begin: {phase}");
        VirtualKeyShort[] keys;
        MouseButton[] buttons;
        lock (InputLock)
        {
            keys = (releaseAllUsed ? UsedKeys : HeldKeys).ToArray();
            buttons = (releaseAllUsed ? UsedButtons : HeldButtons).ToArray();
        }
        var errors = new List<Exception>();
        // Try every release even if one fails. A failed release keeps its ownership entry.
        foreach (var button in buttons)
        {
            try { ReleaseMouse(button, phase); }
            catch (Exception ex) { errors.Add(ex); }
        }
        foreach (var key in keys)
        {
            try { ReleaseKey(key, phase); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException($"Input cleanup failed at {phase}.", errors);

        // SendInput returning is not a delivery acknowledgement. Let the hardware input
        // queue advance, wait for the old app before --exit, then require stable key-up state.
        // FlaUI Wait.UntilInputIsProcessed can wait on other desktop input queues.
        // Use bounded input-idle and quiet-state checks instead.
        if (_process is { HasExited: false })
            Assert.True(_process.WaitForInputIdle(1000), $"QuickLaunch did not become input-idle at {phase}.");
        var quiet = Stopwatch.StartNew();
        uint lastInput = Desktop.LastInputTick();
        try
        {
            Eventually(() =>
            {
                uint input = Desktop.LastInputTick();
                if (DownInputs().Length != 0 || input != lastInput)
                { lastInput = input; quiet.Restart(); return false; }
                return quiet.ElapsedMilliseconds >= 150;
            }, $"all keys/buttons released and input quiet at {phase}");
        }
        catch (XunitException ex)
        {
            throw new XunitException($"{ex.Message}\nStill down: {DescribeDownInputs()}\nRecent harness inputs:\n{RecentInputs()}");
        }
        RecordInput($"Input boundary complete: {phase}; all keys/buttons up; last input tick={lastInput}");
    }
}
