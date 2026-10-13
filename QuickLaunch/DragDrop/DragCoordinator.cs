using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickLaunch.Models;
using QuickLaunch.Views;

namespace QuickLaunch.DragDrop;

// One capture owner outside group views. Preview changes selection, never item collections.
public sealed class DragCoordinator
{
    private readonly LauncherWindow _view;
    private readonly DispatcherTimer _hover;
    private LaunchGroup? _source, _hoverTarget;
    private LaunchItem? _item;
    private Point _start;
    private bool _ending;
    public bool Active => _item is not null;
    public bool Dragging { get; private set; }

    public DragCoordinator(LauncherWindow view)
    {
        _view = view;
        _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hover.Tick += (_, _) => { _hover.Stop(); if (_hoverTarget is not null) _view.SelectGroup(_hoverTarget); };
        view.CaptureRoot.PreviewMouseLeftButtonDown += Down;
        view.CaptureRoot.PreviewMouseMove += Move;
        view.CaptureRoot.PreviewMouseLeftButtonUp += Up;
        view.CaptureRoot.LostMouseCapture += (_, _) => { if (!_ending && Active) Cancel(); };
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (_view.Importing || Active || e.OriginalSource is not DependencyObject target) return;
        var button = LauncherWindow.Ancestor<Button>(target);
        if (button?.DataContext is not LaunchItem item) return;
        _item = item;
        _source = _view.SelectedGroup;
        _start = e.GetPosition(_view.CaptureRoot);
        button.Focus();
        if (!_view.CaptureRoot.CaptureMouse()) Clear();
        e.Handled = true;
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!Active) return;
        Point point = e.GetPosition(_view.CaptureRoot);
        if (!Dragging && (Math.Abs(point.X - _start.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(point.Y - _start.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            Dragging = true;
            _view.SetStatus($"正在移动 {_item!.Name} · Esc 取消");
        }
        if (!Dragging) return;
        _view.ShowGhost(_item!.Name, point);
        var tab = _view.GroupAt(point);
        if (tab != _hoverTarget)
        {
            _hover.Stop();
            _hoverTarget = tab;
            if (tab is not null && tab != _view.SelectedGroup) _hover.Start();
        }
        int? slot = _view.SlotAt(point);
        _view.ShowInsertion(slot);
        e.Handled = true;
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (_item is null || _source is null) return;
        var item = _item;
        var source = _source;
        Point point = e.GetPosition(_view.CaptureRoot);
        if (!Dragging)
        {
            bool clickedOriginal = _view.ItemAt(point)?.Id == item.Id;
            Clear();
            if (clickedOriginal) _ = _view.LaunchAsync(item);
        }
        else
        {
            var targetTab = _view.GroupAt(point);
            var target = targetTab ?? _view.SelectedGroup;
            int? slot = targetTab is not null ? target.Items.Count : _view.SlotAt(point);
            int oldIndex = source.Items.IndexOf(item);
            if (slot is null || oldIndex < 0) Cancel();
            else
            {
                try
                {
                    if (target == source)
                    {
                        int index = ReorderMath.FinalIndex(oldIndex, slot.Value, source.Items.Count);
                        if (index != oldIndex) source.Items.Move(oldIndex, index);
                    }
                    else
                    {
                        source.Items.RemoveAt(oldIndex);
                        try { target.Items.Insert(slot.Value, item); }
                        catch { source.Items.Insert(oldIndex, item); throw; }
                    }
                    _view.Log.Write("drag.commit", new { item.Id, source = source.Name, target = target.Name, slot });
                    _view.SetStatus($"已移动 {item.Name}（M0 仅保存在本次会话）");
                }
                finally { Clear(); }
            }
        }
        e.Handled = true;
    }

    public void Cancel()
    {
        if (!Active) return;
        var source = _source;
        Clear();
        if (source is not null) _view.SelectGroup(source);
        _view.Log.Write("drag.cancel");
        _view.SetStatus("已取消移动，数据未改变");
    }

    private void Clear()
    {
        _ending = true;
        _hover.Stop();
        _hoverTarget = null;
        _item = null;
        _source = null;
        Dragging = false;
        if (Mouse.Captured == _view.CaptureRoot) _view.CaptureRoot.ReleaseMouseCapture();
        _view.HideGhost();
        _view.ShowInsertion(null);
        _ending = false;
    }
}
