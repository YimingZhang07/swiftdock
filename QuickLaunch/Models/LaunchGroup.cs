using System.Collections.ObjectModel;

namespace QuickLaunch.Models;

public sealed class LaunchGroup(string name, IEnumerable<LaunchItem> items)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; } = name;
    public ObservableCollection<LaunchItem> Items { get; } = new(items);
}
