using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Snapshots;

internal static class ImageRenderer
{
    internal static int Run(string output, SnapshotImage image)
    {
        Console.Error.WriteLine("Phase: resources");
        int result = 0;
        // Resources only: no product startup, tray, hotkey or modal service call.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _ = System.Reflection.Assembly.Load("Wpf.Ui"); // Make its XML namespace mappings available to loose XAML.
        // App's BAML loader requires its exact concrete type; constructing App would
        // queue product startup. Load the same resource definitions, embedded at build.
        using (var stream = typeof(ImageRenderer).Assembly.GetManifestResourceStream("SnapshotApp.xaml")!)
        {
            var root = XDocument.Load(stream).Root!;
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var dictionary = root.Element(wpf + "Application.Resources")!.Elements().Single();
            foreach (var ns in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                dictionary.SetAttributeValue(ns.Name, ns.Value);
            application.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString(), new ParserContext
                { BaseUri = new Uri("pack://application:,,,/QuickLaunch;component/App.xaml") });
        }
        application.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception); e.Handled = true; result = 1; application.Shutdown();
        };
        application.Dispatcher.BeginInvoke(async () =>
        {
            Console.Error.WriteLine("Phase: renderer dispatcher");
            try { await RenderAsync(output, image); }
            catch (Exception error) { Console.Error.WriteLine($"{image.File}: {error}"); result = 1; }
            finally { Console.Error.WriteLine("Phase: shutdown"); application.Shutdown(); }
        });
        application.Run(); // Own the dispatcher so Application.Shutdown also ends the message loop.
        Console.Error.WriteLine("Phase: finished");
        return result;
    }

    private static async Task RenderAsync(string output, SnapshotImage image)
    {
        Console.Error.WriteLine("Phase: composition");
        string profile = Path.Combine(output, "profiles", "snapshots-" + Guid.NewGuid().ToString("N"));
        await using var store = ConfigService.Open(profile, writer: new RenderConfigWriter());
        using var state = new MainViewModel(store);
        state.SetAutoFetchWebsiteIcons(false);
        state.SetWindowSize(new Size(620, 350));
        state.SetTheme(image.Theme); state.SetAccent(image.Accent); state.SetBackdrop(image.Backdrop);
        var group = state.Groups[0];
        group.Items.Clear();
        foreach (var (glyph, index) in IconCatalog.AvailableGlyphs.Take(18).Select((glyph, index) => (glyph, index)))
            group.Items.Add(new LaunchItem(glyph.Name, "https://example.com/" + index, LaunchKind.Url)
                { Icon = $"glyph:{glyph.CodePoint:X4}:{IconCatalog.Colors[index % IconCatalog.Colors.Count].Hex}" });
        using var appearance = new ThemeService(Dispatcher.CurrentDispatcher, state);
        using var services = new ServiceCollection().AddLogging().AddSingleton(appearance)
            .AddIconServices(Path.Combine(profile, "icons")).AddDialogs()
            .AddSingleton<ISettingsState>(state)
            .AddSingleton(new StartupService(Environment.ProcessPath!, profile, "Snapshots", _ => null, (_, _) => { }))
            .AddSingleton<IHotkeyService>(new SnapshotHotkey())
            .AddSingleton<ISettingsActions>(new SettingsActions(() => { }, () => { }, () => Task.CompletedTask))
            .BuildServiceProvider();
        var icons = services.GetRequiredService<IconService>();
        var dialogs = services.GetRequiredService<IDialogService>();
        LauncherWindow? launcherWindow = null;
        using var launcher = new LauncherViewModel(state, new LauncherDialogs(dialogs, () => launcherWindow!),
            new LauncherActions(icons, (_, _) => Task.FromResult(new LaunchResult(true, false, "Snapshot"))),
            NullLogger<LauncherViewModel>.Instance);
        launcherWindow = new LauncherWindow(NullLogger<LauncherWindow>.Instance, launcher, icons, dialogs)
            { Width = 620, Height = 350 };
        var opened = new List<Window> { launcherWindow };
        HwndSource? menuSource = null;
        using var scope = services.CreateScope();
        Window Open(Window window)
        {
            opened.Add(window);
            OffscreenWindow.Prepare(window);
            appearance.TrackDialog(window);
            OffscreenWindow.Show(window);
            return window;
        }
        try
        {
            Console.Error.WriteLine("Phase: launcher initialization");
            OffscreenWindow.Prepare(launcherWindow);
            launcherWindow.InitializeNative(appearance);
            Console.Error.WriteLine("Phase: launcher show");
            // ShowPanel places on a monitor and explicitly activates; never call it here.
            OffscreenWindow.Show(launcherWindow);
            Console.Error.WriteLine("Phase: icons");
            await launcherWindow.LoadIconsAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (image.Selected) launcher.SelectionFor(group).Item = group.Items[1];
            FrameworkElement target;
            bool overflow = false;
            Console.Error.WriteLine("Phase: " + image.Kind);
            switch (image.Kind)
            {
                case "launcher": target = launcherWindow; break;
                case "settings": target = Open(scope.ServiceProvider.GetRequiredService<SettingsWindow>()); break;
                case "name-dialog":
                    scope.ServiceProvider.GetRequiredService<NameDialogViewModel>().Initialize("新建分组", "新分组");
                    target = Open(scope.ServiceProvider.GetRequiredService<NameDialog>());
                    break;
                case "add-dialog":
                case "edit-dialog":
                case "icon-picker":
                    var model = scope.ServiceProvider.GetRequiredService<EditItemViewModel>();
                    model.Initialize(image.Kind == "add-dialog" ? null : group.Items[0]);
                    var editor = (EditItemDialog)Open(scope.ServiceProvider.GetRequiredService<EditItemDialog>());
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await editor.TargetRefresh.WaitAsync(TimeSpan.FromSeconds(3));
                    target = editor;
                    if (image.Kind == "icon-picker")
                    {
                        // Native popups clamp back onto a monitor. Host the actual child
                        // tree offscreen instead; Popup.IsOpen stays false throughout.
                        var surface = editor.PickerSurface;
                        editor.IconPopup.Child = null;
                        surface.DataContext = model;
                        Open(ContentHost(surface, editor));
                        target = surface; overflow = true;
                    }
                    break;
                case "item-menu":
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    launcherWindow.UpdateLayout();
                    var tile = (ListBoxItem)launcherWindow.SelectedList.ItemContainerGenerator.ContainerFromIndex(0);
                    var menu = tile.ContextMenu;
                    tile.ContextMenu = null;
                    menu.PlacementTarget = tile;
                    menu.Visibility = Visibility.Visible;
                    menu.FontFamily = launcherWindow.FontFamily;
                    menuSource = OffscreenWindow.MenuSurface(menu);
                    target = menu; overflow = true;
                    break;
                case "delete-confirm":
                    // Same control/content as DialogService, without calling that service
                    // or ShowDialogAsync: the latter enters a modal loop before returning.
                    target = Open(new Wpf.Ui.Controls.MessageBox
                    {
                        Title = "删除分组", Content = $"删除“{group.Name}”及其中的条目？此操作不能用单条撤销恢复。",
                        PrimaryButtonText = "删除", PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                        CloseButtonText = "取消", ResizeMode = ResizeMode.NoResize, MaxWidth = 460,
                        FontFamily = launcherWindow.FontFamily
                    });
                    break;
                default: throw new InvalidOperationException("Unknown image kind: " + image.Kind);
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            // WPF-UI's effect/decorator entrance animation lasts 167 ms. Capture
            // its settled content, not an intermediate animated clip rectangle.
            await Task.Delay(220);
            Console.Error.WriteLine("Phase: capture");
            Capture(target, Path.Combine(output, image.File), overflow);
        }
        finally
        {
            Console.Error.WriteLine("Phase: cleanup");
            state.Suspend(true); launcherWindow.PrepareExit();
            menuSource?.Dispose();
            // Base Window.Close is non-modal, including for MessageBox. A blocked
            // cleanup is still bounded by the external six-second worker deadline.
            foreach (Window window in opened.AsEnumerable().Reverse()) window.Close();
        }
    }

    private static Window ContentHost(FrameworkElement content, Window source) => new()
    {
        Content = content, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize, FontFamily = source.FontFamily, Foreground = source.Foreground,
        Background = (Brush)source.FindResource("ApplicationBackgroundBrush"), UseLayoutRounding = true
    };

    private static void Capture(FrameworkElement element, string path, bool overflow)
    {
        if (element is ContextMenu)
        {
            // Bindings/templates can change desired size during the dispatcher drain.
            // This detached tree has no parent layout pass to resize its arrange slot.
            element.InvalidateMeasure();
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new Rect(element.DesiredSize));
        }
        element.UpdateLayout();
        var bounds = new Rect(element is ContextMenu ? element.DesiredSize : element.RenderSize);
        if (overflow) bounds.Union(VisualTreeHelper.GetDescendantBounds(element));
        Console.Error.WriteLine($"Render: {element.GetType().Name}; size={element.RenderSize}; desired={element.DesiredSize}; bounds={bounds}; visible={element.IsVisible}; opacity={element.Opacity}");
        var image = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle((Brush)element.FindResource("ApplicationBackgroundBrush"), null, new Rect(bounds.Size));
            if (overflow) drawing.DrawRectangle(new VisualBrush(element) { AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, new Rect(bounds.Size));
        }
        image.Render(background);
        if (!overflow) image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class SnapshotHotkey : IHotkeyService
    {
        public bool Registered => true;
        public int Error => 0;
        public HotkeyConfig Current { get; } = new();
        public bool TryChange(HotkeyConfig candidate) => true;
    }
    private sealed class RenderConfigWriter : IConfigWriter
    {
        public void Write(string path, byte[] bytes) { } // No configuration persistence contract in a rendering job.
    }
}
