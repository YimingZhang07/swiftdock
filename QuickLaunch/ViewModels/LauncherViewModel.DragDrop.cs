using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GongSolutions.Wpf.DragDrop;
using QuickLaunch.Diagnostics;
using QuickLaunch.DragDrop;
using QuickLaunch.Models;

namespace QuickLaunch.ViewModels;

public sealed partial class LauncherViewModel : IDragSource, IDropTarget
{
    private readonly DispatcherTimer _dragHover = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private LaunchGroup? _dragOriginalGroup, _dragSourceGroup, _dragHoverGroup;
    private object? _dragData, _hoverSurface;
    private bool _cancelDrag, _dropCommitted;
    public IDropTarget NewGroupDropTarget { get; private set; } = null!;
    public bool ExternalDragActive { get; private set; }
    internal Task PendingDrop { get; private set; } = Task.CompletedTask;

    private void InitializeDragDrop()
    {
        NewGroupDropTarget = new NewGroupTarget(this);
        _dragHover.Tick += (_, _) =>
        {
            _dragHover.Stop();
            if (CanEdit && !_cancelDrag && _dragHoverGroup is { } group && Groups.Contains(group))
                SetSelectedGroup(group, preview: true);
        };
        CancelDragRequested += RequestDragCancel;
    }

    public bool CanStartDrag(IDragInfo info) => CanEdit && CanInteract && !HasOpenEditor && !MenuOpen
        && info.MouseButton == MouseButton.Left && (info.SourceItem switch
        {
            LaunchItem { IsPending: false } item => Groups.Any(g => ReferenceEquals(g.Items, info.SourceCollection) && g.Items.Contains(item)),
            LaunchGroup group => (info.SourceCollection is null || ReferenceEquals(info.SourceCollection, Groups)) && Groups.Contains(group),
            _ => false
        });

    public void StartDrag(IDragInfo info)
    {
        info.Effects = DragDropEffects.None;
        if (!CanStartDrag(info)) return;
        _dragData = info.Data = info.SourceItem;
        _dragSourceGroup = Groups.FirstOrDefault(g => ReferenceEquals(g.Items, info.SourceCollection));
        _dragOriginalGroup = SelectedGroup;
        _cancelDrag = _dropCommitted = false;
        ClearAllSelections(); SetDragActive(true);
        info.Effects = DragDropEffects.Move;
        SetStatus("拖动中 · Esc 取消 · 松开提交");
    }

    public void RequestDragCancel() { _cancelDrag = true; StopDragHover(); ExternalDragActive = false; }

    // OLE owns mouse capture and Escape. Keep DragActive until OLE returns so the same
    // Escape cannot clear selection or hide the window while a drag is being cancelled.
    public void QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (DragActive && (e.EscapePressed || _cancelDrag || !CanEdit || _closing))
        { RequestDragCancel(); e.Action = DragAction.Cancel; e.Handled = true; }
    }

    public void DragCancelled() => FinishDrag(cancelled: true);
    public void DragDropOperationFinished(DragDropEffects result, IDragInfo info) => FinishDrag(!_dropCommitted);
    public void Dropped(IDropInfo info) { }
    public bool TryCatchOccurredException(Exception exception)
    {
        FinishDrag(cancelled: true);
        SetStatus($"移动失败：{exception.Message}");
        return true;
    }

    private void FinishDrag(bool cancelled)
    {
        StopDragHover(); ExternalDragActive = false;
        if (!DragActive) return;
        if (cancelled && !_dropCommitted)
        {
            if (_dragOriginalGroup is { } original && Groups.Contains(original)) SetSelectedGroup(original, preview: true);
            _log.DragCancel(); SetStatus("已取消移动，原顺序不变。");
        }
        _dragData = null; _dragOriginalGroup = _dragSourceGroup = null;
        SetDragActive(false); ContentChanged?.Invoke();
    }

    public void DragEnter(IDropInfo info) => DragOver(info);
    public void DragOver(IDropInfo info) => DragOver(info, newGroup: false);
    private void DragOver(IDropInfo info, bool newGroup)
    {
        info.Effects = DragDropEffects.None; info.DropTargetAdorner = null;
        ExternalDragActive = info.DragInfo is null;
        var tab = !newGroup && ReferenceEquals(info.TargetCollection, Groups) ? info.TargetItem as LaunchGroup : null;
        var target = tab ?? Groups.FirstOrDefault(g => ReferenceEquals(g.Items, info.TargetCollection));
        bool internalDrag = DragActive && !_cancelDrag && ReferenceEquals(info.Data, _dragData);
        if (CanEdit && !_closing && !HasOpenEditor && !MenuOpen)
        {
            if (internalDrag && info.Data is LaunchGroup group && !newGroup && ReferenceEquals(info.TargetCollection, Groups)
                && Groups.Contains(group) && info.InsertIndex >= 0 && info.InsertIndex <= Groups.Count)
                info.Effects = DragDropEffects.Move;
            else if (internalDrag && info.Data is LaunchItem { IsPending: false } item && _dragSourceGroup is { } source
                && Groups.Contains(source) && source.Items.Contains(item)
                && (newGroup || target is not null && (tab is not null || info.InsertIndex >= 0 && info.InsertIndex <= target.Items.Count))
                && (target is null || source == target || !State.IsDuplicate(target, item)))
                info.Effects = DragDropEffects.Move;
            else if (!DragActive && !Importing && !newGroup && target is not null && info.DragInfo is null
                && (tab is not null || info.InsertIndex >= 0 && info.InsertIndex <= target.Items.Count)
                && info.Data is IDataObject data && DropParser.Supports(data))
            {
                var allowed = info is LauncherDropInfo ole ? ole.AllowedEffects : DragDropEffects.Copy | DragDropEffects.Link;
                info.Effects = allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
                    : allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
            }
        }
        if (info.Effects != DragDropEffects.None)
            info.DropTargetAdorner = newGroup || tab is not null && info.Data is not LaunchGroup
                ? DropTargetAdorners.Highlight : DropTargetAdorners.Insert;
        HoverTab(info.Effects != DragDropEffects.None && info.Data is LaunchItem ? tab : null, info.VisualTarget);
    }

    private void HoverTab(LaunchGroup? group, object surface)
    {
        _hoverSurface = surface;
        if (group == _dragHoverGroup) return;
        _dragHover.Stop(); _dragHoverGroup = group;
        if (group is not null && group != SelectedGroup) _dragHover.Start();
    }
    private void StopDragHover() { _dragHover.Stop(); _dragHoverGroup = null; _hoverSurface = null; }
    public void DragLeave(IDropInfo info)
    {
        // Gong defers leave notifications. A late leave from the previous surface must
        // not cancel the dwell timer of the tab the pointer has just entered.
        if (!ReferenceEquals(info.VisualTarget, _hoverSurface)) return;
        StopDragHover(); ExternalDragActive = false;
    }

    public void Drop(IDropInfo info) => Drop(info, newGroup: false);
    private void Drop(IDropInfo info, bool newGroup)
    {
        DragOver(info, newGroup);
        StopDragHover(); ExternalDragActive = false;
        if (info.Effects == DragDropEffects.None) return;
        var tab = ReferenceEquals(info.TargetCollection, Groups) ? info.TargetItem as LaunchGroup : null;
        var target = tab ?? Groups.FirstOrDefault(g => ReferenceEquals(g.Items, info.TargetCollection));
        if (info.Data is LaunchGroup group)
        {
            int old = Groups.IndexOf(group);
            State.MoveGroup(old, info.InsertIndex > old ? info.InsertIndex - 1 : info.InsertIndex);
            _dropCommitted = true;
        }
        else if (info.Data is LaunchItem item && _dragSourceGroup is { } source)
        {
            if (newGroup) target = MoveToNewGroup(source, item);
            else if (!State.MoveItem(source, target!, item, tab is not null ? target!.Items.Count : info.InsertIndex)) target = null;
            if (target is null) { info.Effects = DragDropEffects.None; return; }
            _dropCommitted = true;
            State.CommitGroup(SelectedGroup.Id);
            _log.DragCommit(item.Id, source.Name, target.Name);
            SetStatus($"已移动 {item.Name}，将自动保存。");
        }
        else if (info.Data is IDataObject data)
        {
            // Capture OLE data synchronously before the source releases it; reuse the
            // existing pending/cancel/duplicate/status pipeline after that boundary.
            try
            {
                var watch = Stopwatch.StartNew();
                var snapshot = DropParser.Capture(data);
                _log.DropCapture(snapshot.Formats, snapshot.Paths.Length, watch.Elapsed.TotalMilliseconds);
                PendingDrop = ImportAsync(target!, tab is not null ? target!.Items.Count : info.InsertIndex, snapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            { info.Effects = DragDropEffects.None; ReportImportFailure(ex); }
        }
    }

    private sealed class NewGroupTarget(LauncherViewModel owner) : IDropTarget
    {
        public void DragEnter(IDropInfo info) => DragOver(info);
        public void DragOver(IDropInfo info) => owner.DragOver(info, newGroup: true);
        public void DragLeave(IDropInfo info) => owner.DragLeave(info);
        public void Drop(IDropInfo info) => owner.Drop(info, newGroup: true);
    }
}
