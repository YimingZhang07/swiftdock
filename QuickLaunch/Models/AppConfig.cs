using System.Text.Json.Serialization;

namespace QuickLaunch.Models;

public sealed record HotkeyConfig
{
    public string[] Modifiers { get; init; } = ["Alt"];
    public string Key { get; init; } = "Space";
    [JsonIgnore] public string Display => string.Join("+", Modifiers.Append(Key));
    public HotkeyConfig Copy() => this with { Modifiers = Modifiers.ToArray() };
    public bool SameAs(HotkeyConfig other) => Key == other.Key
        && Modifiers.Order().SequenceEqual(other.Modifiers.Order());
}

public sealed record GroupConfig
{
    [JsonRequired]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [JsonRequired]
    public string Name { get; init; } = "常用";
    [JsonRequired]
    public List<LaunchItem> Items { get; init; } = [];
}

public sealed record AppConfig
{
    [JsonRequired]
    public int Version { get; init; } = 1;
    public HotkeyConfig Hotkey { get; init; } = new();
    public bool HideOnDeactivate { get; init; } = true;
    public int IconSize { get; init; } = 48;
    public int Columns { get; init; } = 6;
    public string? LastGroupId { get; init; }
    [JsonRequired] public List<GroupConfig> Groups { get; init; } = [];

    public static AppConfig CreateDefault()
    {
        var common = new GroupConfig
        {
            Items = [new("记事本", "%WINDIR%\\System32\\notepad.exe", LaunchKind.App),
                new("用户目录", "%USERPROFILE%", LaunchKind.Folder),
                new("GitHub", "https://github.com", LaunchKind.Url)]
        };
        var work = new GroupConfig { Name = "工作", Items = [] };
        return new() { Groups = [common, work], LastGroupId = common.Id };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AppConfig))]
public partial class ConfigJsonContext : JsonSerializerContext;
