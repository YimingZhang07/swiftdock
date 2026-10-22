using System.Windows;
using GongSolutions.Wpf.DragDrop;
using Swiftdock.Views;

namespace Swiftdock.DragDrop;

// IDropInfo does not expose the source's allowed effects. Preserve that OLE field
// while retaining Gong's target/adorner plumbing. Tile gaps use the same distributed
// columns as layout and keyboard navigation, including the incomplete last row.
public sealed class LauncherDropInfoBuilder : IDropInfoBuilder
{
    public IDropInfo CreateDropInfo(object sender, DragEventArgs e, IDragInfo dragInfo, EventType eventType) =>
        new LauncherDropInfo(sender, e, dragInfo, eventType);
}

internal sealed class LauncherDropInfo : DropInfo
{
    public DragDropEffects AllowedEffects { get; }
    internal LauncherDropInfo(object sender, DragEventArgs e, IDragInfo dragInfo, EventType eventType)
        : base(sender, e, dragInfo, eventType)
    {
        AllowedEffects = e.AllowedEffects;
        if (VisualTarget is not TileListBox list || LauncherWindow.Descendants<TilePanel>(list).FirstOrDefault() is not { } panel) return;
        var (index, after) = panel.Geometry.DropTarget(e.GetPosition(panel), list.Items.Count);
        InsertIndex = list.Items.Count == 0 ? 0 : index + (after ? 1 : 0);
        InsertPosition = after ? RelativeInsertPosition.AfterTargetItem : RelativeInsertPosition.BeforeTargetItem;
        VisualTargetItem = (UIElement?)list.ItemContainerGenerator.ContainerFromIndex(index);
        TargetItem = list.Items.Count == 0 ? null : list.Items[index];
    }
}
