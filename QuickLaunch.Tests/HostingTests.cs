using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;
using Xunit;

namespace QuickLaunch.Tests;

[Collection("WPF")]
public sealed class HostingTests
{
    [Fact]
    public Task LauncherGraphIsLazySharedAndDisposedBeforeInstanceLease() => WpfTestHost.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-host-" + Guid.NewGuid().ToString("N"));
        var instance = new SingleInstanceService(directory, () => { });
        var writer = new MemoryWriter();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        Assert.All(builder.Configuration.Sources, source => Assert.IsType<Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource>(source));
        builder.Services.AddLauncher(new(directory, Path.Combine(directory, "logs"), true), false, true, null,
            Application.Current.Dispatcher, instance, () => { }, () => { });
        builder.Services.AddSingleton<IConfigWriter>(writer);
        builder.Services.AddIconServices(Path.Combine(directory, "icons"), () => new FaviconFixtureHandler());
        var host = builder.Build();
        try
        {
            Assert.Same(instance, host.Services.GetRequiredService<SingleInstanceService>());
            await host.StartAsync();
            Assert.False(Directory.Exists(directory)); // Build/start cannot construct the heavy graph.
            Assert.Empty(host.Services.GetServices<IHostedService>());
            var window = host.Services.GetRequiredService<LauncherWindow>();
            var model = host.Services.GetRequiredService<MainViewModel>();
            var store = host.Services.GetRequiredService<ConfigService>();
            Assert.Same(window, host.Services.GetRequiredService<LauncherWindow>());
            Assert.Same(model, window.ViewModel);
            Assert.Same(host.Services.GetRequiredService<LauncherViewModel>(), window.Launcher);
            Assert.Same(model, window.Launcher.State);
            Assert.Same(window.Launcher, window.DataContext);
            Assert.Same(window.Icons, host.Services.GetRequiredService<IconService>());
            Assert.NotNull(host.Services.GetRequiredService<FaviconService>());
            Assert.NotNull(host.Services.GetRequiredService<ThemeService>());
            Assert.NotNull(host.Services.GetRequiredService<StartupService>());
            Assert.Same(host.Services.GetRequiredService<HotkeyService>(), host.Services.GetRequiredService<HotkeyService>());
            var dialogs = host.Services.GetRequiredService<IDialogService>();
            Assert.Same(dialogs, host.Services.GetRequiredService<IDialogService>());
            using (var firstScope = host.Services.CreateScope())
            using (var secondScope = host.Services.CreateScope())
            {
                var first = firstScope.ServiceProvider.GetRequiredService<NameDialog>();
                var second = secondScope.ServiceProvider.GetRequiredService<NameDialog>();
                Assert.NotSame(first, second); Assert.NotSame(first.ViewModel, second.ViewModel);
                first.Close(); second.Close();
                var editor = firstScope.ServiceProvider.GetRequiredService<EditItemDialog>();
                Assert.Same(firstScope.ServiceProvider.GetRequiredService<EditItemViewModel>(), editor.ViewModel);
                Assert.Same(firstScope.ServiceProvider.GetRequiredService<IFilePicker>(), firstScope.ServiceProvider.GetRequiredService<DialogFilePicker>());
                editor.Close();
                var settings = firstScope.ServiceProvider.GetRequiredService<SettingsWindow>();
                Assert.Same(firstScope.ServiceProvider.GetRequiredService<SettingsViewModel>(), settings.ViewModel);
                settings.Close();
            }
            bool closedOnDispatcher = false, primaryWhileClosing = false;
            window.Closed += (_, _) =>
            {
                closedOnDispatcher = window.Dispatcher.CheckAccess();
                var contender = new SingleInstanceService(directory, () => { });
                primaryWhileClosing = !contender.IsPrimary;
                contender.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
            model.Suspend(true);
            await model.FlushAsync();
            Assert.NotNull(writer.Bytes);
            await host.StopAsync();
            await ((IAsyncDisposable)host).DisposeAsync();
            Assert.True(closedOnDispatcher); Assert.True(primaryWhileClosing);
            Assert.Throws<ObjectDisposedException>(() => window.Icons.GetAsync(model.Groups[0].Items[0], 48).GetAwaiter().GetResult());
            await using var replacement = new SingleInstanceService(directory, () => { });
            Assert.True(replacement.IsPrimary);
            using var lease = File.Open(store.FilePath.Replace("config.json", "config.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { await ((IAsyncDisposable)host).DisposeAsync(); }
    });

    private sealed class MemoryWriter : IConfigWriter
    {
        internal byte[]? Bytes { get; private set; }
        public void Write(string path, byte[] bytes) => Bytes = bytes;
    }
}
