using System.IO;
using System.Text.Json;
using Swiftdock.Models;

namespace Swiftdock.Services;

public sealed class FutureConfigException(int version) : Exception($"配置版本 {version} 高于当前支持的版本 1；已禁止覆盖。")
{
    public int Version { get; } = version;
}

public static class ConfigValidation
{
    public static IReadOnlyList<string> Keys { get; } = new[] { "Space" }
        .Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString()))
        .Concat(Enumerable.Range(0, 10).Select(i => $"D{i}"))
        .Concat(Enumerable.Range(1, 12).Select(i => $"F{i}")).ToArray();

    public static void ValidateHotkey(HotkeyConfig? value)
    {
        if (value is null || value.Modifiers is null || value.Modifiers.Length == 0
            || value.Modifiers.Distinct().Count() != value.Modifiers.Length
            || value.Modifiers.Any(m => m is not ("Alt" or "Control" or "Shift" or "Windows"))
            || !Keys.Contains(value.Key)) throw new InvalidDataException("热键需要至少一个修饰键和受支持的主键。");
    }

    public static AppConfig Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out int version)) throw new InvalidDataException("配置缺少有效 version。");
        if (version > 1) throw new FutureConfigException(version);
        var config = JsonSerializer.Deserialize(bytes, ConfigJsonContext.Default.AppConfig)
            ?? throw new InvalidDataException("配置为空。");
        return Normalize(config);
    }

    public static AppConfig Normalize(AppConfig config)
    {
        if (config.Version != 1) throw new InvalidDataException("不支持的旧配置版本。");
        ValidateHotkey(config.Hotkey);
        if (config.Columns is < 1 or > 12)
            throw new InvalidDataException("columns 必须为 1–12。");
        if (config.Groups is null) throw new InvalidDataException("groups 不能为 null。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Id(string? id)
        {
            if (id is null || !Guid.TryParseExact(id, "N", out _) || !ids.Add(id))
                throw new InvalidDataException($"无效或重复 ID：{id}");
        }
        foreach (var group in config.Groups)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Name) || group.Items is null)
                throw new InvalidDataException("分组名称或 items 无效。");
            Id(group.Id);
            foreach (var item in group.Items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Path)
                    || !Enum.IsDefined(item.Kind) || item.Arguments is null || item.WorkingDirectory is null || item.LaunchCount < 0)
                    throw new InvalidDataException($"分组 {group.Name} 中有无效条目；未丢弃或覆盖原数据。");
                Id(item.Id);
                if (item.Kind == LaunchKind.Url && !LaunchService.IsWebUrl(item.Path))
                    throw new InvalidDataException($"{item.Name} 不是有效 http/https 网址。");
            }
        }
        var groups = config.Groups.Count == 0 ? new List<GroupConfig> { new() } : config.Groups;
        // Keep the original graph when no icons need normalizing (including legacy configurations).
        if (groups.Any(g => g.Items.Any(i => i.Icon != IconSpec.Normalize(i.Icon))))
            groups = groups.Select(g => g with { Items = g.Items.Select(i => i with { Icon = IconSpec.Normalize(i.Icon) }).ToList() }).ToList();
        static bool ValidSize(double? value) => value is > 0 and <= 10000 && double.IsFinite(value.Value);
        bool sized = ValidSize(config.WindowWidth) && ValidSize(config.WindowHeight);
        bool positioned = config.WindowLeft is >= -100000 and <= 100000 && config.WindowTop is >= -100000 and <= 100000;
        int scale = PanelMetrics.Normalize(config.Scale ?? (config.IconSize is { } icon ? PanelMetrics.FromLegacyIconSize(icon) : 100));
        return config with { Scale = scale == 100 ? null : scale, IconSize = null,
            HideOnDeactivate = false, AutoFetchWebsiteIcons = config.AutoFetchWebsiteIcons == false ? false : null,
            Theme = AppearanceOptions.NormalizeTheme(config.Theme), Backdrop = AppearanceOptions.NormalizeBackdrop(config.Backdrop),
            Accent = AppearanceOptions.NormalizeAccent(config.Accent),
            BackdropOpacity = Math.Clamp(config.BackdropOpacity, 0, 100),
            KeepBackdropWhenInactive = config.KeepBackdropWhenInactive == false ? false : null,
            AlwaysOnTop = config.AlwaysOnTop == false ? false : null,
            WindowWidth = sized ? config.WindowWidth : null,
            WindowLeft = positioned ? config.WindowLeft : null, WindowTop = positioned ? config.WindowTop : null,
            Placement = config.Placement == "cursor" ? "cursor" : null,
            WindowHeight = sized ? config.WindowHeight : null, Groups = groups, LastGroupId = groups.Any(g => g.Id == config.LastGroupId)
            ? config.LastGroupId : groups[0].Id };
    }
}
