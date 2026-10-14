using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using QuickLaunch.Interop;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class LauncherAppearanceSmoke
{
    public static async Task RunAsync(LauncherWindow window, string directory, string profileDirectory, Action<string, bool> check)
    {
        window.SetSettingsOpen(true);
        var resources = Application.Current.Resources;
        var oldBrushes = resources.Keys.OfType<string>().Where(k => resources[k] is SolidColorBrush || k == "PopupShadowOpacity").ToDictionary(k => k, k => resources[k]);
        var preferences = ThemeService.ReadPreferences();
        nint hwnd = new WindowInteropHelper(window).Handle;
        try
        {
            foreach (bool dark in new[] { true, false })
            {
                ThemeService.ApplyPalette(resources, dark, Colors.Teal);
                window.ApplyBackdrop(dark, preferences.HighContrast, preferences.Transparency);
                await BenchmarkRunner.NextFrame();
                bool nativeDark = NativeMethods.DwmGetWindowAttribute(hwnd, 20, out int immersive, sizeof(int)) >= 0;
                check($"theme-{(dark ? "dark" : "light")}-immersive-attribute", !nativeDark || immersive == (dark ? 1 : 0));
                if (window.BackdropMode == "acrylic")
                {
                    check("acrylic-native-backdrop-and-transparent-surfaces", NativeMethods.DwmGetWindowAttribute(hwnd, 38, out int backdrop, sizeof(int)) >= 0
                        && backdrop == 3 && window.Background == Brushes.Transparent && window.CaptureRoot.Background == Brushes.Transparent
                        && HwndSource.FromHwnd(hwnd)?.CompositionTarget.BackgroundColor == Colors.Transparent);
                }
                else check("solid-theme-resource-reaches-window", window.Background == resources["WindowBrush"]);
                WindowLayoutSmoke.CheckResizeDirections(window, check);
                PrototypeSmoke.SaveSnapshot(window, Path.Combine(directory, dark ? "window.png" : "window-light.png"));
            }
            window.ApplyBackdrop(false, false, false);
            check("transparency-off-solid-fallback", window.BackdropMode == "solid" && window.Background == resources["WindowBrush"]);
            WindowLayoutSmoke.CheckResizeDirections(window, check);
            ThemeService.ApplyPalette(resources, false, Colors.Teal, true);
            window.ApplyBackdrop(false, true, true);
            check("high-contrast-solid-fallback", window.BackdropMode == "solid" && window.Background == resources["WindowBrush"]);
        }
        finally
        {
            foreach (var (key, brush) in oldBrushes) resources[key] = brush;
            window.ApplyBackdrop(preferences.Dark, preferences.HighContrast, preferences.Transparency);
        }

        await DialogAppearanceSmoke.RunAsync(window, directory, profileDirectory, check);
        await CheckLiveIconSize(window, profileDirectory, check);

        var selected = window.SelectedGroup;
        window.SelectGroup(window.Groups.First(g => g.Items.Count == 0));
        window.UpdateLayout();
        Point empty = window.Viewer.TranslatePoint(new Point(window.Viewer.ViewportWidth / 2, window.Viewer.ViewportHeight / 2), window.CaptureRoot);
        check("empty-group-drop-resolves-to-zero", window.SlotAt(empty) == 0 && window.ItemAt(empty) is null);
        check("empty-group-hint-visible-and-click-through", LauncherWindow.Descendants<TextBlock>(window.CaptureRoot)
            .Any(t => t.Text == "拖入文件、文件夹或网址" && t.IsVisible && !t.IsHitTestVisible));
        window.SelectGroup(selected);

        var extraGroups = Enumerable.Range(1, 10).Select(i => window.ViewModel.AddGroup($"较长的分组名称 {i}")).ToArray();
        window.UpdateLayout();
        window.TabsViewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        window.UpdateLayout();
        check("tabs-wheel-scrolls-horizontally-without-scrollbar", window.TabsViewport.HorizontalOffset > 0
            && window.TabsViewport.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed);
        window.AddGroupButton.BringIntoView(); window.UpdateLayout();
        Point add = window.AddGroupButton.TranslatePoint(new Point(16, 16), window.CaptureRoot);
        check("scrolled-add-group-drop-target", window.IsAddGroupAt(add) && window.GroupAt(add) is null);
        foreach (var group in extraGroups) window.ViewModel.RemoveGroup(group);
        window.SelectGroup(selected);

        Size size = new(window.Width, window.Height);
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        window.SetStatus("测试提示"); window.UpdateLayout();
        Point toastPoint = window.Status.TranslatePoint(new Point(window.Status.ActualWidth / 2, window.Status.ActualHeight / 2), window.CaptureRoot);
        var throughToast = window.ItemAt(toastPoint);
        window.Toast.Visibility = Visibility.Collapsed;
        check("toast-surface-clicks-and-drops-reach-tile", throughToast is not null && window.ItemAt(toastPoint) == throughToast && window.SlotAt(toastPoint) is not null);
        window.SetStatus("普通提示");
        window.SetStatus("条目已更新，将自动保存。", toast: false);
        check("silent-status-keeps-existing-toast-and-latest-text", window.Status.Text == "普通提示" && window.StatusText == "条目已更新，将自动保存。");
        await Task.Delay(2200);
        window.SetStatus("更新提示");
        await Task.Delay(2200);
        check("toast-restarts-expiry", window.Toast.IsVisible && window.StatusText == "更新提示");
        await Task.Delay(2100);
        await WaitForToastToHide(window);
        check("toast-hides-after-four-seconds-and-keeps-last-text", !window.Toast.IsVisible && window.StatusText == "更新提示");
        window.SetStatus("模拟失败");
        await Task.Delay(4300);
        check("error-toast-lasts-longer-than-four-seconds", window.Toast.IsVisible);
        await Task.Delay(2000);
        await WaitForToastToHide(window);
        check("error-toast-hides-after-six-seconds", !window.Toast.IsVisible);
        window.SetImporting(true); window.SetStatus("正在解析拖入项目…"); window.UpdateLayout();
        Point cancel = window.CancelImportButton.TranslatePoint(new Point(window.CancelImportButton.ActualWidth / 2, window.CancelImportButton.ActualHeight / 2), window.CaptureRoot);
        check("toast-cancel-link-is-hit-testable", LauncherWindow.Ancestor<Button>(window.CaptureRoot.InputHitTest(cancel) as DependencyObject) == window.CancelImportButton);
        await Task.Delay(4300);
        check("import-toast-stays-visible", window.Toast.IsVisible);
        window.SetImporting(false);
        await Task.Delay(4300);
        await WaitForToastToHide(window);
        check("import-toast-expires-after-completion", !window.Toast.IsVisible);
        window.Width = size.Width; window.Height = size.Height;
        await CheckTileSizes(window, directory, profileDirectory, check);
        window.SetSettingsOpen(false);
    }

    private static async Task WaitForToastToHide(LauncherWindow window)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (window.Toast.IsVisible && timeout.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(50);
    }

    private static async Task CheckLiveIconSize(LauncherWindow window, string directory, Action<string, bool> check)
    {
        var model = window.ViewModel;
        int originalIcon = model.IconSize;
        Size? originalSaved = model.WindowSize;
        Size originalSize = new(window.Width, window.Height);
        var group = window.SelectedGroup;
        var added = Enumerable.Range(0, 24).Select(i => new LaunchItem($"尺寸检查 {i}", $"https://example.com/size/{i}", LaunchKind.Url)).ToArray();
        try
        {
            foreach (var item in added) group.Items.Add(item);
            await CheckCompactIconSize(window, added[0], check);
            var saved = new Size(WindowPlacement.MinimumSize(model.CellSize).Width, WindowPlacement.MinimumSize(100).Height);
            model.SetWindowSize(saved); window.Width = saved.Width; window.Height = saved.Height;
            window.UpdateLayout();
            var focused = LauncherWindow.Descendants<Button>(window.GroupHost).Last(b => b.DataContext is LaunchItem item && item.Id == added[^1].Id);
            focused.Focus(); focused.BringIntoView(); window.UpdateLayout();
            bool hasFocus = focused.IsKeyboardFocused;
            check("icon-size-keyboard-focus-acquired", hasFocus);
            foreach (int expected in new[] { 40, 48, 48, 40, 32, 32 })
            {
                int delta = expected >= model.IconSize && expected != 32 ? 120 : -120;
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                window.HandleIconSizeWheel(wheel, ModifierKeys.Control);
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                var panel = LauncherWindow.Descendants<WrapPanel>(window.GroupHost).Single(p => p.IsVisible);
                var tiles = LauncherWindow.Descendants<Button>(panel).Where(b => b.DataContext is LaunchItem).ToArray();
                check($"live-icon-size-{expected}-bindings-and-saved-window", wheel.Handled && model.IconSize == expected
                    && panel.ItemWidth == model.CellSize && panel.ItemHeight == model.CellSize
                    && tiles.All(b => b.Width == model.TileSize && b.Height == model.TileSize)
                    && tiles.All(b => LauncherWindow.Descendants<Image>(b).Single().Parent is Grid icon && icon.ActualWidth == expected && icon.ActualHeight == expected)
                    && Math.Abs(window.ActualWidth - saved.Width) < 1 && Math.Abs(window.ActualHeight - saved.Height) < 1);
                var bounds = new Rect(focused.TranslatePoint(new Point(), window.Viewer), focused.RenderSize);
                check($"live-icon-size-{expected}-keeps-focused-tile-visible", !hasFocus || focused.IsKeyboardFocused
                    && bounds.Top >= -.5 && bounds.Bottom <= window.Viewer.ViewportHeight + .5);
                window.Viewer.ScrollToTop(); window.UpdateLayout();
                int columns = Math.Max(1, (int)(window.Viewer.ViewportWidth / model.CellSize));
                bool aligned = tiles.Take(columns).Select((tile, i) => window.SlotAt(tile.TranslatePoint(new Point(8, 8), window.CaptureRoot)) == i).All(v => v);
                window.ShowInsertion(columns); window.UpdateLayout();
                var marker = (TranslateTransform)window.Insertion.RenderTransform;
                check($"live-icon-size-{expected}-drop-slots-and-insertion", aligned && marker.X == 0 && Math.Abs(marker.Y - model.CellSize - 4) < .5);
                focused.BringIntoView(); window.UpdateLayout();
            }
            var plain = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            window.HandleIconSizeWheel(plain, ModifierKeys.None);
            check("plain-wheel-does-not-resize-icons", !plain.Handled && model.IconSize == 32);
            long revision = model.Revision;
            window.DragSession.BeginPendingItemForDiagnostics(added[^1]);
            var idle = window.DragSession.WhenIdle;
            try
            {
                var dragging = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                window.HandleIconSizeWheel(dragging, ModifierKeys.Control);
                check("ctrl-wheel-ignored-during-drag", window.CanEdit && window.DragSession.Active && !idle.IsCompleted
                    && !dragging.Handled && model.IconSize == 32 && model.Revision == revision);
            }
            finally { window.DragSession.Cancel(); }
            check("diagnostic-pending-drag-cancelled", !window.DragSession.Active && idle.IsCompletedSuccessfully);
            var resumed = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            window.HandleIconSizeWheel(resumed, ModifierKeys.Control);
            check("ctrl-wheel-resumes-after-drag-cancel", resumed.Handled && model.IconSize == 40 && model.Revision == revision + 1);
            model.SetWindowSize(null); model.SetIconSize(48); window.UpdateLayout();
            var auto = WindowPlacement.ResolveSize(null, new(window.MaxWidth, window.MaxHeight), new(window.MinWidth, window.MinHeight),
                model.Columns, model.CellSize, group.Items.Count, WindowPlacement.HorizontalChrome);
            check("live-icon-size-auto-fits-without-saved-size", Math.Abs(window.Width - auto.Width) < 1 && Math.Abs(window.Height - auto.Height) < 1);
            await window.LoadIconsAsync();
            int pixels = IconService.PixelSize(model.IconSize, VisualTreeHelper.GetDpi(window).DpiScaleX);
            check("live-icon-size-reloads-icons-at-current-dpi", group.Items.All(i => i.Visual.RequestedPixels == pixels));
        }
        finally
        {
            window.DragSession.Cancel(); window.ShowInsertion(null);
            foreach (var item in added) group.Items.Remove(item);
            model.SetWindowSize(originalSaved); model.SetIconSize(originalIcon);
            window.Width = originalSize.Width; window.Height = originalSize.Height;
            window.Viewer.ScrollToTop();
        }
        await using var readOnlyStore = ConfigService.Open(Path.Combine(directory, "size-read-only"), readOnly: true);
        var readOnlyModel = new MainViewModel(readOnlyStore);
        var readOnly = new LauncherWindow(window.Log, readOnlyModel, window.Icons);
        try
        {
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            readOnly.HandleIconSizeWheel(wheel, ModifierKeys.Control);
            check("read-only-ctrl-wheel-no-op", !wheel.Handled && readOnlyModel.IconSize == 32 && readOnlyModel.Revision == 0);
        }
        finally { readOnlyModel.Suspend(true); readOnly.PrepareExit(); readOnly.Close(); }
    }

    private static async Task CheckCompactIconSize(LauncherWindow window, LaunchItem item, Action<string, bool> check)
    {
        var model = window.ViewModel;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        async Task<bool> WaitForImage(int size)
        {
            int pixels = IconService.PixelSize(size, scale);
            bool Ready() => item.Visual.RequestedPixels == pixels && item.Visual.Image is { } image
                && image.PixelWidth == pixels && image.PixelHeight == pixels;
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!Ready() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            return Ready();
        }
        model.SetIconSize(24);
        check("compact-icon-size-24-loaded", await WaitForImage(24));
        double cell = model.CellSize;
        bool requestedOnIconChange = false;
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Observe before the later CellSize notification can mask a missing IconSize handler.
            if (e.PropertyName == nameof(MainViewModel.IconSize))
                requestedOnIconChange = item.Visual.RequestedPixels == IconService.PixelSize(32, scale);
        }
        model.PropertyChanged += Changed;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        try { window.HandleIconSizeWheel(wheel, ModifierKeys.Control); }
        finally { model.PropertyChanged -= Changed; }
        check("icon-size-24-to-32-reloads-without-cell-size-change", await WaitForImage(32)
            && requestedOnIconChange && wheel.Handled && model.IconSize == 32 && cell == 84 && model.CellSize == cell);
    }

    private static async Task CheckTileSizes(LauncherWindow main, string directory, string profileDirectory, Action<string, bool> check)
    {
        foreach (int iconSize in new[] { 32, 40, 48 })
        {
            string profile = Path.Combine(profileDirectory, "appearance", iconSize.ToString());
            Directory.CreateDirectory(profile);
            new AtomicConfigWriter().Write(Path.Combine(profile, "config.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                AppConfig.CreateDefault() with { IconSize = iconSize }, ConfigJsonContext.Default.AppConfig));
            await using var store = ConfigService.Open(profile);
            var model = new MainViewModel(store);
            model.Groups[0].Items[0] = model.Groups[0].Items[0] with { Name = "很长的应用程序名称用于验证两行省略号", ErrorMessage = "模拟失败" };
            var result = new LaunchResult(true, false, "模拟启动成功");
            var probe = new LauncherWindow(main.Log, model, main.Icons, (_, _) => Task.FromResult(result));
            try
            {
                check($"normal-startup-silent-{iconSize}", probe.StatusText == "" && probe.Toast.Visibility == Visibility.Collapsed);
                probe.InitializeNative(); probe.ShowPanel("tile-layout-smoke", forDrop: true);
                await BenchmarkRunner.NextFrame(); probe.UpdateLayout();
                var tiles = LauncherWindow.Descendants<Button>(probe.GroupHost).Where(b => b.DataContext is LaunchItem).ToArray();
                check($"labels-fit-two-lines-{iconSize}", tiles.All(tile =>
                {
                    var label = LauncherWindow.Descendants<TextBlock>(tile).Single(t => t.Name == "TileLabel");
                    Point top = label.TranslatePoint(new Point(), tile);
                    return label.ActualHeight >= 28 && top.Y >= 0 && top.Y + label.ActualHeight <= tile.ActualHeight
                        && label.ActualWidth <= tile.ActualWidth - 8 && label.TextWrapping == TextWrapping.Wrap;
                }));
                check($"tile-error-opacity-and-no-effects-{iconSize}", Math.Abs(tiles[0].Opacity - .55) < .01
                    && tiles.All(t => t.Effect is null && t.FocusVisualStyle is null));
                PrototypeSmoke.SaveSnapshot(probe, Path.Combine(directory, $"tiles-{iconSize}.png"));
                await probe.LaunchAsync(model.Groups[0].Items[0]);
                check($"successful-launch-is-silent-and-updates-status-{iconSize}", probe.Toast.Visibility == Visibility.Collapsed && probe.StatusText.Contains(result.Message));
                result = new(false, false, "模拟启动失败");
                await probe.LaunchAsync(model.Groups[0].Items[0]);
                check($"failed-launch-shows-toast-{iconSize}", probe.Toast.IsVisible && probe.StatusText.Contains(result.Message));
            }
            finally
            {
                model.Suspend(true); probe.PrepareExit(); probe.Close();
            }
        }
    }
}
