using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickLaunch.Models;

namespace QuickLaunch.Views;

// Selection belongs to ListBox; keyboard focus stays on the list, including when it is empty.
// WPF's default navigation focuses item containers and pages an internal ScrollViewer. Our
// non-focusable wrap tiles instead use the outer viewport for row and page distances.
public sealed class TileListBox : ListBox
{
    internal ScrollViewer Viewport { get; set; } = null!;

    protected override DependencyObject GetContainerForItemOverride() => new TileListBoxItem();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is TileListBoxItem;

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        // Launch results and edits replace immutable records. Keep the same selected identity
        // through that replacement; an actual removal still clears ListBox selection normally.
        bool retain = e.Action == NotifyCollectionChangedAction.Replace && SelectedItem is LaunchItem selected
            && e.OldItems?[0] is LaunchItem old && e.NewItems?[0] is LaunchItem next
            && selected.Id == old.Id && old.Id == next.Id;
        base.OnItemsChanged(e);
        if (retain) SelectedItem = e.NewItems![0];
    }

    internal static bool IsNavigationKey(Key key, ModifierKeys modifiers) =>
        (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0
        && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    internal void SelectFirst() => SelectEnabled(0, 1);

    internal void HandleNavigation(KeyEventArgs e)
    {
        if (!IsNavigationKey(e.Key, e.KeyboardDevice.Modifiers)) return;
        e.Handled = true;
        Focus();
        if (SelectedIndex < 0) { SelectFirst(); return; }
        UpdateLayout();
        var panel = LauncherWindow.Descendants<WrapPanel>(this).Single();
        int columns = Math.Max(1, (int)(panel.ActualWidth / panel.ItemWidth));
        int page = Math.Max(1, (int)(Viewport.ViewportHeight / panel.ItemHeight)) * columns;
        int index = SelectedIndex;
        int target = e.Key switch
        {
            Key.Left => index - 1, Key.Right => index + 1,
            Key.Up => index < columns ? index : index - columns,
            Key.Down => index / columns == (Items.Count - 1) / columns ? index : index + columns,
            Key.Home => 0, Key.End => Items.Count - 1,
            Key.PageUp => index - page, Key.PageDown => index + page,
            _ => index
        };
        SelectEnabled(Math.Clamp(target, 0, Items.Count - 1), e.Key is Key.Left or Key.Up or Key.End or Key.PageUp ? -1 : 1);
    }

    private void SelectEnabled(int index, int step)
    {
        UpdateLayout();
        for (; index >= 0 && index < Items.Count; index += step)
        {
            if (ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem { IsEnabled: true }) continue;
            SelectedIndex = index;
            RevealSelection();
            return;
        }
    }

    internal void RevealSelection()
    {
        UpdateLayout();
        if (SelectedItem is { } item && ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem tile) tile.BringIntoView();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // The window routes navigation here in PreviewKeyDown, even from a header button.
        // Do not let ListBox's Space behavior create a hidden item-focus selection.
        if (e.Key == Key.Tab) base.OnKeyDown(e);
    }
}

public sealed class TileListBoxItem : ListBoxItem
{
    private static readonly DependencyPropertyKey IsPressedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsPressed), typeof(bool), typeof(TileListBoxItem), new PropertyMetadata(false));
    public static readonly DependencyProperty IsPressedProperty = IsPressedPropertyKey.DependencyProperty;
    public bool IsPressed { get => (bool)GetValue(IsPressedProperty); internal set => SetValue(IsPressedPropertyKey, value); }

    // ContextMenuService still handles the right-button release; do not let ListBoxItem select.
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { }
}
