using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GongSolutions.Wpf.DragDrop;
using QuickLaunch.DragDrop;
using QuickLaunch.Models;
using Xunit;

namespace QuickLaunch.Tests;

public sealed partial class LauncherViewModelTests
{
    [Fact]
    public Task GongEveryInsertionSlotMatchesRemoveThenInsert() => OnModel(h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup;
        for (int count = 1; count <= 12; count++)
        {
            var items = Enumerable.Range(0, count).Select(i => new LaunchItem($"Item {i}", $"https://example.com/{i}", LaunchKind.Url)).ToArray();
            for (int source = 0; source < count; source++)
            for (int slot = 0; slot <= count; slot++)
            {
                group.Items.Clear(); foreach (var item in items) group.Items.Add(item);
                var expected = items.ToList(); expected.Insert(slot, items[source]); expected.RemoveAt(source < slot ? source : source + 1);
                var drag = new DragStub(items[source], group.Items); vm.StartDrag(drag);
                var drop = new DropStub(drag, group.Items, slot); vm.DragOver(drop);
                Assert.Equal(DragDropEffects.Move, drop.Effects); Assert.Equal(DropTargetAdorners.Insert, drop.DropTargetAdorner);
                vm.Drop(drop); vm.DragDropOperationFinished(drop.Effects, drag);
                Assert.Equal(expected.Select(i => i.Id), group.Items.Select(i => i.Id)); Assert.False(vm.DragActive);
            }
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task GongTabAppendPlusAndGroupReorderUseTheModel() => OnModel(h =>
    {
        var vm = h.Model; var source = vm.SelectedGroup; var target = vm.Groups[1]; var item = source.Items[0];
        var drag = new DragStub(item, source.Items); vm.StartDrag(drag);
        var drop = new DropStub(drag, vm.Groups, 0) { TargetItem = target };
        vm.DragOver(drop); Assert.Equal(DropTargetAdorners.Highlight, drop.DropTargetAdorner);
        vm.Drop(drop); vm.DragDropOperationFinished(drop.Effects, drag);
        Assert.Same(item, target.Items[^1]); Assert.DoesNotContain(item, source.Items);

        drag = new(item, target.Items); vm.StartDrag(drag);
        drop = new(drag, null, 0);
        vm.NewGroupDropTarget.DragOver(drop); Assert.Equal(DragDropEffects.Move, drop.Effects);
        vm.NewGroupDropTarget.Drop(drop); vm.DragDropOperationFinished(drop.Effects, drag);
        Assert.Same(item, Assert.Single(vm.SelectedGroup.Items)); Assert.DoesNotContain(item, target.Items);
        Assert.Equal(vm.SelectedGroup.Id, h.State.LastGroupId);

        var groups = vm.Groups.ToArray();
        for (int sourceIndex = 0; sourceIndex < groups.Length; sourceIndex++)
        for (int slot = 0; slot <= groups.Length; slot++)
        {
            for (int i = 0; i < groups.Length; i++) vm.Groups.Move(vm.Groups.IndexOf(groups[i]), i);
            var expected = groups.ToList(); expected.Insert(slot, groups[sourceIndex]); expected.RemoveAt(sourceIndex < slot ? sourceIndex : sourceIndex + 1);
            drag = new(groups[sourceIndex], vm.Groups); vm.StartDrag(drag);
            drop = new(drag, vm.Groups, slot); vm.Drop(drop); vm.DragDropOperationFinished(drop.Effects, drag);
            Assert.Equal(expected, vm.Groups);
        }
        Assert.Empty(h.Actions.Launches);
        return Task.CompletedTask;
    });

    [Fact]
    public Task GongHoverWaitsCancelsOnLeaveAndRestoresUncommittedSelection() => OnModel(async h =>
    {
        var vm = h.Model; var original = vm.SelectedGroup; var target = vm.Groups[1];
        string saved = h.State.LastGroupId; long revision = h.State.Revision;
        var drag = new DragStub(original.Items[0], original.Items); vm.StartDrag(drag);
        var tab = new DropStub(drag, vm.Groups, 1) { TargetItem = target, VisualTarget = new Border() };
        vm.DragOver(tab); await Task.Delay(150); Assert.Same(original, vm.SelectedGroup);
        vm.DragLeave(tab); await Task.Delay(450); Assert.Same(original, vm.SelectedGroup);
        vm.DragOver(tab); await Task.Delay(500); Assert.Same(target, vm.SelectedGroup);
        Assert.Equal(saved, h.State.LastGroupId); Assert.Equal(revision, h.State.Revision);
        var idle = vm.WhenIdle;
        Assert.True(vm.HandleKey(Key.Escape)); Assert.True(vm.DragActive); Assert.False(idle.IsCompleted);
        vm.Drop(tab); Assert.Equal(DragDropEffects.None, tab.Effects);
        vm.DragCancelled(); Assert.True(idle.IsCompleted); Assert.Same(original, vm.SelectedGroup);
        Assert.Equal(revision, h.State.Revision); Assert.Equal(saved, h.State.LastGroupId);
        vm.DragOver(tab); await Task.Delay(450); Assert.Same(original, vm.SelectedGroup);
    });

    [Fact]
    public Task GongStaleLeaveCannotCancelNewHoverAndDropStopsTimer() => OnModel(async h =>
    {
        var vm = h.Model; var source = vm.SelectedGroup; var target = vm.Groups[1];
        var drag = new DragStub(source.Items[0], source.Items); vm.StartDrag(drag);
        var old = new DropStub(drag, source.Items, 0) { VisualTarget = new Border() };
        var tab = new DropStub(drag, vm.Groups, 1) { TargetItem = target, VisualTarget = new Border() };
        vm.DragOver(old); vm.DragOver(tab); vm.DragLeave(old);
        await Task.Delay(500); Assert.Same(target, vm.SelectedGroup);
        vm.DragCancelled();
        drag = new(source.Items[0], source.Items); vm.StartDrag(drag);
        tab = new(drag, vm.Groups, 1) { TargetItem = target };
        vm.DragOver(tab); vm.Drop(tab); vm.DragDropOperationFinished(tab.Effects, drag);
        await Task.Delay(450); Assert.Same(source, vm.SelectedGroup);
    });

    [Fact]
    public Task GongRejectsReadOnlyPendingForeignDuplicateAndInvalidDrops() => OnModel(h =>
    {
        var vm = h.Model; var source = vm.SelectedGroup; var target = vm.Groups[1]; var item = source.Items[0];
        var pending = item with { Id = Guid.NewGuid().ToString("N"), IsPending = true };
        source.Items.Add(pending);
        Assert.False(vm.CanStartDrag(new DragStub(pending, source.Items)));
        Assert.False(vm.CanStartDrag(new DragStub(item, new[] { item })));
        Assert.False(vm.CanStartDrag(new DragStub(item, source.Items) { MouseButton = MouseButton.Right }));
        var drag = new DragStub(item, source.Items); vm.StartDrag(drag);
        foreach (int slot in new[] { -1, source.Items.Count + 1 })
        {
            var invalid = new DropStub(drag, source.Items, slot); vm.Drop(invalid); Assert.Equal(DragDropEffects.None, invalid.Effects);
        }
        target.Items.Add(item with { Id = Guid.NewGuid().ToString("N") });
        var duplicate = new DropStub(drag, vm.Groups, 1) { TargetItem = target }; vm.Drop(duplicate);
        Assert.Equal(DragDropEffects.None, duplicate.Effects); Assert.Contains(item, source.Items);
        h.State.Suspend(true);
        var readOnly = new DropStub(drag, source.Items, source.Items.Count); vm.Drop(readOnly);
        Assert.Equal(DragDropEffects.None, readOnly.Effects);
        vm.NewGroupDropTarget.Drop(readOnly); Assert.Equal(DragDropEffects.None, readOnly.Effects);
        vm.DragCancelled(); Assert.False(vm.CanStartDrag(drag));
        vm.StartDrag(drag); Assert.False(vm.DragActive); Assert.Equal(DragDropEffects.None, drag.Effects);
        var external = new DropStub(null, source.Items, 0) { Data = new DataObject(DataFormats.UnicodeText, "https://example.com") };
        vm.Drop(external); Assert.Equal(DragDropEffects.None, external.Effects); Assert.False(vm.Importing);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public Task GongExternalDropsPreserveInsertionPendingDuplicatesAndStatus(bool file, bool tab) => OnModel(async h =>
    {
        var vm = h.Model; var target = vm.SelectedGroup; var existing = target.Items[0];
        var added = new LaunchItem("Imported", "https://example.com/imported", LaunchKind.Url);
        var resolve = new TaskCompletionSource<DropResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Actions.DropCompletion = resolve.Task;
        var data = new DataObject(); data.SetData(file ? DataFormats.FileDrop : "UniformResourceLocatorW", file ? new[] { @"C:\test.txt" } : "https://example.com/imported");
        int position = tab ? target.Items.Count : 1;
        var drop = new DropStub(null, tab ? vm.Groups : target.Items, 1) { Data = data, TargetItem = tab ? target : null! };
        int flashes = 0; vm.DuplicateFound += _ => flashes++;
        vm.DragOver(drop); Assert.Equal(DragDropEffects.Link, drop.Effects);
        vm.Drop(drop); Assert.True(vm.Importing); Assert.True(target.Items[position].IsPending);
        Assert.False(vm.CanStartDrag(new DragStub(target.Items[position], target.Items)));
        resolve.SetResult(new([added, existing], [])); await vm.PendingDrop;
        Assert.Same(added, target.Items[position]); Assert.DoesNotContain(target.Items, i => i.IsPending);
        Assert.Equal(1, flashes); Assert.True(vm.ToastVisible); Assert.Contains("重复", vm.StatusText); Assert.False(vm.Importing);
    });

    [Fact]
    public Task GongExternalCancelUnsupportedAndPlusNeverCreateStrayGroups() => OnModel(async h =>
    {
        var vm = h.Model; var target = vm.SelectedGroup; var original = target.Items.ToArray(); int groups = vm.Groups.Count;
        var drop = new DropStub(null, target.Items, 0) { Data = new DataObject(DataFormats.UnicodeText, "https://example.com/cancel") };
        vm.NewGroupDropTarget.Drop(drop); Assert.Equal(DragDropEffects.None, drop.Effects); Assert.Equal(groups, vm.Groups.Count);
        h.Actions.DropCompletion = new TaskCompletionSource<DropResolution>().Task;
        vm.Drop(drop); Assert.True(vm.Importing);
        vm.CancelImportCommand.Execute(null); await vm.PendingDrop;
        Assert.True(h.Actions.DropToken.IsCancellationRequested); Assert.Equal(original, target.Items); Assert.Contains("取消", vm.StatusText);
        drop.Data = new DataObject("unsupported", new byte[] { 1 }); vm.Drop(drop);
        Assert.Equal(DragDropEffects.None, drop.Effects); Assert.False(vm.Importing);
    });

    private sealed class DragStub(object item, IEnumerable source) : IDragInfo
    {
        public object SourceItem { get; } = item;
        public IEnumerable SourceCollection { get; } = source;
        public IEnumerable SourceItems => new[] { SourceItem };
        public DataFormat DataFormat { get; set; } = GongSolutions.Wpf.DragDrop.DragDrop.DataFormat;
        public object Data { get; set; } = null!;
        public DragDropEffects Effects { get; set; }
        public MouseButton MouseButton { get; set; } = MouseButton.Left;
        public Point DragStartPosition => default;
        public Point PositionInDraggedItem => default;
        public int SourceIndex => 0;
        public CollectionViewGroup SourceGroup => null!;
        public UIElement VisualSource => null!;
        public UIElement VisualSourceItem => null!;
        public FlowDirection VisualSourceFlowDirection => FlowDirection.LeftToRight;
        public object DataObject { get; set; } = null!;
        public Func<DependencyObject, object, DragDropEffects, DragDropEffects> DragDropHandler { get; set; } = null!;
        public DragDropKeyStates DragDropCopyKeyState => default;
        public void RefreshSourceItems(object sender) { }
    }

    private sealed class DropStub(IDragInfo? drag, IEnumerable? collection, int index) : IDropInfo
    {
        public object Data { get; set; } = drag?.Data!;
        public IDragInfo DragInfo { get; } = drag!;
        public int InsertIndex { get; } = index;
        public int UnfilteredInsertIndex => InsertIndex;
        public IEnumerable TargetCollection { get; } = collection!;
        public object TargetItem { get; set; } = null!;
        public UIElement VisualTarget { get; set; } = null!;
        public Type DropTargetAdorner { get; set; } = null!;
        public Type DropTargetHintAdorner { get; set; } = null!;
        public DropHintState DropTargetHintState { get; set; }
        public string DropHintText { get; set; } = "";
        public DragDropEffects Effects { get; set; }
        public Point DropPosition => default;
        public CollectionViewGroup TargetGroup => null!;
        public ScrollViewer TargetScrollViewer => null!;
        public ScrollingMode TargetScrollingMode => ScrollingMode.Both;
        public UIElement VisualTargetItem => null!;
        public Orientation VisualTargetOrientation => Orientation.Horizontal;
        public FlowDirection VisualTargetFlowDirection => FlowDirection.LeftToRight;
        public string DestinationText { get; set; } = "";
        public string EffectText { get; set; } = "";
        public RelativeInsertPosition InsertPosition => default;
        public DragDropKeyStates KeyStates => default;
        public bool NotHandled { get; set; }
        public bool IsSameDragDropContextAsSource => true;
        public EventType EventType => EventType.Bubbled;
        public bool AcceptChildItem { get; set; }
    }
}
