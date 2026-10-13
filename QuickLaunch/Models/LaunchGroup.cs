using System.Collections.ObjectModel;

namespace QuickLaunch.Models;

public sealed class LaunchGroup(string name, IEnumerable<LaunchItem> items, string? id = null)
{
    public string Id { get; } = id ?? Guid.NewGuid().ToString("N");
    public string Name { get; } = name;
    public ObservableCollection<LaunchItem> Items { get; } = new(items);
}
