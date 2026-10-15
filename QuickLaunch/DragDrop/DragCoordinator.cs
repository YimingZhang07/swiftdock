using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickLaunch.Models;
using QuickLaunch.Views;

namespace QuickLaunch.DragDrop;

public sealed class DragCoordinator
{
    private readonly LauncherWindow _view;
    private readonly DispatcherTimer _hover, _scroll;
    private LaunchGroup? _source, _hoverTarget, _tab, _startGroup;
    private LaunchItem? _item;
    private TileListBoxItem? _pressedTile;
    private Point _start;
    private bool _ending;
    private string? _previewKey;
    private TaskCompletionSource? _done;
    public bool Active => _item is not null || _tab is not null;
    public bool Dragging { get; private set; }
    public Task WhenIdle => Active && _done is not null ? _done.Task : Task.CompletedTask;
    internal Func<MouseEventArgs, Point> PointerPosition { get; set; }
    internal Func<bool> CapturePointer { get; set; }

    public DragCoordinator(LauncherWindow view)
    {
        _view = view;
        PointerPosition = e => e.GetPosition(view.CaptureRoot);
        CapturePointer = () => view.CaptureRoot.CaptureMouse();
        _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hover.Tick += (_, _) => { _hover.Stop(); if (_hoverTarget is not null) { _view.SelectGroup(_hoverTarget, preview: true); _previewKey = null; } };
        _scroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _scroll.Tick += (_, _) => { if (Dragging) { var point = Mouse.GetPosition(_view.CaptureRoot); _view.AutoScroll(point); Preview(point); } };
        view.CaptureRoot.PreviewMouseLeftButtonDown += Down;
        view.CaptureRoot.PreviewMouseMove += Move;
        view.CaptureRoot.PreviewMouseLeftButtonUp += Up;
        view.CaptureRoot.LostMouseCapture += (_, _) => { if (!_ending && Active) Cancel(); };
    }
    internal void BeginPendingItemForDiagnostics(LaunchItem item)
    {
        _view.Dispatcher.VerifyAccess();
        if (!_view.CanEdit || Active || item.IsPending || !_view.SelectedGroup.Items.Contains(item))
            throw new InvalidOperationException("无法开始诊断拖动。");
        _item = item; _source = _startGroup = _view.SelectedGroup; _start = default;
        _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (Active || e.OriginalSource is not DependencyObject target) return;
        var button = LauncherWindow.Ancestor<Button>(target);
        var tile = LauncherWindow.Ancestor<TileListBoxItem>(target);
        if (tile?.DataContext is LaunchItem item && !item.IsPending)
        {
            _item = item; _source = _view.SelectedGroup;
            _view.ClearTileSelection();
            _pressedTile = tile; tile.IsPressed = true;
        }
        else if (_view.CanEdit && button?.Tag is LaunchGroup group)
        {
            if (e.ClickCount == 2) { e.Handled = true; _view.RenameGroup(group); return; }
            _tab = group;
        }
        else return;
        _startGroup = _view.SelectedGroup; _start = PointerPosition(e);
        _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        button?.Focus();
        if (!CapturePointer()) Clear();
        e.Handled = true;
    }
    private void Move(object sender, MouseEventArgs e)
    {
        if (!Active || !_view.CanEdit) return;
        var point = PointerPosition(e);
        if (!Dragging && (Math.Abs(point.X - _start.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - _start.Y) > SystemParameters.MinimumVerticalDragDistance))
        { Dragging = true; if (_pressedTile is not null) _pressedTile.IsPressed = false; _scroll.Start(); _view.SetStatus("拖动中 · Esc 取消 · 松开提交"); }
        if (Dragging) Preview(point);
        e.Handled = true;
    }
    private void Preview(Point point)
    {
        _view.ShowGhost(_item?.Name ?? _tab!.Name, point, _item?.Visual.Image);
        if (_tab is not null)
        {
            int? groupSlot = _view.GroupSlotAt(point);
            string key = "tabs:" + groupSlot;
            if (_previewKey != key) { _previewKey = key; _view.PreviewGroupInsertion(_tab, groupSlot); }
            return;
        }
        var target = _view.GroupAt(point);
        _view.HighlightDropTab(target);
        if (target != _hoverTarget)
        {
            _hover.Stop(); _hoverTarget = target;
            if (target is not null && target != _view.SelectedGroup) _hover.Start();
        }
        int? slot = _view.SlotAt(point);
        string preview = _view.SelectedGroup.Id + ":" + slot;
        if (_previewKey != preview) { _previewKey = preview; _view.PreviewInsertion(_source!, _item!, slot); }
        else _view.ShowInsertion(slot);
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!Active) return;
        var point = PointerPosition(e);
        e.Handled = true;
        if (_tab is { } tab)
        {
            if (!Dragging) { bool clicked = _view.GroupAt(point) == tab; Clear(); if (clicked) _view.SelectGroup(tab); return; }
            int? slot = _view.GroupSlotAt(point);
            int old = _view.Groups.ToList().IndexOf(tab);
            if (slot is null || old < 0) { Cancel(); return; }
            try { _view.ViewModel.MoveGroup(old, ReorderMath.FinalIndex(old, slot.Value, _view.Groups.Count)); }
            finally { Clear(); }
            return;
        }
        var item = _item!; var source = _source!;
        if (!Dragging)
        {
            bool launch = _view.ItemAt(point)?.Id == item.Id; Clear();
            if (launch) _ = _view.LaunchAsync(item);
            return;
        }
        try
        {
            LaunchGroup? target;
            if (_view.IsAddGroupAt(point)) target = _view.MoveToNewGroup(source, item);
            else
            {
                var tabTarget = _view.GroupAt(point); target = tabTarget ?? _view.SelectedGroup;
                int? slot = tabTarget is not null ? target.Items.Count : _view.SlotAt(point);
                if (slot is null) { Cancel(); return; }
                if (!_view.ViewModel.MoveItem(source, target, item, slot.Value))
                { Cancel(); _view.SetStatus("未移动：目标已有相同条目或源条目已变化。"); return; }
            }
            if (target is null) { Cancel(); return; }
            _view.ViewModel.CommitGroup(_view.SelectedGroup.Id);
            _view.Log.Write("drag.commit", new { item.Id, source = source.Name, target = target.Name });
            _view.SetStatus($"已移动 {item.Name}，将自动保存。");
            Clear(keepGhost: true); _view.FlyGhostTo(item, target);
        }
        catch (Exception ex) { Cancel(); _view.SetStatus($"移动失败：{ex.Message}"); }
    }
    public void Cancel()
    {
        if (!Active) return;
        var original = _startGroup; Clear();
        if (original is not null && _view.Groups.Contains(original)) _view.SelectGroup(original, preview: true);
        _view.Log.Write("drag.cancel"); _view.SetStatus("已取消移动，原顺序不变。");
    }
    private void Clear(bool keepGhost = false)
    {
        _ending = true;
        if (_pressedTile is not null) _pressedTile.IsPressed = false;
        _pressedTile = null;
        _hover.Stop(); _scroll.Stop(); _hoverTarget = null; _previewKey = null;
        _item = null; _source = _tab = _startGroup = null; Dragging = false;
        _view.ClearPreview(); _view.HighlightDropTab(null); _view.ShowInsertion(null);
        if (Mouse.Captured == _view.CaptureRoot) _view.CaptureRoot.ReleaseMouseCapture();
        if (!keepGhost) _view.HideGhost();
        _ending = false; _done?.TrySetResult(); _view.AfterDrag();
    }
}
