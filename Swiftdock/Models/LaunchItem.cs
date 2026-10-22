using System.Text.Json.Serialization;
using Swiftdock.Services;
using Swiftdock.ViewModels;

namespace Swiftdock.Models;

public enum LaunchKind { App, File, Folder, Url }

// Immutable records can be copied into configuration and launch snapshots on the UI thread.
public sealed record LaunchItem([property: JsonRequired] string Name, [property: JsonRequired] string Path, [property: JsonRequired] LaunchKind Kind,
    string Arguments = "", string WorkingDirectory = "", bool RunAsAdmin = false)
{
    [JsonRequired] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public long LaunchCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(IconValueConverter))]
    public string? Icon { get; init; }
    [JsonIgnore] public string? ErrorMessage { get; init; }
    [JsonIgnore] public IconVisual Visual { get; init; } = new();
    [JsonIgnore] public bool IsPending { get; init; }
    [JsonIgnore] public string ToolTip => ErrorMessage is null ? Path : $"{Path}\n{ErrorMessage}";
    [JsonIgnore] public string Glyph => IsPending ? "…" : ErrorMessage is not null ? "!" : Kind switch
    {
        LaunchKind.Folder => "▤", LaunchKind.Url => "↗", LaunchKind.File => "▧", _ => "▣"
    };
}
