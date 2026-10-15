using System.Collections;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Xunit;

namespace QuickLaunch.Tests;

// WPF permits only one Application per AppDomain. Share its dispatcher across UI tests;
// never start App.OnStartup (tray, hotkey, profile, or external network).
internal static class WpfTestHost
{
    private static readonly Lazy<Task<Dispatcher>> DispatcherTask = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App();
                application.InitializeComponent();
                ready.SetResult(application.Dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return ready.Task;
    });

    internal static async Task RunAsync(Func<Task> action)
    {
        var dispatcher = await DispatcherTask.Value;
        await dispatcher.InvokeAsync(async () =>
        {
            var theme = ApplicationThemeManager.GetAppTheme();
            var resources = Application.Current.Resources;
            var dictionaries = resources.MergedDictionaries.ToArray();
            var entries = resources.Cast<DictionaryEntry>().ToArray();
            try { await action(); }
            finally
            {
                try { ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false); }
                finally
                {
                    // Apply replaces the theme dictionary; accent updates also add local
                    // overrides. Restore the original instances, order and values, even on failure.
                    // Do not Clear the application dictionary: validating all deferred
                    // style references at once can re-enter resource resolution in WPF.
                    var keys = entries.Select(entry => entry.Key).ToHashSet();
                    foreach (var key in resources.Keys.Cast<object>().Where(key => !keys.Contains(key)).ToArray()) resources.Remove(key);
                    resources.MergedDictionaries.Clear();
                    foreach (var dictionary in dictionaries) resources.MergedDictionaries.Add(dictionary);
                    foreach (var entry in entries)
                        if (!ReferenceEquals(resources[entry.Key], entry.Value)) resources[entry.Key] = entry.Value;
                    Application.Current.Resources = resources;
                }
            }
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(45));
    }
}

// Application resources and the theme manager are process-global, including for other
// collections that create WPF controls without using this dispatcher.
[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfTestCollection { }
