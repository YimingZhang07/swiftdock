using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickLaunch.Interop;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class WindowPositionSmoke
{
    private sealed class RecordingWriter : IConfigWriter
    {
        public int Writes { get; private set; }
        // Atomic replacement is covered separately; this smoke checks the debounced pipeline in restricted desktops too.
        public void Write(string path, byte[] bytes) { File.WriteAllBytes(path, bytes); Writes++; }
    }

    public static async Task RunAsync(LauncherWindow main, string directory, Action<string, bool> check)
    {
        var writer = new RecordingWriter();
        await using var store = ConfigService.Open(Path.Combine(directory, "position"), writer: writer);
        var model = new MainViewModel(store);
        var window = new LauncherWindow(main.Log, model, main.Icons);
        SettingsWindow? settings = null;
        try
        {
            window.InitializeNative();
            nint hwnd = new WindowInteropHelper(window).Handle;
            var desktop = WindowPlacement.ReadDesktop(hwnd);
            var monitor = desktop.Monitors[0];
            desktop = desktop with { Cursor = new(monitor.Bounds.Left + monitor.Bounds.Width / 2, monitor.Bounds.Top + monitor.Bounds.Height / 2) };
            window.GetPlacementDesktop = _ => desktop;
            Rect Bounds() => NativeMethods.GetWindowRect(hwnd, out var rect) ? WindowPlacement.ToRect(rect) : Rect.Empty;
            void Move(Point point) => NativeMethods.SetWindowPos(hwnd, 0, (int)point.X, (int)point.Y, 0, 0, 0x0015);
            async Task Settle()
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
            }
            Rect DefaultBounds() => WindowPlacement.ResolvePanel("cursor", null, model.WindowSize, new(window.MinWidth, window.MinHeight),
                model.Columns, model.CellSize, window.SelectedGroup.Items.Count, desktop).Bounds;

            window.ShowPanel("position-initial", forDrop: true);
            await Settle();
            await model.FlushAsync();
            byte[] initial = await File.ReadAllBytesAsync(store.FilePath);
            long revision = model.Revision;
            int writes = writer.Writes;
            window.BeginSizeMove(); window.CompleteSizeMove();
            check("position-no-motion-does-not-create-saved-position", model.WindowPosition is null && model.Revision == revision);
            Move(new(monitor.WorkArea.Left + 16, monitor.WorkArea.Top + 16));
            WindowPlacement.ResizeInPlace(window, window.CellSize, 12, 4);
            window.FitContent();
            WindowPlacement.AtCursor(window, savedSize: new(400, 250));
            window.RefreshDpi();
            window.CompleteSizeMove();
            await Settle();
            await model.FlushAsync();
            check("position-programmatic-placement-and-sizing-do-not-save", model.WindowPosition is null && model.WindowSize is null
                && model.Revision == revision && writer.Writes == writes && initial.SequenceEqual(File.ReadAllBytes(store.FilePath)));

            window.ShowPanel("position-before-move", forDrop: true);
            await Settle();
            var target = new Point(monitor.WorkArea.Left + 16, monitor.WorkArea.Top + 16);
            window.BeginSizeMove();
            Move(target);
            window.CompleteSizeMove();
            var saved = Bounds();
            check("position-user-move-completed-saves-native-top-left", saved.Location == target && model.WindowPosition == saved.Location
                && model.WindowSize is null && model.Revision == revision + 1);
            await WaitForSave(model, store);
            var persisted = ConfigValidation.Parse(await File.ReadAllBytesAsync(store.FilePath));
            check("position-user-move-debounced-save", persisted.WindowLeft == saved.Left && persisted.WindowTop == saved.Top && store.SavedRevision == model.Revision);
            revision = model.Revision;
            writes = writer.Writes;
            window.BeginSizeMove(); window.CompleteSizeMove();
            await model.FlushAsync();
            check("position-unchanged-move-does-not-save", model.Revision == revision && store.SavedRevision == revision && writer.Writes == writes);

            foreach (string source in new[] { "hotkey", "tray", "drop" })
            {
                window.HidePanel(); Move(new(monitor.WorkArea.Left, monitor.WorkArea.Top));
                window.ShowPanel(source, forDrop: source == "drop");
                await Settle();
                check($"position-{source}-show-restores-saved-rect", Bounds() == saved && model.Revision == revision);
            }
            window.HidePanel();
            await window.PrewarmAsync();
            check("position-prewarm-does-not-save", model.WindowPosition == saved.Location && model.Revision == revision && !window.IsVisible);
            window.ShowPanel("position-after-prewarm", forDrop: true);
            await Settle();
            check("position-prewarm-then-show-restores-saved-rect", Bounds() == saved);
            window.HidePanel();
            var warming = window.PrewarmAsync();
            window.ShowPanel("position-interrupt-prewarm", forDrop: true);
            await warming; await Settle();
            check("position-interrupted-prewarm-keeps-real-show", window.IsVisible && Bounds() == saved && model.Revision == revision);

            window.BeginSizeMove(); window.MarkSizing();
            NativeMethods.SetWindowPos(hwnd, 0, (int)saved.Left + 8, (int)saved.Top + 8, (int)saved.Width - 24, (int)saved.Height + 16, 0x0014);
            window.CompleteSizeMove();
            await Settle();
            saved = Bounds();
            check("position-user-resize-saves-size-and-top-left", model.WindowPosition == saved.Location && model.WindowSize is not null);

            settings = new SettingsWindow(model, store.DirectoryPath, "", _ => ("", false),
                new StartupService(Environment.ProcessPath!, store.DirectoryPath), window.ResetWindowSize, () => { }) { Owner = window };
            settings.FollowMouse.IsChecked = true;
            await Settle();
            check("position-settings-cursor-live-applies", model.Placement == "cursor" && Bounds() == DefaultBounds() && model.WindowPosition == saved.Location);
            await WaitForSave(model, store);
            check("position-settings-cursor-persists", ConfigValidation.Parse(await File.ReadAllBytesAsync(store.FilePath)).Placement == "cursor");
            settings.LastPosition.IsChecked = true;
            await Settle();
            check("position-settings-last-position-live-applies", model.Placement == "lastPosition" && Bounds() == saved);
            await WaitForSave(model, store);
            check("position-settings-default-is-omitted", !File.ReadAllText(store.FilePath).Contains("\"placement\"", StringComparison.Ordinal));

            var originalDesktop = desktop;
            var otherMonitor = new WindowPlacement.Monitor(new(1000000, 0, 1920, 1080), new(1000000, 0, 1920, 1040), 96);
            desktop = new([.. desktop.Monitors, otherMonitor], new(1000500, 300));
            settings.ResetSizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            desktop = originalDesktop;
            check("position-reset-clears-both-and-moves-visible-panel", window.IsVisible && model.WindowPosition is null && model.WindowSize is null
                && Bounds() == DefaultBounds() && Bounds().Location != saved.Location);
            await WaitForSave(model, store);
            persisted = ConfigValidation.Parse(await File.ReadAllBytesAsync(store.FilePath));
            check("position-reset-persists-cleared-size-and-position", persisted.WindowLeft is null && persisted.WindowTop is null
                && persisted.WindowWidth is null && persisted.WindowHeight is null);
            revision = model.Revision;
            settings.ResetSizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("position-repeated-reset-does-not-save", model.Revision == revision);

            await CheckReadOnly(main, store, desktop, check);
        }
        finally
        {
            settings?.Close(); WindowSmoke.Pump(); window.PrepareExit(); window.Close(); WindowSmoke.Pump();
            await model.FlushAsync(); model.Suspend(true);
        }
    }

    private static async Task WaitForSave(MainViewModel model, ConfigService store)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (store.SavedRevision != model.Revision && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    }

    private static async Task CheckReadOnly(LauncherWindow main, ConfigService writer, WindowPlacement.Desktop desktop, Action<string, bool> check)
    {
        byte[] before = await File.ReadAllBytesAsync(writer.FilePath);
        await using var store = ConfigService.Open(writer.DirectoryPath, readOnly: true);
        var model = new MainViewModel(store);
        var window = new LauncherWindow(main.Log, model, main.Icons) { GetPlacementDesktop = _ => desktop };
        var settings = new SettingsWindow(model, store.DirectoryPath, "", _ => ("", false),
            new StartupService(Environment.ProcessPath!, store.DirectoryPath), window.ResetWindowSize, () => { });
        try
        {
            window.InitializeNative(); window.ShowPanel("position-read-only", forDrop: true);
            var work = desktop.Monitors[0].WorkArea;
            window.BeginSizeMove(); window.MarkSizing();
            nint hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.GetWindowRect(hwnd, out var rect);
            NativeMethods.SetWindowPos(hwnd, 0, (int)work.Left + 24, (int)work.Top + 24, rect.Right - rect.Left - 16, rect.Bottom - rect.Top + 16, 0x0014);
            window.CompleteSizeMove();
            var saved = model.WindowPosition;
            var size = model.WindowSize;
            window.HidePanel(); window.ShowPanel("position-read-only-reshow", forDrop: true);
            NativeMethods.GetWindowRect(hwnd, out rect);
            check("position-read-only-keeps-session-size-and-position", model.Revision == 0 && size is not null && saved == new Point(rect.Left, rect.Top));
            settings.FollowMouse.IsChecked = true;
            settings.ResetSizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await model.FlushAsync();
            check("position-read-only-disables-settings-and-never-writes", !settings.Placements.IsEnabled && !settings.ResetSizeButton.IsEnabled
                && model.Placement == "lastPosition" && model.WindowPosition == saved && model.WindowSize == size
                && before.SequenceEqual(File.ReadAllBytes(writer.FilePath)));
        }
        finally { settings.Close(); WindowSmoke.Pump(); window.PrepareExit(); window.Close(); WindowSmoke.Pump(); model.Suspend(true); }
    }
}
