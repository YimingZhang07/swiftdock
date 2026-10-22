using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftdock.DragDrop;
using Swiftdock.Models;
using Swiftdock.Services;
using Swiftdock.ViewModels;
using Xunit;

namespace Swiftdock.Tests;

public sealed partial class LauncherViewModelTests
{
    [Fact]
    public Task EscapeDefersToMenuThenCancelsDragThenClearsSelectionThenHides() => OnModel(async h =>
    {
        var vm = h.Model; var item = vm.SelectedGroup.Items[0];
        int hides = 0, cancels = 0, focuses = 0;
        vm.HideRequested += () => hides++;
        vm.CancelDragRequested += () => { cancels++; vm.SetDragActive(false); };
        vm.FocusSelectionRequested += () => focuses++;
        vm.SelectionFor(vm.SelectedGroup).Item = item;
        vm.SetDragActive(true); vm.MenuOpen = true;
        Assert.False(vm.HandleKey(Key.Escape));
        Assert.Equal(0, cancels); Assert.Same(item, vm.SelectedItem);
        vm.MenuOpen = false;
        Assert.True(vm.HandleKey(Key.Escape));
        Assert.Equal(1, cancels); Assert.Same(item, vm.SelectedItem); Assert.Equal(0, hides);
        Assert.True(vm.HandleKey(Key.Escape));
        Assert.Null(vm.SelectedItem); Assert.Equal(1, focuses); Assert.Equal(0, hides);
        Assert.True(vm.HandleKey(Key.Escape)); Assert.Equal(1, hides);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(Key.Enter)] [InlineData(Key.Delete)] [InlineData(Key.F2)] [InlineData(Key.Escape)]
    public Task EditorTextInputAndMenuOwnTheirKeys(Key key) => OnModel(h =>
    {
        var vm = h.Model; var item = vm.SelectedGroup.Items[0];
        vm.SelectionFor(vm.SelectedGroup).Item = item;
        h.Dialogs.HasOpenEditor = true; Assert.False(vm.HandleKey(key));
        h.Dialogs.HasOpenEditor = false; Assert.False(vm.HandleKey(key, textInput: true));
        vm.MenuOpen = true; Assert.False(vm.HandleKey(key));
        Assert.Same(item, vm.SelectedItem); Assert.Contains(item, vm.SelectedGroup.Items);
        Assert.Empty(h.Actions.Launches); Assert.Equal(0, h.Dialogs.Names);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EnterUsesSelectionSkipsRepeatAndPendingAndDefersDuringDrag() => OnModel(async h =>
    {
        var vm = h.Model;
        Assert.True(vm.HandleKey(Key.Enter)); Assert.Empty(h.Actions.Launches);
        var item = vm.SelectedGroup.Items[0]; vm.SelectionFor(vm.SelectedGroup).Item = item;
        Assert.True(vm.HandleKey(Key.Enter, repeat: true)); Assert.Empty(h.Actions.Launches);
        vm.SetDragActive(true); Assert.False(vm.HandleKey(Key.Enter)); Assert.Empty(h.Actions.Launches);
        vm.SetDragActive(false); Assert.True(vm.HandleKey(Key.Enter));
        await vm.LaunchCommand.ExecutionTask!;
        Assert.Single(h.Actions.Launches); Assert.Equal((item, false), h.Actions.Launches[0]);
        Assert.Equal(item.LaunchCount + 1, vm.SelectedItem!.LaunchCount);
        Assert.False(vm.ToastVisible);
        var pending = new LaunchItem("pending", "", LaunchKind.File) { IsPending = true };
        vm.SelectedGroup.Items.Add(pending); vm.SelectionFor(vm.SelectedGroup).Item = pending;
        Assert.True(vm.HandleKey(Key.Enter)); Assert.Single(h.Actions.Launches);
        Assert.False(vm.LaunchCommand.CanExecute(pending));
    });

    [Fact]
    public Task DeleteAndUndoRespectSelectionDragReadOnlyAndExpiry() => OnModel(h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup; var item = group.Items[0];
        Assert.True(vm.HandleKey(Key.Delete)); Assert.Contains(item, group.Items);
        vm.SelectionFor(group).Item = item;
        vm.SetDragActive(true); Assert.False(vm.HandleKey(Key.Delete)); Assert.Contains(item, group.Items);
        vm.SetDragActive(false); h.State.Suspend(true);
        Assert.True(vm.HandleKey(Key.Delete)); Assert.Contains(item, group.Items);
        h.State.Suspend(false); Assert.True(vm.HandleKey(Key.Delete));
        Assert.DoesNotContain(item, group.Items); Assert.Null(vm.SelectedItem);
        Assert.True(vm.HandleKey(Key.Z, ModifierKeys.Control)); Assert.Contains(item, group.Items);
        vm.DeleteCommand.Execute(item); h.Now = h.Now.AddSeconds(6);
        vm.UndoDeleteCommand.Execute(null); Assert.DoesNotContain(item, group.Items);
        Assert.Contains("5 秒", vm.StatusText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task CommandsNotifyAndGuardDirectExecutionWhenSuspendedDraggingOrImporting() => OnModel(h =>
    {
        var vm = h.Model; var item = vm.SelectedGroup.Items[0];
        int changes = 0; vm.DeleteCommand.CanExecuteChanged += (_, _) => changes++;
        foreach (var block in new Action<bool>[] { h.State.Suspend, vm.SetDragActive, vm.SetImporting })
        {
            block(true);
            Assert.False(vm.DeleteCommand.CanExecute(item)); Assert.False(vm.AddItemCommand.CanExecute(null));
            Assert.False(vm.AddGroupCommand.CanExecute(null)); Assert.False(vm.UndoDeleteCommand.CanExecute(null));
            vm.DeleteCommand.Execute(item); vm.AddItemCommand.Execute(null); vm.AddGroupCommand.Execute(null);
            Assert.Contains(item, vm.SelectedGroup.Items); Assert.Equal(0, h.Dialogs.Edits); Assert.Equal(0, h.Dialogs.Names);
            block(false); Assert.True(vm.DeleteCommand.CanExecute(item));
        }
        Assert.Equal(6, changes);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReadOnlyStillLaunchesButCannotManage() => OnModel(async h =>
    {
        var vm = h.Model; var item = vm.SelectedGroup.Items[0];
        Assert.True(vm.IsReadOnly); Assert.False(vm.CanEdit);
        Assert.False(vm.DeleteCommand.CanExecute(item)); Assert.False(vm.EditCommand.CanExecute(item));
        Assert.True(vm.LaunchCommand.CanExecute(item));
        await vm.LaunchCommand.ExecuteAsync(item);
        Assert.Single(h.Actions.Launches); Assert.Equal(item.LaunchCount, vm.SelectedGroup.Items[0].LaunchCount);
    }, readOnly: true);

    [Fact]
    public Task ItemCommandsUseTheirParameterAndInjectedServices() => OnModel(async h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup;
        var app = new LaunchItem("app", "example.exe", LaunchKind.App);
        var url = new LaunchItem("url", "https://example.com", LaunchKind.Url);
        group.Items.Add(app); group.Items.Add(url); vm.SelectionFor(group).Item = url;
        await vm.LaunchElevatedCommand.ExecuteAsync(app);
        Assert.Equal((app, true), Assert.Single(h.Actions.Launches));
        Assert.False(vm.LaunchElevatedCommand.CanExecute(url)); Assert.False(vm.OpenLocationCommand.CanExecute(url));
        app = group.Items.Single(i => i.Id == app.Id);
        await vm.OpenLocationCommand.ExecuteAsync(app); Assert.Same(app, h.Actions.Opened);
        vm.RefreshIconCommand.Execute(app); Assert.Same(app, h.Actions.Refreshed);
        h.Actions.LocationError = new IOException("location failure");
        await vm.OpenLocationCommand.ExecuteAsync(app); Assert.Contains("location failure", vm.ToastText);
        h.Dialogs.Edited = app with { Arguments = "--edited" };
        vm.EditCommand.Execute(app); Assert.False(h.Dialogs.IconPicker);
        app = group.Items.Single(i => i.Id == app.Id); Assert.Equal("--edited", app.Arguments);
        Assert.Same(app, vm.SelectedItem); Assert.Equal(app, h.Dialogs.Edited);
        h.Dialogs.Edited = app with { Icon = "glyph:E943:#2F6FDB" };
        vm.ChangeIconCommand.Execute(app); Assert.True(h.Dialogs.IconPicker);
        app = group.Items.Single(i => i.Id == app.Id); Assert.Equal(h.Dialogs.Edited.Icon, app.Icon);
        h.Dialogs.NameResult = "renamed"; vm.RenameCommand.Execute(app);
        Assert.Equal("renamed", vm.SelectedItem!.Name);
        Assert.Contains(url, group.Items);
    });

    [Fact]
    public Task AddItemSkipsCancelledEditorRejectsDuplicatesAndSelectsAcceptedItem() => OnModel(h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup; int count = group.Items.Count;
        vm.AddItemCommand.Execute(null); Assert.Equal(count, group.Items.Count);
        h.Dialogs.Edited = group.Items[0] with { Id = Guid.NewGuid().ToString() };
        vm.AddItemCommand.Execute(null); Assert.Equal(count, group.Items.Count); Assert.Contains("相同", vm.StatusText);
        h.Dialogs.Edited = new("added", "https://example.com/new", LaunchKind.Url);
        vm.AddItemCommand.Execute(null);
        Assert.Equal(count + 1, group.Items.Count); Assert.Same(h.Dialogs.Edited, vm.SelectedItem);
        Assert.Contains("相同", vm.ToastText); Assert.Contains("已更新", vm.StatusText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task GroupCommandsPreserveMouseKeyboardPreviewAndRemovalSemantics() => OnModel(async h =>
    {
        var vm = h.Model; var first = vm.SelectedGroup;
        h.Dialogs.NameResult = "created"; vm.AddGroupCommand.Execute(null);
        var created = vm.SelectedGroup; Assert.Equal("created", created.Name); Assert.Null(vm.SelectedItem);
        h.Dialogs.NameResult = "renamed"; vm.RenameGroupCommand.Execute(created); Assert.Equal("renamed", created.Name);
        created.Items.Add(new("pending", "", LaunchKind.File) { IsPending = true });
        var item = new LaunchItem("new", "https://example.com/new", LaunchKind.Url); created.Items.Add(item);
        vm.SelectGroupIndexCommand.Execute(vm.Groups.IndexOf(created)); Assert.Same(item, vm.SelectedItem);
        vm.SelectGroupCommand.Execute(first); Assert.Null(vm.SelectedItem); Assert.Null(vm.SelectionFor(created).Item);
        Assert.True(vm.HandleKey(Key.D1, ModifierKeys.Control)); Assert.Same(vm.Groups[0], vm.SelectedGroup);
        Assert.Same(vm.SelectedGroup.Items.First(i => !i.IsPending), vm.SelectedItem);
        Assert.True(vm.HandleKey(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift)); Assert.Same(created, vm.SelectedGroup);
        Assert.True(vm.HandleKey(Key.Tab, ModifierKeys.Control)); Assert.Same(vm.Groups[0], vm.SelectedGroup);
        var selected = vm.SelectedItem; vm.HandleKey(Key.D9, ModifierKeys.Control); Assert.Same(selected, vm.SelectedItem);
        string committed = h.State.LastGroupId;
        vm.SetSelectedGroup(created, preview: true); Assert.Equal(committed, h.State.LastGroupId); Assert.Null(vm.SelectedItem);
        h.Dialogs.Confirm = false; await vm.DeleteGroupCommand.ExecuteAsync(created); Assert.Contains(created, vm.Groups);
        h.Dialogs.Confirm = true; await vm.DeleteGroupCommand.ExecuteAsync(created); Assert.DoesNotContain(created, vm.Groups);
        Assert.Same(vm.Groups[0], vm.SelectedGroup); Assert.Equal(2, h.Dialogs.Confirmations);
        foreach (var group in vm.Groups.ToArray()) { group.Items.Clear(); await vm.DeleteGroupCommand.ExecuteAsync(group); }
        Assert.Single(vm.Groups); Assert.Empty(vm.SelectedGroup.Items); Assert.Equal(2, h.Dialogs.Confirmations);
    });

    [Fact]
    public Task GroupDeletionRechecksStateAfterConfirmation() => OnModel(async h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup;
        var confirmation = new TaskCompletionSource<bool>(); h.Dialogs.Confirmation = confirmation.Task;
        var deleting = vm.DeleteGroupCommand.ExecuteAsync(group);
        h.State.Suspend(true); confirmation.SetResult(true); await deleting;
        Assert.Contains(group, vm.Groups);
    });

    [Fact]
    public Task SelectionTracksReplacementsAndRemovalsWithoutControls() => OnModel(h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup; var other = vm.Groups.First(g => g != group);
        var item = group.Items[0]; vm.SelectionFor(group).Item = item;
        var second = new LaunchItem("second", "second.exe", LaunchKind.App); other.Items.Add(second);
        vm.SelectionFor(other).Item = second;
        h.State.UpdateItem(group, item, item with { Name = "replacement" });
        Assert.Same(group.Items[0], vm.SelectedItem); Assert.Equal("replacement", vm.SelectedItem!.Name);
        Assert.Same(second, vm.SelectionFor(other).Item);
        group.Items.RemoveAt(0); Assert.Null(vm.SelectedItem);
        vm.ClearAllSelections(); Assert.Null(vm.SelectionFor(other).Item);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ToastPolicyKeepsSilentSuccessSeparateAndPinsImports() => OnModel(async h =>
    {
        var vm = h.Model; int changes = 0; vm.ToastChanged += () => changes++;
        vm.SetStatus("普通提示"); Assert.Equal(TimeSpan.FromSeconds(4), vm.ToastDuration);
        vm.SetStatus("普通提示"); Assert.Equal(2, changes);
        await vm.LaunchCommand.ExecuteAsync(vm.SelectedGroup.Items[0]);
        Assert.Equal("普通提示", vm.ToastText); Assert.Contains("success", vm.StatusText); Assert.Equal(2, changes);
        vm.ExpireToast(); Assert.False(vm.ToastVisible); Assert.Equal("普通提示", vm.ToastText);
        h.Actions.Result = new(false, false, "模拟失败");
        await vm.LaunchCommand.ExecuteAsync(vm.SelectedGroup.Items[0]);
        Assert.True(vm.ToastVisible); Assert.Equal(TimeSpan.FromSeconds(6), vm.ToastDuration);
        vm.SetImporting(true); Assert.Null(vm.ToastDuration); vm.ExpireToast(); Assert.True(vm.ToastVisible);
        vm.SetImporting(false); Assert.Equal(TimeSpan.FromSeconds(6), vm.ToastDuration);
        h.Actions.Result = new(false, true, "cancelled");
        await vm.LaunchCommand.ExecuteAsync(vm.SelectedGroup.Items[0]); Assert.Contains("cancelled", vm.ToastText);
        vm.SetStatus("第一条\r\n第二条\n第三条");
        Assert.Equal("第一条 第二条 第三条", vm.ToastText);
    });

    [Fact]
    public Task LaunchCompletionWaitsForDragAndIgnoresExitGeneration() => OnModel(async h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup; var item = group.Items[0];
        var completion = new TaskCompletionSource<LaunchResult>(); h.Actions.LaunchCompletion = completion.Task;
        var launch = vm.LaunchCommand.ExecuteAsync(item); vm.SetDragActive(true);
        completion.SetResult(h.Actions.Result); await Task.Yield();
        Assert.False(launch.IsCompleted); Assert.Equal(item.LaunchCount, group.Items[0].LaunchCount);
        vm.SetDragActive(false); await launch; Assert.Equal(item.LaunchCount + 1, group.Items[0].LaunchCount);
        item = group.Items[0]; completion = new(); h.Actions.LaunchCompletion = completion.Task;
        launch = vm.LaunchCommand.ExecuteAsync(item);
        vm.PrepareExit(); vm.ResumeAfterExitFailure(); completion.SetResult(h.Actions.Result); await launch;
        Assert.Equal(item.LaunchCount, group.Items[0].LaunchCount);
    });

    [Fact]
    public Task ImportResolvesDeduplicatesWaitsForDragAndNeverPersistsPlaceholders() => OnModel(async h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup; int count = group.Items.Count;
        var duplicate = group.Items[0]; LaunchItem? flashed = null; vm.DuplicateFound += item => flashed = item;
        var added = new LaunchItem("added", "https://example.com/import", LaunchKind.Url);
        var completion = new TaskCompletionSource<DropResolution>(); h.Actions.DropCompletion = completion.Task;
        var import = vm.ImportAsync(group, 0, new([], ["one", "two"], null, null));
        Assert.True(vm.Importing); Assert.True(vm.CancelImportCommand.CanExecute(null)); Assert.Null(vm.ToastDuration);
        Assert.Equal(2, group.Items.Count(i => i.IsPending));
        Assert.DoesNotContain(h.State.Snapshot().Groups.SelectMany(g => g.Items), i => i.IsPending);
        vm.SetDragActive(true); completion.SetResult(new([duplicate, added], ["partial error"])); await Task.Yield();
        Assert.False(import.IsCompleted); Assert.DoesNotContain(added, group.Items);
        vm.SetDragActive(false); await import;
        Assert.False(vm.Importing); Assert.False(vm.CancelImportCommand.CanExecute(null));
        Assert.Equal(count + 1, group.Items.Count); Assert.DoesNotContain(group.Items, i => i.IsPending);
        Assert.Same(duplicate, flashed); Assert.Contains("partial error", vm.StatusText);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task CancelOrExitDuringImportRemovesPendingAndIgnoresLateResults(bool exit) => OnModel(async h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup; var before = group.Items.ToArray();
        var completion = new TaskCompletionSource<DropResolution>(); h.Actions.DropCompletion = completion.Task;
        var import = vm.ImportAsync(group, 0, new([], ["one"], null, null));
        if (exit) vm.PrepareExit(); else vm.CancelImportCommand.Execute(null);
        await import;
        Assert.True(h.Actions.DropToken.IsCancellationRequested); Assert.False(vm.Importing);
        Assert.Equal(before, group.Items);
        completion.SetResult(new([new("late", "late.exe", LaunchKind.App)], [])); await Task.Yield();
        Assert.Equal(before, group.Items);
        if (!exit) Assert.Equal("已取消导入。", vm.StatusText);
    });

    [Fact]
    public Task FailedImportCleansUpAndReportsError() => OnModel(async h =>
    {
        var vm = h.Model; var group = vm.SelectedGroup; var before = group.Items.ToArray();
        h.Actions.DropCompletion = Task.FromException<DropResolution>(new IOException("test failure"));
        await vm.ImportAsync(group, 0, new([], ["one"], null, null));
        Assert.Equal(before, group.Items); Assert.False(vm.Importing);
        Assert.Contains("test failure", vm.ToastText); Assert.Equal(TimeSpan.FromSeconds(6), vm.ToastDuration);
    });

    // A dispatcher supplies the state store's owning thread and async continuations, without an Application or Window.
    private static Task OnModel(Func<Harness, Task> test, bool readOnly = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-panel-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using var store = ConfigService.Open(directory, readOnly, new MemoryWriter());
                    using var harness = new Harness(store);
                    await test(harness); done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private sealed class MemoryWriter : IConfigWriter { public void Write(string path, byte[] bytes) { } }
    private sealed class Harness : IDisposable
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public MainViewModel State { get; }
        public LauncherViewModel Model { get; }
        public Dialogs Dialogs { get; } = new();
        public Actions Actions { get; } = new();
        public Harness(ConfigService store)
        {
            State = new(store, () => Now);
            Model = new(State, Dialogs, Actions, NullLogger<LauncherViewModel>.Instance);
        }
        public void Dispose() { Model.Dispose(); State.Dispose(); }
    }
    private sealed class Dialogs : ILauncherDialogs
    {
        public bool HasOpenEditor { get; set; }
        public LaunchItem? Edited { get; set; }
        public bool IconPicker { get; private set; }
        public string? NameResult { get; set; }
        public bool Confirm { get; set; }
        public Task<bool>? Confirmation { get; set; }
        public int Names { get; private set; }
        public int Edits { get; private set; }
        public int Confirmations { get; private set; }
        public LaunchItem? EditItem(LaunchItem? item, bool openIconPicker) { Edits++; IconPicker = openIconPicker; return Edited; }
        public string? Name(string title, string value) { Names++; return NameResult; }
        public Task<bool> ConfirmDeleteGroupAsync(string name) { Confirmations++; return Confirmation ?? Task.FromResult(Confirm); }
    }
    private sealed class Actions : ILauncherActions
    {
        public List<(LaunchItem Item, bool Elevate)> Launches { get; } = [];
        public LaunchResult Result { get; set; } = new(true, false, "success");
        public Task<LaunchResult>? LaunchCompletion { get; set; }
        public LaunchItem? Opened { get; private set; }
        public LaunchItem? Refreshed { get; private set; }
        public Exception? LocationError { get; set; }
        public Task<DropResolution> DropCompletion { get; set; } = Task.FromResult(new DropResolution([], []));
        public CancellationToken DropToken { get; private set; }
        public Task<LaunchResult> LaunchAsync(LaunchItem item, bool elevate) { Launches.Add((item, elevate)); return LaunchCompletion ?? Task.FromResult(Result); }
        public Task OpenLocationAsync(LaunchItem item) { Opened = item; return LocationError is null ? Task.CompletedTask : Task.FromException(LocationError); }
        public void RefreshIcon(LaunchItem item, IEnumerable<LaunchItem> items) => Refreshed = item;
        public Task<DropResolution> ResolveDropAsync(DropSnapshot snapshot, CancellationToken cancellation) { DropToken = cancellation; return DropCompletion; }
    }
}
