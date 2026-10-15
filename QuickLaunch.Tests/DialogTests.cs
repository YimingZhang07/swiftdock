using System.IO;
using QuickLaunch.Diagnostics;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;
using Xunit;

namespace QuickLaunch.Tests;

[Collection("WPF")]
public sealed class DialogTests
{
    [Fact]
    public Task FluentDialogsThemesAndSettingsLayout() => WithLauncher(DialogAppearanceSmoke.RunAsync);

    [Fact]
    public Task SettingsTogglesModalResultsAndDeleteConfirmation() => WithLauncher(DialogBehaviorSmoke.RunAsync);

    [Fact]
    public Task EditorTypesValidationChipsAdvancedAndIconPicker() => WithLauncher(async (window, directory, profile, check) =>
    {
        await EditItemDialogSmoke.RunAsync(window, directory, profile, check);
        await IconPickerSmoke.RunAsync(window, directory, check);
    });

    private static Task WithLauncher(Func<LauncherWindow, string, string, Action<string, bool>, Task> action) => WpfTestHost.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-dialogs-" + Guid.NewGuid().ToString("N"));
        await using var log = new DiagnosticLog(directory);
        await using var store = ConfigService.Open(Path.Combine(directory, "profile"));
        using var icons = new IconService(Path.Combine(directory, "icons"), log, new FaviconFixtureHandler());
        var model = new MainViewModel(store);
        var window = new LauncherWindow(log, model, icons) { GetPlacementDesktop = _ => LauncherInputSmoke.Desktop };
        var failures = new List<string>();
        try
        {
            ThemeSmokeScope.Apply(true);
            window.InitializeNative(); window.ShowPanel("dialog-test", forDrop: true); WindowSmoke.Pump();
            await action(window, directory, store.DirectoryPath, (name, passed) => { if (!passed) failures.Add(name); });
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { model.Suspend(true); window.PrepareExit(); window.Close(); WindowSmoke.Pump(); }
    });
}
