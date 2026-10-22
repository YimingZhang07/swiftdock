using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows;
using Swiftdock.Services;
using Swiftdock.Models;
using Swiftdock.ViewModels;

namespace Swiftdock.Views;

public partial class LauncherWindow
{
    public bool HasOpenEditor => _dialogs.HasOpenEditor;
    public double CellSize => ViewModel.CellSize;

    private void AddGroupClick(object sender, RoutedEventArgs e) => Launcher.AddGroupCommand.Execute(null);
    private void TabDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Launcher.RenameGroupCommand.Execute((LaunchGroup)((Button)sender).Tag);
    }
    private void TabLoaded(object sender, RoutedEventArgs e)
    {
        var tab = (Button)sender;
        var group = (LaunchGroup)tab.Tag;
        var menu = new ContextMenu();
        menu.Items.Add(new LocalizedMenuItem(L.Text("RenameGroup")) { Command = Launcher.RenameGroupCommand, CommandParameter = group });
        var delete = new LocalizedMenuItem(L.Text("DeleteGroup321")) { Command = Launcher.DeleteGroupCommand, CommandParameter = group };
        delete.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
        menu.Items.Add(delete);
        menu.Opened += (_, _) => Launcher.MenuOpen = true;
        menu.Closed += (_, _) => Launcher.MenuOpen = false;
        tab.ContextMenu = menu;
    }

    private void RebuildGroups()
    {
        foreach (var old in _grids.Keys.Where(g => !Groups.Contains(g)).ToArray())
        { GroupHost.Children.Remove(_grids[old]); _grids.Remove(old); }
        if (Groups.Count == 0) return;
        ApplySelectedGroup(Launcher.DragActive);
    }
    private void EnsureGrid(LaunchGroup group)
    {
        if (_grids.ContainsKey(group)) return;
        var grid = new TileListBox
        {
            ItemsSource = group.Items, ItemContainerStyle = (Style)FindResource("TileItem"),
            Style = (Style)FindResource("TileList"), Viewport = Viewer,
            ItemsPanel = (ItemsPanelTemplate)FindResource("TilePanel"), Visibility = Visibility.Collapsed,
            FocusVisualStyle = null, Launcher = Launcher
        };
        grid.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
            new Binding(nameof(TileSelection.Item)) { Source = Launcher.SelectionFor(group), Mode = BindingMode.TwoWay });
        grid.SetBinding(MinHeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = Viewer });
        GongSolutions.Wpf.DragDrop.DragDrop.SetDropTargetScrollViewer(grid, Viewer);
        _grids.Add(group, grid); GroupHost.Children.Add(grid);
    }
    private void RevealSelection()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !IsVisible) return;
            FocusSelectionList(); SelectedList.RevealSelection();
        });
    }
    public void FlashDuplicate(LaunchItem item)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var tile = Descendants<ListBoxItem>(_grids[SelectedGroup]).FirstOrDefault(b => b.DataContext is LaunchItem i && i.Id == item.Id);
        tile?.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(100)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), FillBehavior = FillBehavior.Stop });
    }
}
