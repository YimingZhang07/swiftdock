using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using QuickLaunch.Diagnostics;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class LauncherInputTests
{
    [Theory]
    [InlineData(Key.Left, ModifierKeys.None, true)]
    [InlineData(Key.Right, ModifierKeys.None, true)]
    [InlineData(Key.Up, ModifierKeys.None, true)]
    [InlineData(Key.Down, ModifierKeys.None, true)]
    [InlineData(Key.Home, ModifierKeys.None, true)]
    [InlineData(Key.End, ModifierKeys.None, true)]
    [InlineData(Key.PageUp, ModifierKeys.None, true)]
    [InlineData(Key.PageDown, ModifierKeys.None, true)]
    [InlineData(Key.Tab, ModifierKeys.None, true)]
    [InlineData(Key.Tab, ModifierKeys.Shift, true)]
    [InlineData(Key.Tab, ModifierKeys.Control, true)]
    [InlineData(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, true)]
    [InlineData(Key.D1, ModifierKeys.Control, true)]
    [InlineData(Key.D9, ModifierKeys.Control, true)]
    [InlineData(Key.D1, ModifierKeys.None, false)]
    [InlineData(Key.D0, ModifierKeys.Control, false)]
    [InlineData(Key.LeftAlt, ModifierKeys.Alt, false)]
    [InlineData(Key.LeftCtrl, ModifierKeys.Control, false)]
    [InlineData(Key.LeftShift, ModifierKeys.Shift, false)]
    [InlineData(Key.LWin, ModifierKeys.Windows, false)]
    [InlineData(Key.System, ModifierKeys.Alt, false)]
    [InlineData(Key.Space, ModifierKeys.Alt, false)]
    [InlineData(Key.Tab, ModifierKeys.Alt, false)]
    [InlineData(Key.Left, ModifierKeys.Windows, false)]
    [InlineData(Key.Enter, ModifierKeys.None, false)]
    [InlineData(Key.Delete, ModifierKeys.None, false)]
    [InlineData(Key.F2, ModifierKeys.None, false)]
    public void OnlyPanelNavigationEnablesCues(Key key, ModifierKeys modifiers, bool expected) =>
        Assert.Equal(expected, LauncherWindow.IsKeyboardNavigation(key, modifiers));

    [Fact]
    public Task RoutedInputAndTemplatesDoNotDependOnDesktopFocus() => OnSta((window, launches) =>
        LauncherInputSmoke.CheckInput(window, launches, (name, passed) => Assert.True(passed, name)));

    [Fact]
    public Task ContainerFocusRecoveryMessagesAndScrollingDoNotDependOnDesktopFocus() => OnSta((window, _) =>
        LauncherInputSmoke.CheckFocusRouting(window, (name, passed) => Assert.True(passed, name)));

    private static async Task OnSta(Action<LauncherWindow, Func<int>> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
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
                ThemeService.ApplyPalette(window.Resources, true, Colors.Teal);
                window.GetPlacementDesktop = _ => LauncherInputSmoke.Desktop;
                window.ShowPanel("unit-input", forDrop: true);
                action(window, () => launches);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally
            {
                model?.Suspend(true); window?.PrepareExit(); window?.Close();
                icons?.Dispose();
                store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                log?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
