using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Xunit;

namespace Swiftdock.E2E;

internal sealed partial class LauncherSession
{
    public void AssertUiLanguage(Window settings, bool english)
    {
        NavigateSettings(settings, "General");
        var changedAfter = DateTimeOffset.UtcNow;
        Click(ById(english ? "EnglishLanguage" : "ChineseLanguage", settings), settings);
        _englishUi = english;
        Eventually(() => settings.Name == (english ? "Settings" : "设置"), "settings title to change live");
        Assert.Equal("中文", ById("ChineseLanguage", settings).Name);
        Assert.Equal("English", ById("EnglishLanguage", settings).Name);
        foreach (var (id, en, zh) in new[] {
            ("SettingsGeneralNav", "General", "常规"), ("SettingsAppearanceNav", "Appearance", "外观"),
            ("SettingsPanelNav", "Panel", "面板"), ("SettingsAboutNav", "About", "关于"),
            ("LanguageLabel", "Language", "语言") })
            Eventually(() => ById(id, settings).Name == (english ? en : zh), id + " to change live");
        var add = ById("AddItemButton");
        Assert.Equal(english ? "Add item" : "添加项目", add.Name);
        Assert.Equal(english ? "Add item (or drop files)" : "添加项目（也可拖入文件）", add.Properties.HelpText.Value);
        AssertProductTrayTooltip(changedAfter);
        AssertTrayToggleIfReachable(languageOnly: true);
        Foreground(settings);
        Eventually(() =>
        {
            using var config = TryReadConfig();
            if (config is null) return false;
            return config.RootElement.GetProperty("language").GetString() == (english ? "en" : "zh-CN");
        }, "language to persist after real input");
    }

    private void AssertProductTrayTooltip(DateTimeOffset changedAfter)
    {
        string expected = TestTrayTooltip;
        string latest = "No tray.tooltip event from this process.";
        try
        {
            Eventually(() =>
            {
                foreach (string file in Directory.GetFiles(Path.Combine(_temp, "diagnostics"), "swiftdock-*.jsonl").Order())
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        JsonDocument entry;
                        try { entry = JsonDocument.Parse(line); }
                        catch (JsonException) { continue; } // The final buffered line may still be in flight.
                        using (entry)
                        {
                            var root = entry.RootElement;
                            if (!root.TryGetProperty("EventId", out var eventId)
                                || !eventId.TryGetProperty("Name", out var name) || name.GetString() != "tray.tooltip"
                                || root.GetProperty("Pid").GetInt32() != _process!.Id) continue;
                            latest = line;
                            if (root.GetProperty("@t").GetDateTimeOffset() < changedAfter) continue;
                            Assert.Equal(_englishUi ? "en" : "zh-CN", root.GetProperty("culture").GetString());
                            Assert.Equal(expected, root.GetProperty("toolTipText").GetString());
                            Assert.Equal(expected, root.GetProperty("shellToolTip").GetString());
                            Assert.True(root.GetProperty("shellUpdated").GetBoolean(), "Tooltip was not sent to an already-created shell icon.");
                            return true;
                        }
                    }
                }
                return false;
            }, "fresh product tray tooltip event for " + expected, 4000);
        }
        finally { Diagnostic($"Product tray tooltip expected={JsonSerializer.Serialize(expected)}; latest={latest}"); }
    }
}
