using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Swiftdock.Diagnostics;
using Swiftdock.DragDrop;
using Swiftdock.Models;
using Swiftdock.Services;

namespace Swiftdock.ViewModels;

// Owns transient panel state and interaction policy; MainViewModel owns configuration mutations and persistence.
public sealed partial class LauncherViewModel : ObservableObject, IDisposable
{
    private readonly ILauncherDialogs _dialogs;
    private readonly ILauncherActions _actions;
    private readonly ILogger _log;
    private readonly Dictionary<LaunchGroup, TileSelection> _selections = [];
    private CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _importStop;
    private TaskCompletionSource? _dragDone;
    private bool _closing;
    private LaunchGroup _selectedGroup;
    private bool _dragActive, _importing;
    private string _statusText = "", _toastText = "";
    private bool _toastVisible;
    public MainViewModel State { get; }
    public ObservableCollection<LaunchGroup> Groups => State.Groups;
    public bool IsReadOnly => State.IsReadOnly;
    public bool CanEdit => State.CanEdit;
    public bool CanManage => CanEdit && CanInteract && !Importing;
    private bool CanInteract => !_closing && !DragActive;
    public bool MenuOpen { get; set; }
    public bool HasOpenEditor => _dialogs.HasOpenEditor;
    public LaunchGroup SelectedGroup => _selectedGroup;
    public LaunchItem? SelectedItem => SelectionFor(SelectedGroup).Item;
    public bool DragActive => _dragActive;
    public bool Importing => _importing;
    public string StatusText => _statusText;
    public string ToastText => _toastText.ReplaceLineEndings(" ");
    public bool ToastVisible => _toastVisible;
    public TimeSpan? ToastDuration => Importing || _closing ? null : TimeSpan.FromSeconds(
        new[] { "失败", "错误", "无法", "未能", "尚未保存", "不可", "不能" }.Any(ToastText.Contains) ? 6 : 4);
    public event Action<bool>? GroupSelected;
    public event Action? FocusSelectionRequested;
    public event Action? RevealSelectionRequested;
    public event Action? HideRequested;
    public event Action? CancelDragRequested;
    public event Action? ContentChanged;
    public event Action? IconsRequested;
    public event Action<LaunchItem>? DuplicateFound;
    public event Action? ToastChanged;

    public LauncherViewModel(MainViewModel state, ILauncherDialogs dialogs, ILauncherActions actions, ILogger log)
    {
        State = state; _dialogs = dialogs; _actions = actions; _log = log;
        InitializeDragDrop();
        _selectedGroup = Groups.FirstOrDefault(g => g.Id == state.LastGroupId) ?? Groups[0];
        foreach (var group in Groups) SelectionFor(group);
        Groups.CollectionChanged += GroupsChanged;
        State.PropertyChanged += StateChanged;
        if (IsReadOnly) SetStatus("当前为只读配置，可启动项目，不能添加或排序。");
    }

    public TileSelection SelectionFor(LaunchGroup group)
    {
        if (!_selections.TryGetValue(group, out var selection))
        {
            selection = new(group);
            selection.PropertyChanged += SelectionChanged;
            _selections.Add(group, selection);
        }
        return selection;
    }
    private void SelectionChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(SelectedItem));
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CanEdit)) return;
        OnPropertyChanged(nameof(CanEdit)); NotifyCommands();
    }
    private void GroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var group in _selections.Keys.Where(g => !Groups.Contains(g)).ToArray())
        {
            _selections[group].PropertyChanged -= SelectionChanged;
            _selections[group].Dispose(); _selections.Remove(group);
        }
        foreach (var group in Groups) SelectionFor(group);
        if (Groups.Count > 0) SetSelectedGroup(Groups.Contains(SelectedGroup) ? SelectedGroup : Groups[0], preview: DragActive);
        NotifyCommands();
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanManage));
        foreach (var command in new IRelayCommand[] { LaunchCommand, LaunchElevatedCommand, OpenLocationCommand, EditCommand,
            ChangeIconCommand, RenameCommand, DeleteCommand, RefreshIconCommand, UndoDeleteCommand, AddItemCommand,
            AddGroupCommand, RenameGroupCommand, DeleteGroupCommand, SelectGroupCommand, SelectGroupIndexCommand,
            NextGroupCommand, PreviousGroupCommand, HidePanelCommand, CancelImportCommand }) command.NotifyCanExecuteChanged();
    }
    public void SetDragActive(bool value)
    {
        if (!SetProperty(ref _dragActive, value, nameof(DragActive))) return;
        if (value) _dragDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        else { _dragDone?.TrySetResult(); _dragDone = null; }
        NotifyCommands();
    }
    internal Task WhenIdle => _dragDone?.Task ?? Task.CompletedTask;

    public void SetSelectedGroup(LaunchGroup group, bool preview = false)
    {
        if (!Groups.Contains(group)) return;
        SetProperty(ref _selectedGroup, group, nameof(SelectedGroup));
        ClearAllSelections();
        if (!preview) State.CommitGroup(group.Id);
        GroupSelected?.Invoke(preview);
    }
    // Tabs must stay enabled as Gong drag/drop surfaces. Guard the action without
    // disabling the button while OLE owns input.
    [RelayCommand]
    private void SelectGroup(LaunchGroup? group) { if (CanSelectGroup(group)) SetSelectedGroup(group!); }
    private bool CanSelectGroup(LaunchGroup? group) => CanInteract && group is not null && Groups.Contains(group);
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void SelectGroupIndex(int index)
    {
        if (!CanInteract || index < 0 || index >= Groups.Count) return;
        SetSelectedGroup(Groups[index]);
        SelectionFor(SelectedGroup).Item = SelectedGroup.Items.FirstOrDefault(i => !i.IsPending);
        RevealSelectionRequested?.Invoke();
    }
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void NextGroup() => SelectGroupIndex((Groups.IndexOf(SelectedGroup) + 1) % Groups.Count);
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void PreviousGroup() => SelectGroupIndex((Groups.IndexOf(SelectedGroup) + Groups.Count - 1) % Groups.Count);
    public void ClearAllSelections() { foreach (var selection in _selections.Values) selection.Item = null; }
    [RelayCommand]
    private void ClearSelection() { SelectionFor(SelectedGroup).Item = null; FocusSelectionRequested?.Invoke(); }
    private void SelectItem(string id)
    {
        SelectionFor(SelectedGroup).Item = SelectedGroup.Items.FirstOrDefault(i => i.Id == id);
        RevealSelectionRequested?.Invoke();
    }
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void HidePanel() { if (CanInteract) HideRequested?.Invoke(); }

    // Navigation distances depend on the tile viewport; all nonvisual key precedence lives here.
    public bool HandleKey(Key key, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false, bool textInput = false)
    {
        if (textInput || HasOpenEditor || MenuOpen || _closing) return false;
        if (key == Key.Escape)
        {
            if (DragActive) CancelDragRequested?.Invoke();
            else if (SelectedItem is not null) ClearSelectionCommand.Execute(null);
            else HidePanelCommand.Execute(null);
            return true;
        }
        if (DragActive) return false;
        if (key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control))
        {
            if (modifiers.HasFlag(ModifierKeys.Shift)) PreviousGroupCommand.Execute(null);
            else NextGroupCommand.Execute(null);
        }
        else if (modifiers == ModifierKeys.Control && key >= Key.D1 && key <= Key.D9) SelectGroupIndexCommand.Execute(key - Key.D1);
        else if (key == Key.Z && modifiers == ModifierKeys.Control) { if (UndoDeleteCommand.CanExecute(null)) UndoDeleteCommand.Execute(null); }
        else if (key == Key.F2) { if (RenameCommand.CanExecute(SelectedItem)) RenameCommand.Execute(SelectedItem); }
        else if (key == Key.Delete) { if (DeleteCommand.CanExecute(SelectedItem)) DeleteCommand.Execute(SelectedItem); }
        else if (key == Key.Enter) { if (!repeat && LaunchCommand.CanExecute(SelectedItem)) LaunchCommand.Execute(SelectedItem); }
        else return false;
        return true;
    }

    private bool CanUseItem(LaunchItem? item) => CanInteract && item is { IsPending: false };
    private bool CanElevate(LaunchItem? item) => CanUseItem(item) && item!.Kind == LaunchKind.App;
    private bool CanOpenLocation(LaunchItem? item) => CanUseItem(item) && item!.Kind != LaunchKind.Url;
    private bool CanManageItem(LaunchItem? item) => CanManage && item is { IsPending: false } && SelectedGroup.Items.Contains(item);
    private bool CanManageGroup(LaunchGroup? group) => CanManage && group is not null && Groups.Contains(group);
    [RelayCommand(CanExecute = nameof(CanUseItem), AllowConcurrentExecutions = true)]
    private Task LaunchAsync(LaunchItem? item) => LaunchItemAsync(item, false);
    [RelayCommand(CanExecute = nameof(CanElevate), AllowConcurrentExecutions = true)]
    private Task LaunchElevatedAsync(LaunchItem? item) => CanElevate(item) ? LaunchItemAsync(item, true) : Task.CompletedTask;
    private async Task LaunchItemAsync(LaunchItem? item, bool elevate)
    {
        if (!CanUseItem(item)) return;
        var cancellation = _shutdown.Token;
        var result = await _actions.LaunchAsync(item!, elevate);
        try { await WhenIdle.WaitAsync(cancellation); }
        catch (OperationCanceledException) { return; }
        if (_closing || cancellation.IsCancellationRequested) return;
        State.RecordLaunch(item!, result);
        SetStatus($"{item!.Name}：{result.Message}", toast: !result.Accepted || result.Cancelled);
        _log.LaunchResult((int)item.Kind, result.Accepted, result.Cancelled, LogPrivacy.Message(result.Message));
    }
    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private async Task OpenLocationAsync(LaunchItem? item)
    {
        if (!CanOpenLocation(item)) return;
        try { await _actions.OpenLocationAsync(item!); }
        catch (Exception ex) { SetStatus($"无法打开所在位置：{ex.Message}"); }
    }
    [RelayCommand(CanExecute = nameof(CanManageItem))]
    private void Edit(LaunchItem? item) { if (CanManageItem(item)) EditItem(item); }
    [RelayCommand(CanExecute = nameof(CanManageItem))]
    private void ChangeIcon(LaunchItem? item) { if (CanManageItem(item)) EditItem(item, true); }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private void AddItem() { if (CanManage) EditItem(null); }
    private void EditItem(LaunchItem? original, bool openIconPicker = false)
    {
        var group = SelectedGroup;
        if (_dialogs.EditItem(original, openIconPicker) is not { } result || !CanManage || !Groups.Contains(group)) return;
        try
        {
            if (original is not null) State.UpdateItem(group, original, result);
            else if (State.IsDuplicate(group, result)) throw new InvalidOperationException("该组已有相同的启动条目。");
            else group.Items.Add(result);
            SetStatus("条目已更新，将自动保存。", toast: false);
            ContentChanged?.Invoke(); IconsRequested?.Invoke(); SelectItem(original?.Id ?? result.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { SetStatus(ex.Message); }
    }
    [RelayCommand(CanExecute = nameof(CanManageItem))]
    private void Rename(LaunchItem? item)
    {
        if (!CanManageItem(item)) return;
        var group = SelectedGroup;
        if (_dialogs.Name("重命名条目", item!.Name) is { } value && CanManage && Groups.Contains(group))
        { State.UpdateItem(group, item, item with { Name = value }); SelectItem(item.Id); }
    }
    [RelayCommand(CanExecute = nameof(CanManageItem))]
    private void Delete(LaunchItem? item)
    {
        if (!CanManageItem(item)) return;
        State.DeleteItem(SelectedGroup, item!); SetStatus("已删除，5 秒内按 Ctrl+Z 撤销。");
        ContentChanged?.Invoke(); ClearSelectionCommand.Execute(null);
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private void UndoDelete()
    {
        if (CanManage) SetStatus(State.UndoDelete() ? "已恢复删除的条目。" : "没有可恢复的删除记录，或已超过 5 秒。");
    }
    [RelayCommand(CanExecute = nameof(CanUseItem))]
    private void RefreshIcon(LaunchItem? item)
    {
        if (!CanUseItem(item)) return;
        _actions.RefreshIcon(item!, Groups.SelectMany(g => g.Items)); IconsRequested?.Invoke();
    }
    private string NewGroupName()
    {
        int n = 1; string name = "新分组";
        while (Groups.Any(g => g.Name == name)) name = $"新分组 {++n}";
        return name;
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private void AddGroup()
    {
        if (CanManage && _dialogs.Name("新建分组", NewGroupName()) is { } value && CanManage) SetSelectedGroup(State.AddGroup(value));
    }
    [RelayCommand(CanExecute = nameof(CanManageGroup))]
    private void RenameGroup(LaunchGroup? group)
    {
        if (CanManageGroup(group) && _dialogs.Name("重命名分组", group!.Name) is { } value && CanManageGroup(group)) State.RenameGroup(group!, value);
    }
    [RelayCommand(CanExecute = nameof(CanManageGroup))]
    private async Task DeleteGroupAsync(LaunchGroup? group)
    {
        if (!CanManageGroup(group) || HasOpenEditor) return;
        if (group!.Items.Any(i => !i.IsPending) && !await _dialogs.ConfirmDeleteGroupAsync(group.Name)) return;
        if (CanManageGroup(group)) State.RemoveGroup(group);
    }
    public LaunchGroup? MoveToNewGroup(LaunchGroup source, LaunchItem item)
    {
        var group = State.MoveToNewGroup(source, item, NewGroupName());
        if (group is not null) SetSelectedGroup(group);
        return group;
    }

    public void SetStatus(string text, bool toast = true)
    {
        SetProperty(ref _statusText, text, nameof(StatusText));
        if (!toast) return;
        SetProperty(ref _toastText, text, nameof(ToastText));
        SetProperty(ref _toastVisible, true, nameof(ToastVisible));
        OnPropertyChanged(nameof(ToastDuration)); ToastChanged?.Invoke();
    }
    public void ExpireToast()
    {
        if (!Importing) { SetProperty(ref _toastVisible, false, nameof(ToastVisible)); ToastChanged?.Invoke(); }
    }
    internal void SetImporting(bool value)
    {
        if (!SetProperty(ref _importing, value, nameof(Importing))) return;
        OnPropertyChanged(nameof(ToastDuration)); NotifyCommands();
    }
    [RelayCommand(CanExecute = nameof(Importing))]
    private void CancelImport() { if (Importing) { _importStop?.Cancel(); SetStatus("已取消导入。"); } }
    public async Task ImportAsync(LaunchGroup target, int slot, DropSnapshot snapshot)
    {
        if (!CanManage || !Groups.Contains(target)) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _importStop = stop;
        string[] pending = [];
        try
        {
            SetImporting(true);
            pending = State.BeginImport(target, slot, Math.Max(1, snapshot.Paths.Length));
            SetStatus("正在解析拖入项目…");
            var resolution = await _actions.ResolveDropAsync(snapshot, stop.Token).WaitAsync(stop.Token);
            await WhenIdle.WaitAsync(stop.Token);
            if (_closing || stop.IsCancellationRequested) return;
            var committed = State.CompleteImport(target, pending, resolution.Items);
            if (target == SelectedGroup) ContentChanged?.Invoke();
            IconsRequested?.Invoke();
            if (committed.Duplicate is not null && target == SelectedGroup) DuplicateFound?.Invoke(committed.Duplicate);
            SetStatus($"已添加 {committed.Added} 项。{(committed.Duplicate is null ? "" : "重复项目已跳过。")} {string.Join("；", resolution.Errors)}");
            _log.DropResolved(committed.Added, resolution.Errors.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { ReportImportFailure(ex); }
        finally
        {
            State.CancelImport(target, pending);
            if (ReferenceEquals(_importStop, stop)) _importStop = null;
            SetImporting(false);
        }
    }
    public void ReportImportFailure(Exception ex) { SetStatus($"拖入失败：{ex.Message}"); _log.DropFailed(LogPrivacy.Message(ex.Message)); }
    public void PrepareExit() { _closing = true; RequestDragCancel(); _shutdown.Cancel(); NotifyCommands(); }
    public void ResumeAfterExitFailure()
    {
        _closing = false; _shutdown.Dispose(); _shutdown = new(); NotifyCommands();
    }
    public void Dispose()
    {
        PrepareExit(); _shutdown.Dispose();
        Groups.CollectionChanged -= GroupsChanged; State.PropertyChanged -= StateChanged;
        foreach (var selection in _selections.Values) { selection.PropertyChanged -= SelectionChanged; selection.Dispose(); }
    }
}

// Each group has model-owned selection, including immutable-record replacement and removal.
public sealed class TileSelection : ObservableObject, IDisposable
{
    private readonly LaunchGroup _group;
    private LaunchItem? _item;
    public LaunchItem? Item { get => _item; set => SetProperty(ref _item, value); }
    internal TileSelection(LaunchGroup group) { _group = group; group.Items.CollectionChanged += ItemsChanged; }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Item is { } item) Item = _group.Items.FirstOrDefault(i => i.Id == item.Id);
    }
    public void Dispose() => _group.Items.CollectionChanged -= ItemsChanged;
}
