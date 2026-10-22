using System.Collections;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Xunit;

namespace Swiftdock.Tests;

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
                // Application's constructor queues OnStartup even without Application.Run().
                // A real App enters product startup and can Shutdown/clear Application.Current.
                // Use the snapshot renderer's resource-only approach; App's BAML loader also
                // rejects a test-assembly subclass, so load the embedded App.xaml definitions.
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                _ = System.Reflection.Assembly.Load("Wpf.Ui"); // XML namespace mappings for loose XAML.
                var assembly = typeof(WpfTestHost).Assembly;
                using (var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
                    .Single(name => name.EndsWith(".App.xaml", StringComparison.Ordinal)))!)
                {
                    var root = XDocument.Load(stream).Root!;
                    XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                    var dictionary = root.Element(wpf + "Application.Resources")!.Elements().Single();
                    foreach (var ns in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                        dictionary.SetAttributeValue(ns.Name, ns.Value);
                    application.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString(), new ParserContext
                        { BaseUri = new Uri("pack://application:,,,/Swiftdock;component/App.xaml") });
                }
                // Publish readiness only after the base Application's queued startup has run.
                application.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    () => ready.SetResult(application.Dispatcher));
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
                try
                {
                    // Accent updates add app-level overrides. Restore those BEFORE rebuilding
                    // the theme, otherwise derived brushes can resolve the ending test's palette.
                    // Do not Clear the application dictionary: validating all deferred
                    // style references at once can re-enter resource resolution in WPF.
                    var keys = entries.Select(entry => entry.Key).ToHashSet();
                    foreach (var key in resources.Keys.Cast<object>().Where(key => !keys.Contains(key)).ToArray()) resources.Remove(key);
                    foreach (var entry in entries)
                        if (!ReferenceEquals(resources[entry.Key], entry.Value)) resources[entry.Key] = entry.Value;
                    resources.MergedDictionaries.Clear();
                    foreach (var dictionary in dictionaries) resources.MergedDictionaries.Add(dictionary);
                }
                finally
                {
                    Application.Current.Resources = resources;
                    // The saved dictionaries can contain already-resolved brushes. Reload last
                    // to restore both WPF-UI's cached theme and its palette-derived resources.
                    ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
                }
            }
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(45));
    }
}

// Application resources and the theme manager are process-global, including for other
// collections that create WPF controls without using this dispatcher.
[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfTestCollection { }
