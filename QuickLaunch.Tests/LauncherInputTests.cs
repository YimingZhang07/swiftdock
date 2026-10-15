using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickLaunch.Diagnostics;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;
using Xunit;

namespace QuickLaunch.Tests;

[Collection("WPF")]
public sealed class LauncherInputTests
{
    [Fact]
    public Task SelectionRoutedInputAndTemplatesDoNotDependOnDesktopFocus() => OnSta((window, launches) =>
    {
        LauncherInputSmoke.CheckInput(window, launches, (name, passed) => Assert.True(passed, name));
        LauncherInputSmoke.CheckSelectionNavigation(window, (name, passed) => Assert.True(passed, name));
        LauncherInputSmoke.CheckDragRouting(window, launches, (name, passed) => Assert.True(passed, name));
        CheckPopupCloseWait(window);
    });

    private static void CheckPopupCloseWait(LauncherWindow window)
    {
        // Exercise both lifecycle paths even when this desktop cannot create native popups.
        // These are routed Opened/Closed events, not writes to the launcher's private state.
        var tile = (ListBoxItem)window.SelectedList.ItemContainerGenerator.ContainerFromIndex(0);
        var menu = tile.ContextMenu;
        menu.PlacementTarget = tile;
        using var lifetime = new PopupSmokeScope(menu, window);
        lifetime.Close();
        Assert.False(lifetime.Opened);
        Assert.True(lifetime.Closed);
        Assert.False(PopupSmokeScope.MenuIsOpen(window));

        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(PopupSmokeScope.MenuIsOpen(window));
        bool closed = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); closed = true;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        };
        timer.Start();
        try { lifetime.Close(); }
        finally { timer.Stop(); }
        Assert.True(closed);
        Assert.True(lifetime.Closed);
        Assert.False(PopupSmokeScope.MenuIsOpen(window));

        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        try { Assert.Throws<TimeoutException>(() => lifetime.Close(timeout: TimeSpan.FromMilliseconds(40))); }
        finally { lifetime.Close(() => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent))); }
    }

    private static Task OnSta(Action<LauncherWindow, Func<int>> action) => WpfTestHost.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-input-" + Guid.NewGuid().ToString("N"));
        ConfigService? store = null;
        DiagnosticLog? log = null;
        IconService? icons = null;
        LauncherWindow? window = null;
        MainViewModel? model = null;
        try
        {
            store = ConfigService.Open(directory);
            log = new DiagnosticLog(directory);
            icons = new IconService(Path.Combine(directory, "icons"), log, new FaviconFixtureHandler());
            model = new MainViewModel(store);
            int launches = 0;
            window = new LauncherWindow(log, model, icons, (_, _) =>
            {
                launches++;
                return Task.FromResult(new LaunchResult(true, false, "test launch"));
            });
            ThemeSmokeScope.Apply(true);
            window.GetPlacementDesktop = _ => LauncherInputSmoke.Desktop;
            window.ShowPanel("unit-input");
            action(window, () => launches);
        }
        finally
        {
            model?.Suspend(true); window?.PrepareExit(); window?.Close();
            icons?.Dispose();
            if (store is not null) await store.DisposeAsync();
            if (log is not null) await log.DisposeAsync();
        }
    });
}
