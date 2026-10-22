using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftdock.Models;
using Swiftdock.Services;
using Swiftdock.ViewModels;
using Swiftdock.Views;
using Swiftdock.Snapshots;
using Xunit;
using Localization = Swiftdock.Services.Localization;

namespace Swiftdock.Tests;

[Collection("WPF")]
public sealed class LocalizationTests
{
    [Fact]
    public void ResourceSetsAreCompleteAndEnglishHasNoCjk()
    {
        var en = Read(CultureInfo.InvariantCulture);
        var zh = Read(CultureInfo.GetCultureInfo("zh-CN"));
        Assert.NotEmpty(en);
        Assert.Equal(en.Keys.Order(), zh.Keys.Order());
        // This exact endonym is the sole CJK exception in English resources:
        // users must be able to find their language even in an unfamiliar UI.
        var englishCjkEndonyms = new Dictionary<string, string> { ["Chinese"] = "中文" };
        Assert.Equal("English", en["English"]);
        Assert.Equal("English", zh["English"]);
        foreach (string key in en.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(zh[key]), key);
            if (englishCjkEndonyms.TryGetValue(key, out string? endonym))
            {
                Assert.Equal(endonym, en[key]);
                Assert.Equal(endonym, zh[key]);
            }
            else
                Assert.DoesNotMatch(@"[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}]", en[key]);
            // Formatting must also work for messages not exercised by happy paths.
            var args = Enumerable.Repeat<object>("sample", 12).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, en[key], args);
            _ = string.Format(CultureInfo.InvariantCulture, zh[key], args);
        }
        static Dictionary<string, string> Read(CultureInfo culture) => Localization.Resources.GetResourceSet(culture, true, false)!
            .Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    [Fact]
    public void ProductSourcesHaveNoHardCodedCjk()
    {
        var assembly = typeof(LocalizationTests).Assembly;
        // No exceptions currently needed. If a retired migration identifier must
        // contain CJK, allow its exact literal here with the migration reason.
        string[] legacyMigrationIdentifiers = [];
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".cs") || n.EndsWith(".xaml")))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            string source = reader.ReadToEnd();
            foreach (string identifier in legacyMigrationIdentifiers) source = source.Replace(identifier, "");
            Assert.False(Regex.IsMatch(source, @"[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}]"), name);
            foreach (Match reference in Regex.Matches(source, "(?:L\\.Text\\(\"|\\{loc:Loc )([A-Za-z0-9]+)"))
                Assert.False(string.IsNullOrWhiteSpace(Localization.Resources.GetString(reference.Groups[1].Value, CultureInfo.InvariantCulture)), name + ": " + reference.Groups[1].Value);
        }
    }

    [Theory]
    [InlineData(null, null)] [InlineData("system", null)] [InlineData("", null)]
    [InlineData("fr", null)] [InlineData("en-US", null)]
    [InlineData(" EN ", "en")] [InlineData("zh-cn", "zh-CN")]
    public void LanguageIsNormalizedLikeOtherSettings(string? input, string? expected)
    {
        Assert.Equal(expected, Localization.Normalize(input));
        Assert.Equal(expected, ConfigValidation.Normalize(AppConfig.CreateDefault() with { Language = input }).Language);
    }

    [Theory]
    [InlineData("zh-CN", "zh-CN")] [InlineData("zh-TW", "zh-CN")]
    [InlineData("zh-HK", "zh-CN")] [InlineData("zh", "zh-CN")]
    [InlineData("en-US", "en")] [InlineData("ja-JP", "en")]
    [InlineData("fr-FR", "en")]
    public void FollowSystemMapsEveryChineseCultureToSimplifiedChinese(string system, string expected)
    {
        var culture = CultureInfo.GetCultureInfo(system);
        Assert.Equal(expected, Localization.Resolve(null, culture).Name);
        Assert.Equal("en", Localization.Resolve("en", culture).Name);
        Assert.Equal("zh-CN", Localization.Resolve("zh-CN", culture).Name);
    }

    [Fact]
    public Task SwitchingUpdatesOpenSettingsPanelAndEditorAndPersists() => WpfTestHost.RunAsync(async () =>
    {
        string original = Localization.Instance.Culture.Name;
        string folder = Path.Combine(Path.GetTempPath(), "Swiftdock-language-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = ConfigService.Open(folder, writer: new LanguageWriter());
            using var state = new MainViewModel(store);
            state.SetLanguage("zh-CN");
            var originalNames = state.Groups.Select(g => g.Name).ToArray();
            using var services = new ServiceCollection().AddLogging().AddIconServices(Path.Combine(folder, "icons"))
                .AddDialogs().AddSingleton<ISettingsState>(state)
                .AddSingleton(new StartupService(@"C:\Swiftdock\Swiftdock.exe", folder, "LanguageTest", _ => null, (_, _) => { }))
                .AddSingleton<IHotkeyService>(new SettingsViewModelTests.Hotkey())
                .AddSingleton<ISettingsActions>(new SettingsViewModelTests.Actions()).BuildServiceProvider();
            using var scope = services.CreateScope();
            var dialogs = services.GetRequiredService<IDialogService>();
            LauncherWindow? panel = null;
            using var launcher = new LauncherViewModel(state, new LauncherDialogs(dialogs, () => panel!),
                new LauncherActions(services.GetRequiredService<IconService>(), (_, _) => Task.FromResult(new LaunchResult(true, false, "test"))), NullLogger<LauncherViewModel>.Instance);
            panel = new LauncherWindow(NullLogger<LauncherWindow>.Instance, launcher, services.GetRequiredService<IconService>(), dialogs);
            var settings = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
            await using var trayLog = new TestLogging();
            using var tray = new TaskbarIcon();
            using var trayTooltip = new TrayTooltip(tray, () => state.HotkeyDisplay, trayLog.Logger<App>());
            Assert.False(tray.IsCreated); // No desktop icon or Shell_NotifyIcon call in this host test.
            var model = scope.ServiceProvider.GetRequiredService<EditItemViewModel>();
            model.Initialize(null);
            var editor = scope.ServiceProvider.GetRequiredService<EditItemDialog>();
            try
            {
                foreach (var window in new Window[] { panel, settings, editor }) { OffscreenWindow.Prepare(window); OffscreenWindow.Show(window); }
                settings.NavigateTo("general");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var page = LauncherWindow.Descendants<SettingsGeneralPage>(settings).Single();
                var languageLabel = LauncherWindow.Descendants<TextBlock>(page).Single(t => AutomationProperties.GetAutomationId(t) == "LanguageLabel");
                var add = (Button)panel.FindName("AddItemButton");
                launcher.SetStatus(Localization.Text("ItemUpdatedChangesWillBeSavedAutomatically"));
                foreach (var (language, title, label, addName, editorTitle) in new[] {
                    ("en", "Settings", "Language", "Add item", "Add item"),
                    ("zh-CN", "设置", "语言", "添加项目", "添加项目") })
                {
                    settings.ViewModel.Language = language;
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    Assert.Equal(title, settings.Title);
                    Assert.Equal(label, languageLabel.Text);
                    Assert.Equal("中文", ((RadioButton)page.FindName("ChineseLanguage")).Content);
                    Assert.Equal("English", ((RadioButton)page.FindName("EnglishLanguage")).Content);
                    string tooltip = Localization.Text("Swiftdock0", state.HotkeyDisplay);
                    Assert.Equal(tooltip, tray.ToolTipText);
                    Assert.Equal(tooltip, tray.TrayIcon.ToolTip);
                    var tooltipEvent = trayLog.Events.Last();
                    using (var entry = JsonDocument.Parse(TestLogging.Clef(tooltipEvent)))
                    {
                        var root = entry.RootElement;
                        Assert.Equal("tray.tooltip", root.GetProperty("EventId").GetProperty("Name").GetString());
                        Assert.Equal(language, root.GetProperty("culture").GetString());
                        Assert.Equal(tooltip, root.GetProperty("toolTipText").GetString());
                        Assert.Equal(tooltip, root.GetProperty("shellToolTip").GetString());
                        Assert.False(root.GetProperty("shellUpdated").GetBoolean());
                    }
                    Assert.False(tray.IsCreated);
                    Assert.Equal(addName, AutomationProperties.GetName(add));
                    Assert.StartsWith(addName, add.ToolTip.ToString());
                    Assert.Equal(editorTitle, editor.Title);
                    Assert.Equal(Localization.Instance["ItemUpdatedChangesWillBeSavedAutomatically"], launcher.ToastText);
                    Assert.Equal(originalNames, state.Groups.Select(g => g.Name));
                    await state.FlushAsync();
                    using var config = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath));
                    Assert.Equal(language, config.RootElement.GetProperty("language").GetString());
                    Assert.Equal(language, ConfigValidation.Parse(await File.ReadAllBytesAsync(store.FilePath)).Language);
                }
                state.SetLanguage("system"); await state.FlushAsync();
                using var system = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath));
                Assert.False(system.RootElement.TryGetProperty("language", out _));
                foreach (string language in new[] { "en", "zh-CN" })
                {
                    state.SetLanguage(language);
                    Assert.Equal(Localization.Instance["Favorites"], new GroupConfig().Name);
                    Assert.Equal(Localization.Instance["Favorites"], AppConfig.CreateDefault().Groups[0].Name);
                }
            }
            finally { editor.Close(); settings.Close(); panel.Close(); }
        }
        finally { Localization.Instance.Apply(original); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    });

    // This test verifies setting persistence. Atomic replace/backup behavior is
    // covered by ConfigTests and cannot run under the restricted filesystem.
    private sealed class LanguageWriter : IConfigWriter
    {
        public void Write(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void BothCulturesResolveFromPublishedBundleWhenProvided()
    {
        string? exe = Environment.GetEnvironmentVariable("SWIFTDOCK_PUBLISHED_EXE");
        if (exe is null) return; // Release verification reuses this test; no probe executable.
        byte[] bytes = File.ReadAllBytes(exe);
        byte[] marker = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
        int signature = bytes.AsSpan().IndexOf(marker);
        Assert.True(signature >= 8, "Not a .NET single-file bundle.");
        using var reader = new BinaryReader(new MemoryStream(bytes));
        reader.BaseStream.Position = BitConverter.ToInt64(bytes, signature - 8);
        Assert.Equal(6u, reader.ReadUInt32()); _ = reader.ReadUInt32();
        int count = reader.ReadInt32(); _ = reader.ReadString();
        reader.BaseStream.Position += 40; // deps/runtimeconfig locations and bundle flags
        var files = new Dictionary<string, byte[]>();
        for (int i = 0; i < count; i++)
        {
            long offset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = reader.ReadInt64();
            _ = reader.ReadByte(); string path = reader.ReadString();
            if (path is not ("Swiftdock.dll" or "zh-CN/Swiftdock.resources.dll")) continue;
            Assert.Equal(0, compressed);
            files[path] = bytes.AsSpan((int)offset, (int)size).ToArray();
        }
        Assert.Equal(2, files.Count);
        var context = new AssemblyLoadContext("Published resources", isCollectible: true);
        try
        {
            context.Resolving += (_, name) => name.Name == "Swiftdock.resources"
                ? context.LoadFromStream(new MemoryStream(files["zh-CN/Swiftdock.resources.dll"])) : null;
            var assembly = context.LoadFromStream(new MemoryStream(files["Swiftdock.dll"]));
            var resources = new ResourceManager("Swiftdock.Resources.Strings", assembly);
            Assert.Equal("Swiftdock", resources.GetString("Swiftdock", CultureInfo.GetCultureInfo("en")));
            Assert.Equal("迅启", resources.GetString("Swiftdock", CultureInfo.GetCultureInfo("zh-CN")));
        }
        finally { context.Unload(); }
    }
}
