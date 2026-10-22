using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Swiftdock.Models;

public sealed class LaunchGroup(string name, IEnumerable<LaunchItem> items, string? id = null) : ObservableObject
{
    public string Id { get; } = id ?? Guid.NewGuid().ToString("N");
    private string _name = name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public ObservableCollection<LaunchItem> Items { get; } = new(items);
}
