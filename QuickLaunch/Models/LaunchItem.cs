namespace QuickLaunch.Models;

public enum LaunchKind { App, File, Folder, Url }

// M0 deliberately has no persisted configuration. These records are immutable launch snapshots.
public sealed record LaunchItem(string Name, string Path, LaunchKind Kind,
    string Arguments = "", string WorkingDirectory = "", bool RunAsAdmin = false)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Glyph => Kind switch
    {
        LaunchKind.Folder => "▤", LaunchKind.Url => "↗", LaunchKind.File => "▧", _ => "▣"
    };
}
