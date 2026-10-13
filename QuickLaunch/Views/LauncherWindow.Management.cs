using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuickLaunch.Models;

namespace QuickLaunch.Views;

public partial class LauncherWindow
{
    private Window? _editor;
    public bool HasOpenEditor => _editor?.IsVisible == true;
    private readonly Dictionary<FrameworkElement, (Transform Transform, double Opacity)> _previewElements = [];
    private int _ghostGeneration;
    public double CellSize => ViewModel.CellSize;

    private void RebuildGroups()
    {
        foreach (var old in _grids.Keys.Where(g => !Groups.Contains(g)).ToArray())
        { GroupHost.Children.Remove(_grids[old]); _grids.Remove(old); }
        Tabs.Children.Clear();
        foreach (var group in Groups)
        {
            var tab = new Button { DataContext = group, Tag = group, MinWidth = 70, MaxWidth = 190, BorderThickness = new Thickness(0, 0, 0, 2), ToolTip = group.Name };
            tab.SetBinding(ContentControl.ContentProperty, new Binding(nameof(LaunchGroup.Name)));
            tab.Click += (_, _) => { if (!DragSession.Active) { SelectGroup(group); FocusFirst(); } };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "重命名分组", IsEnabled = CanEdit };
            rename.Click += (_, _) => RenameGroup(group);
            var delete = new MenuItem { Header = "删除分组…", IsEnabled = CanEdit };
            delete.Click += (_, _) => DeleteGroup(group);
            menu.Items.Add(rename); menu.Items.Add(delete);
            menu.Opened += (_, _) => _menuOpen = true;
            menu.Closed += (_, _) => { _menuOpen = false; Dispatcher.BeginInvoke(() => MaybeHide()); };
            tab.ContextMenu = menu; Tabs.Children.Add(tab);
        }
        if (Groups.Count == 0) return;
        if (!Groups.Contains(SelectedGroup)) SelectedGroup = Groups[0];
        SelectGroup(SelectedGroup, preview: DragSession.Active);
    }
    private void EnsureGrid(LaunchGroup group)
    {
        if (_grids.ContainsKey(group)) return;
        var grid = new ItemsControl
        {
            ItemsSource = group.Items, ItemTemplate = (DataTemplate)FindResource("TileTemplate"),
            ItemsPanel = (ItemsPanelTemplate)FindResource("TilePanel"), Visibility = Visibility.Collapsed, Focusable = true
        };
        KeyboardNavigation.SetDirectionalNavigation(grid, KeyboardNavigationMode.Contained);
        _grids.Add(group, grid); GroupHost.Children.Add(grid);
    }
    internal bool ShowEditor(Window editor)
    {
        if (!CanEdit || DragSession.Active) return false;
        _editor = editor; editor.Owner = this; SetSettingsOpen(true);
        try { return editor.ShowDialog() == true; }
        finally { _editor = null; SetSettingsOpen(false); }
    }
    private void AddItemClick(object sender, RoutedEventArgs e) => EditItem(null);
    public void EditItem(LaunchItem? original)
    {
        if (!CanEdit || DragSession.Active || original?.IsPending == true) return;
        var group = SelectedGroup; var dialog = new EditItemDialog(original);
        if (!ShowEditor(dialog) || dialog.Result is not { } result || !Groups.Contains(group)) return;
        try
        {
            if (original is not null) ViewModel.UpdateItem(group, original, result);
            else if (ViewModel.IsDuplicate(group, result)) throw new InvalidOperationException("该组已有相同的启动条目。");
            else group.Items.Add(result);
            SetStatus("条目已更新，将自动保存。"); Services.WindowPlacement.FitContent(this, CellSize, SelectedGroup.Items.Count); _ = LoadIconsAsync(); FocusItem(original?.Id ?? result.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { SetStatus(ex.Message); }
    }
    public void RenameItem(LaunchItem item)
    {
        if (!CanEdit || DragSession.Active || item.IsPending) return;
        var dialog = new NameDialog("重命名条目", item.Name);
        if (ShowEditor(dialog)) { ViewModel.UpdateItem(SelectedGroup, item, item with { Name = dialog.Value }); FocusItem(item.Id); }
    }
    public void DeleteItem(LaunchItem item)
    {
        if (!CanEdit || DragSession.Active || item.IsPending) return;
        ViewModel.DeleteItem(SelectedGroup, item); SetStatus("已删除，5 秒内按 Ctrl+Z 撤销。"); Services.WindowPlacement.FitContent(this, CellSize, SelectedGroup.Items.Count); FocusFirst();
    }
    private string NewGroupName()
    {
        int n = 1; string name = "新分组";
        while (Groups.Any(g => g.Name == name)) name = $"新分组 {++n}";
        return name;
    }
    private void AddGroupClick(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || DragSession.Active) return;
        var dialog = new NameDialog("新建分组", NewGroupName());
        if (ShowEditor(dialog)) { SelectGroup(ViewModel.AddGroup(dialog.Value)); FocusFirst(); }
    }
    public void RenameGroup(LaunchGroup group)
    {
        if (!CanEdit || DragSession.Active) return;
        var dialog = new NameDialog("重命名分组", group.Name);
        if (ShowEditor(dialog)) ViewModel.RenameGroup(group, dialog.Value);
    }
    private void DeleteGroup(LaunchGroup group)
    {
        if (!CanEdit || DragSession.Active) return;
        if (group.Items.Any(i => !i.IsPending))
        {
            SetSettingsOpen(true);
            try { if (MessageBox.Show(this, $"删除“{group.Name}”及其中的条目？此操作不能用单条撤销恢复。", "删除分组", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; }
            finally { SetSettingsOpen(false); }
        }
        ViewModel.RemoveGroup(group); FocusFirst();
    }
    private LaunchItem? MenuItemData(object sender) => sender is MenuItem { Parent: ContextMenu { PlacementTarget: Button { DataContext: LaunchItem item } } } ? item : null;
    private void EditMenuClick(object sender, RoutedEventArgs e) { if (MenuItemData(sender) is { } item) EditItem(item); }
    private void RenameMenuClick(object sender, RoutedEventArgs e) { if (MenuItemData(sender) is { } item) RenameItem(item); }
    private void DeleteMenuClick(object sender, RoutedEventArgs e) { if (MenuItemData(sender) is { } item) DeleteItem(item); }
    private async void LocationMenuClick(object sender, RoutedEventArgs e)
    {
        if (MenuItemData(sender) is not { } item || item.Kind == LaunchKind.Url) return;
        try
        {
            await Task.Run(() =>
            {
                string path = Environment.ExpandEnvironmentVariables(item.Path);
                if (path.Contains('"')) throw new ArgumentException("路径包含非法引号。");
                using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            });
        }
        catch (Exception ex) { SetStatus($"无法打开所在位置：{ex.Message}"); }
    }
    private void RefreshIconMenuClick(object sender, RoutedEventArgs e)
    {
        if (MenuItemData(sender) is { } item) { Icons.Refresh(item); item.Visual.Image = null; _ = LoadIconsAsync(); }
    }
    private void FocusItem(string id)
    {
        _focusedItemId = id;
        Dispatcher.BeginInvoke(() => { if (!_closing && IsVisible) FocusFirst(); });
    }
    public bool IsAddGroupAt(Point point) => new Rect(AddGroupButton.TranslatePoint(new Point(), Root), AddGroupButton.RenderSize).Contains(point);
    public LaunchGroup? MoveToNewGroup(LaunchGroup source, LaunchItem item)
    {
        var group = ViewModel.MoveToNewGroup(source, item, NewGroupName());
        if (group is not null) SelectGroup(group);
        return group;
    }
    public int? GroupSlotAt(Point point)
    {
        Point local = Root.TranslatePoint(point, TabsViewport);
        if (local.Y < 0 || local.Y > TabsViewport.ActualHeight || local.X < 0 || local.X > TabsViewport.ActualWidth) return null;
        int index = 0;
        foreach (Button tab in Tabs.Children)
        {
            double previewOffset = tab.RenderTransform is TranslateTransform transform ? transform.X : 0;
            if (point.X < tab.TranslatePoint(new Point(tab.ActualWidth / 2, 0), Root).X - previewOffset) return index;
            index++;
        }
        return index;
    }
    public void AutoScroll(Point point)
    {
        Point local = Root.TranslatePoint(point, Viewer);
        if (local.X >= 0 && local.X < Viewer.ViewportWidth)
        {
            if (local.Y >= 0 && local.Y < 28) Viewer.ScrollToVerticalOffset(Math.Max(0, Viewer.VerticalOffset - CellSize * .18));
            else if (local.Y <= Viewer.ViewportHeight && local.Y > Viewer.ViewportHeight - 28) Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset + CellSize * .18);
        }
        local = Root.TranslatePoint(point, TabsViewport);
        if (local.Y >= 0 && local.Y < TabsViewport.ActualHeight)
        {
            if (local.X >= 0 && local.X < 24) TabsViewport.ScrollToHorizontalOffset(TabsViewport.HorizontalOffset - 12);
            else if (local.X > TabsViewport.ActualWidth - 24 && local.X < TabsViewport.ActualWidth) TabsViewport.ScrollToHorizontalOffset(TabsViewport.HorizontalOffset + 12);
        }
    }
    public void PreviewInsertion(LaunchGroup source, LaunchItem item, int? slot)
    {
        var previous = _previewElements.Keys.ToDictionary(e => e, e => e.RenderTransform is TranslateTransform t ? new Point(t.X, t.Y) : new Point());
        ClearPreview(); ShowInsertion(slot);
        if (slot is null) return;
        var original = SelectedGroup.Items.ToList(); var projected = original.ToList();
        if (source == SelectedGroup)
        {
            int old = original.FindIndex(i => i.Id == item.Id); if (old < 0) return;
            projected.RemoveAt(old); projected.Insert(DragDrop.ReorderMath.FinalIndex(old, slot.Value, original.Count), item);
        }
        else projected.Insert(slot.Value, item);
        int columns = Math.Max(1, (int)(Viewer.ViewportWidth / CellSize));
        foreach (var button in Descendants<Button>(_grids[SelectedGroup]))
        {
            if (button.DataContext is not LaunchItem entry) continue;
            int old = original.FindIndex(i => i.Id == entry.Id), next = projected.FindIndex(i => i.Id == entry.Id);
            _previewElements[button] = (button.RenderTransform, button.Opacity);
            if (entry.Id == item.Id) { button.Opacity = .2; continue; }
            Point start = previous.GetValueOrDefault(button);
            var transform = new TranslateTransform(start.X, start.Y); button.RenderTransform = transform;
            double x = (next % columns - old % columns) * CellSize, y = (next / columns - old / columns) * CellSize;
            if (SystemParameters.ClientAreaAnimation)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else { transform.X = x; transform.Y = y; }
        }
    }
    public void ClearPreview()
    {
        foreach (var pair in _previewElements) { pair.Key.RenderTransform = pair.Value.Transform; pair.Key.Opacity = pair.Value.Opacity; }
        _previewElements.Clear();
    }
    public void PreviewGroupInsertion(LaunchGroup source, int? slot)
    {
        ClearPreview();
        if (slot is null) return;
        var old = Groups.ToList(); int index = old.IndexOf(source); if (index < 0) return;
        var projected = old.ToList(); projected.RemoveAt(index);
        projected.Insert(DragDrop.ReorderMath.FinalIndex(index, slot.Value, old.Count), source);
        var buttons = Tabs.Children.OfType<Button>().ToDictionary(b => (LaunchGroup)b.Tag);
        var positions = new Dictionary<LaunchGroup, double>(); double left = 0;
        foreach (var group in projected) { positions[group] = left; left += buttons[group].ActualWidth + buttons[group].Margin.Left + buttons[group].Margin.Right; }
        left = 0;
        foreach (var group in old)
        {
            var button = buttons[group]; _previewElements[button] = (button.RenderTransform, button.Opacity);
            if (group == source) button.Opacity = .25;
            else
            {
                var translation = new TranslateTransform(); button.RenderTransform = translation;
                if (SystemParameters.ClientAreaAnimation) translation.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(positions[group] - left, TimeSpan.FromMilliseconds(150)));
                else translation.X = positions[group] - left;
            }
            left += button.ActualWidth + button.Margin.Left + button.Margin.Right;
        }
    }
    public void HighlightDropTab(LaunchGroup? group)
    { foreach (Button tab in Tabs.Children) tab.SetResourceReference(BackgroundProperty, tab.Tag == group ? "HoverBrush" : "PanelBrush"); }
    public void AfterDrag() => Dispatcher.BeginInvoke(() => { if (IsVisible) Services.WindowPlacement.FitContent(this, CellSize, SelectedGroup.Items.Count); MaybeHide(); });
    public void FlyGhostTo(LaunchItem item, LaunchGroup target)
    {
        int generation = ++_ghostGeneration;
        Dispatcher.BeginInvoke(() =>
        {
            if (generation != _ghostGeneration || !IsVisible) return;
            UpdateLayout();
            FrameworkElement? destination = target == SelectedGroup ? Descendants<Button>(_grids[target]).FirstOrDefault(b => b.DataContext is LaunchItem i && i.Id == item.Id)
                : Tabs.Children.OfType<Button>().FirstOrDefault(b => b.Tag == target);
            if (destination is null || !SystemParameters.ClientAreaAnimation) { HideGhost(); return; }
            Point point = destination.TranslatePoint(new Point(8, 8), Root);
            var x = new DoubleAnimation(Canvas.GetLeft(Ghost), point.X, TimeSpan.FromMilliseconds(120));
            var y = new DoubleAnimation(Canvas.GetTop(Ghost), point.Y, TimeSpan.FromMilliseconds(120));
            y.Completed += (_, _) => { if (generation == _ghostGeneration) HideGhost(); };
            Ghost.BeginAnimation(Canvas.LeftProperty, x); Ghost.BeginAnimation(Canvas.TopProperty, y);
        });
    }
    public void FlashDuplicate(LaunchItem item)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var button = Descendants<Button>(_grids[SelectedGroup]).FirstOrDefault(b => b.DataContext is LaunchItem i && i.Id == item.Id);
        button?.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(100)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), FillBehavior = FillBehavior.Stop });
    }
}
