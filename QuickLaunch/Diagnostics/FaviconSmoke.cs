using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Diagnostics;

internal static class FaviconSmoke
{
    internal static async Task RunAsync(LauncherWindow main, string directory, string profileDirectory, Action<string, bool> check)
    {
        await using var store = ConfigService.Open(Path.Combine(profileDirectory, "favicons"));
        var model = new MainViewModel(store);
        model.Groups[0].Items.Clear(); model.Groups[1].Items.Clear();
        model.Groups[0].Name = "网站图标"; model.SetIconSize(48);
        var samples = new[]
        {
            new LaunchItem("大图标", "https://large.test/page", LaunchKind.Url),
            new LaunchItem("16 px 图标", "https://tiny.test/page", LaunchKind.Url),
            new LaunchItem("获取失败", "https://fail.test/page", LaunchKind.Url),
            new LaunchItem("自定义优先", "https://medium.test/page", LaunchKind.Url) { Icon = "glyph:E735:#B7791F" }
        };
        foreach (var item in samples) model.Groups[0].Items.Add(item);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FaviconFixtureHandler(async (request, token) =>
        {
            await release.Task.WaitAsync(token); return FaviconFixtureHandler.Respond(request.RequestUri!);
        });
        using var icons = new IconService(Path.Combine(store.DirectoryPath, "icons"), main.Log, handler);
        var window = new LauncherWindow(main.Log, model, icons);
        var settings = new SettingsWindow(model, store.DirectoryPath, "", _ => ("", false),
            new StartupService(Environment.ProcessPath!, store.DirectoryPath), window.ResetWindowSize, () => { });
        using var themeScope = new ThemeSmokeScope();
        try
        {
            ThemeSmokeScope.Apply(true);
            window.InitializeNative(); window.ApplyBackdrop(false, false);
            window.ShowPanel("favicon-smoke", forDrop: true);
            await window.LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(2));
            int pixels = IconService.PixelSize(model.IconSize, VisualTreeHelper.GetDpi(window).DpiScaleX);
            var globe = IconService.Globe(pixels);
            check("favicon-globe-before-network-completes", samples.Take(3).All(i => ReferenceEquals(i.Visual.Image, globe)));
            release.SetResult();
            await Task.WhenAll(samples.Select(i => icons.GetFaviconAsync(i, pixels))).WaitAsync(TimeSpan.FromSeconds(5));
            await BenchmarkRunner.NextFrame();
            check("favicon-arrival-updates-existing-visual", samples.Take(2).All(i => i.Visual.Image is { IsFrozen: true } image && !ReferenceEquals(image, globe)));
            check("favicon-failure-keeps-globe", ReferenceEquals(samples[2].Visual.Image, globe));
            check("favicon-custom-icon-skips-network", handler.Requests.All(u => u.Host != "medium.test"));
            window.UpdateLayout();
            PrototypeSmoke.SaveSnapshot(window, Path.Combine(directory, "launcher-favicons.png"));

            int requests = handler.Requests.Count;
            settings.WebsiteIconsBox.IsChecked = false;
            settings.WebsiteIconsBox.RaiseEvent(new RoutedEventArgs(Wpf.Ui.Controls.ToggleSwitch.ClickEvent));
            await window.LoadIconsAsync(); await BenchmarkRunner.NextFrame();
            check("favicon-setting-off-restores-globes-with-zero-requests", !model.AutoFetchWebsiteIcons && samples.Take(3).All(i => ReferenceEquals(i.Visual.Image, globe))
                && handler.Requests.Count == requests);
            await model.FlushAsync();
            check("favicon-setting-persists-opt-out", ConfigValidation.Parse(File.ReadAllBytes(store.FilePath)).AutoFetchWebsiteIcons == false);
            settings.WebsiteIconsBox.IsChecked = true;
            settings.WebsiteIconsBox.RaiseEvent(new RoutedEventArgs(Wpf.Ui.Controls.ToggleSwitch.ClickEvent));
            await BenchmarkRunner.NextFrame();
            check("favicon-setting-on-reuses-cache-lazily", !ReferenceEquals(samples[0].Visual.Image, globe) && handler.Requests.Count == requests);
            window.RefreshItemIcon(samples[0]);
            await icons.GetFaviconAsync(samples[0], pixels).WaitAsync(TimeSpan.FromSeconds(5));
            await BenchmarkRunner.NextFrame();
            check("favicon-refresh-menu-refetches-origin", handler.Requests.Count == requests + 2 && !ReferenceEquals(samples[0].Visual.Image, globe));
            check("favicon-setting-default-omitted", !System.Text.Json.JsonSerializer.Serialize(model.Snapshot(), ConfigJsonContext.Default.AppConfig)
                .Contains("autoFetchWebsiteIcons", StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult(); settings.Close(); WindowSmoke.Pump(); window.PrepareExit(); window.Close(); WindowSmoke.Pump(); model.Suspend(true);
            themeScope.Dispose();
        }
    }
}
