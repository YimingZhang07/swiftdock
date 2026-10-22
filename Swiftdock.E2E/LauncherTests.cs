using System.Drawing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;
using static Swiftdock.E2E.LauncherSession;

namespace Swiftdock.E2E;

[Collection(DesktopCollection.Name)]
public sealed class LauncherTests(ITestOutputHelper output)
{
    [DesktopFact]
    public void Settings_navigation_and_transparency_drag_persist_after_exit() => Run(output, app =>
    {
        bool supported = app.SupportsMaterial("acrylic");
        app.Click(app.ById("SettingsButton"));
        var settings = app.Dialog("设置");
        foreach (var (page, control) in new[] { ("General", "MainKey"), ("Appearance", "TransparencySlider"),
            ("Panel", "ResetSizeButton"), ("About", "DirectoryLabel") })
        {
            app.NavigateSettings(settings, page); app.ById(control, settings);
        }
        app.ById("OpenLogsButton", settings); // Never opens Explorer during automation.
        app.NavigateSettings(settings, "Appearance");
        var slider = app.ById("TransparencySlider", settings);
        var toggle = app.ById("KeepBackdropWhenInactiveBox", settings);
        Assert.Equal(ToggleState.On, toggle.Patterns.Toggle.Pattern.ToggleState.Value);
        app.Click(app.ById("SolidBackdrop", settings), settings);
        Eventually(() => !slider.IsEnabled && !toggle.IsEnabled, "solid disables both backdrop subrows");
        app.Click(app.ById("AcrylicBackdrop", settings), settings);
        if (!supported)
        {
            Assert.False(slider.IsEnabled); Assert.False(toggle.IsEnabled);
            return;
        }
        app.DragTransparency(settings, 65);
        int value = (int)slider.Patterns.RangeValue.Pattern.Value.Value;
        Assert.InRange(value, 50, 80);
        app.Click(toggle, settings);
        // Leave settings open: --exit must flush the latest debounced value too.
        using var config = app.ExitAndReadConfig();
        Assert.Equal(0, value % 5);
        Assert.Equal(100 - value, config.RootElement.GetProperty("backdropOpacity").GetInt32());
        Assert.False(config.RootElement.GetProperty("keepBackdropWhenInactive").GetBoolean());
    }, theme: "dark", backdrop: "acrylic");

    [DesktopTheory]
    [InlineData("acrylic")]
    [InlineData("mica")]
    public void Inactive_material_pixels_and_real_focus_survive_prewarm(string backdrop) => Run(output, app =>
    {
        app.AssertInactiveBackdrop(backdrop);
        using var config = app.ExitAndReadConfig();
        Assert.False(config.RootElement.TryGetProperty("keepBackdropWhenInactive", out _));
        Assert.False(config.RootElement.TryGetProperty("backdropOpacity", out _));
        Assert.False(config.RootElement.TryGetProperty("alwaysOnTop", out _));
    }, theme: "dark", backdrop: backdrop, silentStart: true, test: $"Inactive_material_{backdrop}");

    [DesktopTheory]
    [InlineData("dark", "mica", "purple", 0x87, 0x64, 0xB8)]
    [InlineData("light", "acrylic", "green", 0x10, 0x7C, 0x10)]
    public void Accent_preset_survives_startup_and_appearance_changes(string theme, string backdrop, string accent, int red, int green, int blue) => Run(output, app =>
    {
        var color = Color.FromArgb(red, green, blue);
        // This first capture happens before any in-process settings interaction.
        app.AssertTabAccent(color, "startup");
        app.AssertNativeBackdrop(app.PanelHandle, backdrop);
        app.Hotkey(); app.Hotkey(panelHidden: true);
        app.AssertTabAccent(color, "reactivated");
        app.NotifyWindowsAppearanceChanged();
        app.AssertTabAccent(color, "windows-notifications");
        app.ChangeAppearance(theme == "dark" ? "light" : "dark", "solid");
        app.AssertTabAccent(color, "theme-and-backdrop-changed");
        app.ChangeAppearance(theme, backdrop);
        app.AssertTabAccent(color, "restored");
        using var config = app.ExitAndReadConfig();
        Assert.Equal(theme, AppearanceValue(config.RootElement, "theme", "system"));
        Assert.Equal(backdrop, AppearanceValue(config.RootElement, "backdrop", "acrylic"));
        Assert.Equal(accent, AppearanceValue(config.RootElement, "accent", "system"));
    }, theme: theme, backdrop: backdrop, accent: accent, test: $"Accent_preset_{theme}_{backdrop}_{accent}");

    [DesktopFact]
    public void Keyboard_pointer_menu_escape_and_hotkey() => Run(output, app =>
    {
        app.DeactivateAndAssertVisible();
        app.AssertSelection(-1);
        app.Key(VirtualKeyShort.F2);
        app.Key(VirtualKeyShort.DELETE);
        app.Key(VirtualKeyShort.RETURN);
        Assert.False(app.HasWindow("重命名条目"));
        Assert.Equal(4, app.Tiles().Length);
        app.Key(VirtualKeyShort.DOWN);
        app.AssertSelection(0);
        app.Key(VirtualKeyShort.RIGHT);
        app.AssertSelection(1);
        app.Key(VirtualKeyShort.ALT); // Alt down/up must not enter the native system-menu loop.
        app.AssertSelection(1);
        app.AssertForeground();
        app.Key(VirtualKeyShort.RIGHT);
        app.AssertSelection(2);
        app.Key(VirtualKeyShort.ESCAPE);
        app.AssertSelection(-1);
        app.Key(VirtualKeyShort.DOWN);
        app.Key(VirtualKeyShort.RIGHT);
        app.AssertSelection(1);
        var viewport = app.ById("Viewer").BoundingRectangle;
        var empty = new Point(viewport.Left + viewport.Width / 2, viewport.Bottom - (int)(30 * app.Scale));
        Assert.DoesNotContain(app.Tiles(), tile => tile.BoundingRectangle.Contains(empty));
        app.Click(empty);
        app.AssertSelection(-1);
        app.Key(VirtualKeyShort.DOWN);
        app.AssertSelection(0);
        app.Click(app.Tiles()[0], button: MouseButton.Right);
        foreach (string text in new[] { "打开", "编辑…", "删除", "刷新图标" })
            app.Named(text, ControlType.MenuItem);
        app.Key(VirtualKeyShort.ESCAPE);
        Eventually(() => !app.HasMenuItem("打开"), "context menu to close");
        app.AssertVisible(true);
        app.Key(VirtualKeyShort.ESCAPE);
        app.AssertSelection(-1);
        app.AssertVisible(true);
        app.Key(VirtualKeyShort.ESCAPE);
        app.AssertVisible(false);
        app.Hotkey(panelHidden: true);
        app.Hotkey();
        app.AssertVisible(false);
        app.Hotkey(panelHidden: true);
        app.AssertVisible(true);
        app.AssertForeground();
        app.AssertSelection(-1);
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        Eventually(() => app.TileNames().SequenceEqual(new[] { "Echo" }), "Ctrl+Tab to select the other group");
        app.AssertSelection(0);
        app.Click(app.Named("E2E Main", ControlType.Button, app.Panel));
        Eventually(() => app.TileNames().SequenceEqual(new[] { "Alpha", "Bravo", "Charlie", "Delta" }),
            "mouse click to switch back to the main group after Ctrl+Tab");
        app.AssertSelection(-1);
        app.Key(VirtualKeyShort.DOWN);
        app.AssertSelection(0);
        app.AssertTrayToggleIfReachable();
    }, silentStart: true);

    [DesktopFact]
    public void Tab_click_switches_both_ways_and_drag_reorders() => Run(output, app =>
    {
        string[] mainTiles = ["Alpha", "Bravo", "Charlie", "Delta"];
        Assert.Equal(mainTiles, app.TileNames());
        app.Key(VirtualKeyShort.DOWN);
        app.AssertSelection(0);
        app.Click(app.Named("E2E Other", ControlType.Button, app.Panel));
        Eventually(() => app.TileNames().SequenceEqual(new[] { "Echo" }), "real tab click to show Echo");
        app.AssertSelection(-1);
        app.AssertTileListFocused();
        app.Key(VirtualKeyShort.DOWN);
        app.AssertSelection(0);
        app.Click(app.Named("E2E Main", ControlType.Button, app.Panel));
        Eventually(() => app.TileNames().SequenceEqual(mainTiles), "real tab click back to show Alpha through Delta");
        app.AssertSelection(-1);
        app.AssertTileListFocused();

        var main = app.Named("E2E Main", ControlType.Button, app.Panel).BoundingRectangle;
        var other = app.Named("E2E Other", ControlType.Button, app.Panel).BoundingRectangle;
        Assert.True(main.Right <= other.Left, "Main must start before Other.");
        // Drag the inactive tab into the left half of Main: insertion slot 0.
        app.Drag(Center(other), new Point(main.Left + (int)(6 * app.Scale), Center(main).Y));
        Eventually(() => app.Named("E2E Other", ControlType.Button, app.Panel).BoundingRectangle.Right
            <= app.Named("E2E Main", ControlType.Button, app.Panel).BoundingRectangle.Left,
            "real tab drag to reorder Other before Main");
        Assert.Equal(mainTiles, app.TileNames()); // Releasing a drag must not select its source tab.
        app.AssertSelection(-1);
        app.Click(app.Named("E2E Other", ControlType.Button, app.Panel));
        Eventually(() => app.TileNames().SequenceEqual(new[] { "Echo" }), "reordered tab to remain clickable");
        app.AssertTileListFocused();
        using var config = app.ExitAndReadConfig();
        Assert.Equal(new[] { "E2E Other", "E2E Main" }, config.RootElement.GetProperty("groups")
            .EnumerateArray().Select(group => group.GetProperty("name").GetString()));
    });

    [DesktopFact]
    public void Click_and_held_enter_each_launch_once() => Run(output, app =>
    {
        app.Key(VirtualKeyShort.DOWN);
        app.AssertSelection(0);
        app.Click(app.Tiles()[1]);
        app.ExpectLaunch(BravoId, "Bravo");
        app.AssertSelection(-1);
        app.AssertVisible(true);
        app.Key(VirtualKeyShort.DOWN);
        app.Key(VirtualKeyShort.RIGHT);
        app.AssertSelection(1);
        app.HoldEnter();
        app.ExpectLaunch(BravoId, "Bravo");
        app.AssertVisible(true);
        // Every scenario rechecks the complete launch log after graceful exit, including delayed duplicates.
    });

    [DesktopFact]
    public void Header_drag_moves_and_persists_position() => Run(output, app =>
    {
        var before = app.Panel.BoundingRectangle;
        var addGroup = app.ById("AddGroupButton").BoundingRectangle;
        var addItem = app.ById("AddItemButton").BoundingRectangle;
        Assert.True(addItem.Left - addGroup.Right > 20, "Seeded header must have an empty drag area.");
        var from = new Point((addGroup.Right + addItem.Left) / 2, Center(addItem).Y);
        app.Drag(from, new Point(from.X + 120, from.Y + 60));
        Eventually(() => Math.Abs(app.Panel.BoundingRectangle.Left - before.Left - 120) <= 2
            && Math.Abs(app.Panel.BoundingRectangle.Top - before.Top - 60) <= 2, "header drag to move the panel by (+120,+60) physical pixels");
        var after = app.Panel.BoundingRectangle;
        using var config = app.ExitAndReadConfig();
        Assert.InRange(Math.Abs(config.RootElement.GetProperty("windowLeft").GetInt32() - after.Left), 0, 2);
        Assert.InRange(Math.Abs(config.RootElement.GetProperty("windowTop").GetInt32() - after.Top), 0, 2);
    });

    [DesktopFact]
    public void Right_edge_resize_persists_size() => Run(output, app =>
    {
        var before = app.Panel.BoundingRectangle;
        var from = new Point(before.Right - 2, Center(before).Y);
        app.Drag(from, new Point(from.X + 100, from.Y));
        Eventually(() => Math.Abs(app.Panel.BoundingRectangle.Width - before.Width - 100) <= 3,
            "right edge drag to widen the panel by 100 physical pixels");
        var after = app.Panel.BoundingRectangle;
        double scale = app.Scale;
        using var config = app.ExitAndReadConfig();
        Assert.InRange(Math.Abs(config.RootElement.GetProperty("windowWidth").GetDouble() - after.Width / scale), 0, 2);
        Assert.InRange(Math.Abs(config.RootElement.GetProperty("windowHeight").GetDouble() - after.Height / scale), 0, 2);
    });

    [DesktopFact]
    public void Tile_reorder_is_visible_and_persisted() => Run(output, app =>
    {
        var tiles = app.Tiles();
        var target = tiles[2].BoundingRectangle;
        // Right half of the original third tile is insertion slot 3 (final index 2).
        app.Drag(Center(tiles[0].BoundingRectangle), new Point(target.Right - (int)(6 * app.Scale), Center(target).Y));
        string[] expected = ["Bravo", "Charlie", "Alpha", "Delta"];
        Eventually(() => app.TileNames().SequenceEqual(expected), "visible tile order Bravo, Charlie, Alpha, Delta");
        Assert.Equal(expected, app.Tiles().OrderBy(t => t.BoundingRectangle.Top).ThenBy(t => t.BoundingRectangle.Left).Select(t => t.Name));
        using var config = app.ExitAndReadConfig();
        Assert.Equal(expected, config.RootElement.GetProperty("groups")[0].GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    });

    [DesktopFact]
    public void Large_icon_reorder_crosses_row_boundary_and_is_persisted() => Run(output, app =>
    {
        var tiles = app.Tiles();
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Delta" }, tiles.Select(tile => tile.Name));
        var source = tiles[0].BoundingRectangle;
        var target = tiles[3].BoundingRectangle;
        Assert.True(target.Top >= source.Bottom, "The 48 px fixture must put Delta on the next row.");
        Assert.Equal(source.Top, tiles[2].BoundingRectangle.Top);
        Assert.Equal(source.Left, target.Left);
        Assert.True(app.ById("Viewer").BoundingRectangle.Contains(target), "The drop tile must be fully visible.");
        app.Drag(Center(source), new Point(target.Right - (int)(8 * app.Scale), Center(target).Y));
        string[] expected = ["Bravo", "Charlie", "Delta", "Alpha"];
        Eventually(() => app.TileNames().SequenceEqual(expected), "48 px cross-row tile order Bravo, Charlie, Delta, Alpha");
        Assert.Equal(expected, app.Tiles().OrderBy(tile => tile.BoundingRectangle.Top).ThenBy(tile => tile.BoundingRectangle.Left).Select(tile => tile.Name));
        using var config = app.ExitAndReadConfig();
        Assert.Equal(120, config.RootElement.GetProperty("scale").GetInt32());
        Assert.False(config.RootElement.TryGetProperty("iconSize", out _));
        Assert.Equal(expected, config.RootElement.GetProperty("groups")[0].GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    }, iconSize: 48, windowWidth: 400, windowHeight: 380);

    [DesktopFact]
    public void Settings_live_preferences_are_persisted() => Run(output, app =>
    {
        app.Click(app.ById("SettingsButton"));
        var dialog = app.Dialog("设置");
        app.AssertLivePanelScale(dialog);
        app.NavigateSettings(dialog, "Panel");
        var toggle = app.ById("WebsiteIconsBox", dialog);
        Assert.Equal(ToggleState.On, toggle.Patterns.Toggle.Pattern.ToggleState.Value);
        app.Click(toggle, dialog);
        Eventually(() => toggle.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off, "自动获取网站图标 off");
        var followMouse = app.ById("FollowMouse", dialog);
        Assert.False(followMouse.Patterns.SelectionItem.Pattern.IsSelected.Value);
        app.Click(followMouse, dialog);
        Eventually(() => followMouse.Patterns.SelectionItem.Pattern.IsSelected.Value, "呼出位置 跟随鼠标 selected");
        app.Click(app.ById("ResetSizeButton", dialog), dialog);
        app.Click(app.Named("关闭", ControlType.Button, dialog), dialog);
        Eventually(() => !app.HasWindow("设置"), "settings window to close");
        using var config = app.ExitAndReadConfig();
        Assert.False(config.RootElement.GetProperty("autoFetchWebsiteIcons").GetBoolean());
        Assert.Equal("cursor", config.RootElement.GetProperty("placement").GetString());
        foreach (string property in new[] { "windowLeft", "windowTop", "windowWidth", "windowHeight" })
            Assert.False(config.RootElement.TryGetProperty(property, out _), $"Reset should omit {property}.");
    }, websiteIcons: true); // Only this profile starts enabled; all five seed items are local folders with glyph icons.

    [DesktopFact]
    public void Add_url_with_icon_then_edit_and_undo_delete() => Run(output, app =>
    {
        app.Click(app.ById("AddItemButton"));
        var dialog = app.Dialog("添加项目");
        app.AssertEditorTypeHeight(dialog);
        app.SetText("https://example.com/", dialog, app.ById("PathBox", dialog));
        app.Click(app.ById("IconPreview", dialog), dialog);
        app.Named("代码", ControlType.RadioButton);
        app.Key(VirtualKeyShort.RIGHT, dialog);
        Eventually(() => app.Named("设备连接", ControlType.RadioButton).Properties.HasKeyboardFocus.Value, "picker Right arrow to move focus");
        app.Key(VirtualKeyShort.RETURN, dialog);
        Assert.True(app.HasWindow("添加项目")); // Picker Enter must not submit the editor.
        app.Click(app.ById("SystemSeries"), dialog);
        app.AssertSystemIconPagePacked();
        var systemChoice = app.FirstSystemIcon();
        string systemSpec = systemChoice.AutomationId;
        Assert.StartsWith("file:%SystemRoot%\\System32\\imageres.dll,-", systemSpec);
        app.Click(systemChoice, dialog);
        app.Key(VirtualKeyShort.RIGHT, dialog);
        Eventually(() => !systemChoice.Properties.HasKeyboardFocus.Value, "system grid Right arrow");
        app.Key(VirtualKeyShort.LEFT, dialog);
        Eventually(() => systemChoice.Properties.HasKeyboardFocus.Value, "system grid Left arrow");
        var firstCell = systemChoice.BoundingRectangle;
        app.Key(VirtualKeyShort.DOWN, dialog);
        Eventually(() => app.ById("CatalogRows").FindAllDescendants(cf => cf.ByControlType(ControlType.RadioButton))
            .Any(e => e.Properties.HasKeyboardFocus.Value && Math.Abs(e.BoundingRectangle.Left - firstCell.Left) <= 1
                && Math.Abs(e.BoundingRectangle.Top - firstCell.Bottom) <= 1), "system Down arrow to reach the packed second row without a gap");
        app.Key(VirtualKeyShort.UP, dialog);
        Eventually(() => systemChoice.Properties.HasKeyboardFocus.Value, "system Up arrow to return to the first cell");
        app.Key(VirtualKeyShort.RETURN, dialog);
        app.Key(VirtualKeyShort.ESCAPE, dialog);
        Eventually(() => !app.HasNamed("系统", ControlType.RadioButton), "icon picker to close without closing the editor");
        Assert.True(app.HasWindow("添加项目"));
        app.Click(app.ById("SaveButton", dialog), dialog);
        Eventually(() => !app.HasWindow("添加项目") && app.TileNames().Contains("example.com"), "new example.com tile");
        Assert.Equal(5, app.Tiles().Length);
        app.AssertPersistedIcon("example.com", systemSpec);
        app.AssertTileMatchesIcon("example.com", systemSpec);
        app.Click(app.Tiles().Single(tile => tile.Name == "example.com"), button: MouseButton.Right);
        app.Click(app.Named("编辑…", ControlType.MenuItem));
        dialog = app.Dialog("编辑项目");
        app.AssertEditorTypeHeight(dialog);
        app.Click(app.ById("IconPreview", dialog), dialog);
        Eventually(() => app.ById("SystemSeries").Patterns.SelectionItem.Pattern.IsSelected.Value, "current system icon's tab to open selected");
        Eventually(() => app.ById(systemSpec).Patterns.SelectionItem.Pattern.IsSelected.Value, "current system item selected");
        app.Click(app.ById("ColorSeries"), dialog);
        app.Click(app.ById("fluent:apps"), dialog);
        app.Key(VirtualKeyShort.RIGHT, dialog);
        Eventually(() => app.ById("fluent:apps_list").Properties.HasKeyboardFocus.Value, "colour grid Right arrow");
        app.Key(VirtualKeyShort.RETURN, dialog);
        app.Key(VirtualKeyShort.ESCAPE, dialog);
        app.Click(app.ById("IconPreview", dialog), dialog);
        Eventually(() => app.ById("ColorSeries").Patterns.SelectionItem.Pattern.IsSelected.Value
            && app.ById("fluent:apps_list").Patterns.SelectionItem.Pattern.IsSelected.Value, "current colour tab and item selected on reopening");
        app.Key(VirtualKeyShort.ESCAPE, dialog);
        app.SetText("Edited link", dialog, app.ById("NameBox", dialog));
        app.Click(app.ById("SaveButton", dialog), dialog);
        Eventually(() => !app.HasWindow("编辑项目") && app.TileNames().Contains("Edited link"), "context-menu edit to update the tile");
        app.AssertPersistedIcon("Edited link", "fluent:apps_list");
        app.AssertTileMatchesIcon("Edited link", "fluent:apps_list", previousSpec: systemSpec);
        // The image check clears selection. Restore it with real navigation for F2.
        int editedIndex = Array.IndexOf(app.TileNames(), "Edited link");
        app.Key(VirtualKeyShort.DOWN);
        for (int i = 0; i < editedIndex; i++) app.Key(VirtualKeyShort.RIGHT);
        app.AssertSelection(editedIndex);
        app.Key(VirtualKeyShort.F2);
        dialog = app.Dialog("重命名条目");
        app.SetText("Review link", dialog, app.ById("NameBox", dialog));
        app.Click(app.Named("确定", ControlType.Button, dialog), dialog);
        Eventually(() => !app.HasWindow("重命名条目") && app.TileNames().Contains("Review link"), "F2 rename to update the selected tile");
        app.Key(VirtualKeyShort.DELETE);
        Eventually(() => app.Tiles().Length == 4, "Delete to remove the selected tile");
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        Eventually(() => app.Tiles().Length == 5 && app.TileNames().Contains("Review link"), "Ctrl+Z to restore the edited item");
        using var config = app.ExitAndReadConfig();
        var item = config.RootElement.GetProperty("groups")[0].GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Review link");
        Assert.Equal("fluent:apps_list", item.GetProperty("icon").GetString());

    }, backdrop: "solid");

    [DesktopFact]
    public void Delete_group_confirmation_can_cancel_then_delete() => Run(output, app =>
    {
        void OpenConfirmation()
        {
            app.Click(app.Named("E2E Other", ControlType.Button, app.Panel), button: MouseButton.Right);
            app.Click(app.Named("删除分组…", ControlType.MenuItem));
        }
        var beforeCancel = app.CapturePanelFrame("before-cancel-confirmation");
        OpenConfirmation();
        var dialog = app.Dialog("删除分组");
        nint dialogHandle = dialog.Properties.NativeWindowHandle.Value;
        app.Click(app.Named("取消", ControlType.Button, dialog), dialog);
        Eventually(() => !Desktop.IsWindowVisible(dialogHandle), "cancelled confirmation HWND to close");
        app.AssertPanelFrameUnchanged(beforeCancel, "after-cancel-confirmation");
        Eventually(() => !app.HasWindow("删除分组"), "cancelled confirmation to close");
        Assert.True(app.HasTab("E2E Other"));
        var beforeDelete = app.CapturePanelFrame("before-delete-confirmation");
        OpenConfirmation();
        dialog = app.Dialog("删除分组");
        dialogHandle = dialog.Properties.NativeWindowHandle.Value;
        app.Click(app.Named("删除", ControlType.Button, dialog), dialog);
        Eventually(() => !Desktop.IsWindowVisible(dialogHandle), "deleted confirmation HWND to close");
        app.AssertPanelFrameUnchanged(beforeDelete, "after-delete-confirmation");
        Eventually(() => !app.HasWindow("删除分组") && !app.HasTab("E2E Other"), "second group to be removed");
        Assert.True(app.HasTab("E2E Main"));
        using var config = app.ExitAndReadConfig();
        Assert.Equal(PrimaryGroupId, Assert.Single(config.RootElement.GetProperty("groups").EnumerateArray()).GetProperty("id").GetString());
    });
    // AppConfig normalizes defaults to null and omits them when writing JSON.
    private static string AppearanceValue(System.Text.Json.JsonElement config, string property, string fallback) =>
        config.TryGetProperty(property, out var value) ? value.GetString() ?? fallback : fallback;
}
