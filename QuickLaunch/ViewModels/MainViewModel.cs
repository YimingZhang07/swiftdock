using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickLaunch.Models;
using QuickLaunch.Services;

namespace QuickLaunch.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ConfigService _store;
    private readonly Dispatcher _owner = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AppConfig _settings;
    private string _lastGroupId;
    private long _revision;
    private bool _suspended, _volatileChange;
    private string _storageStatus;
    private readonly Func<DateTimeOffset> _now;
    private (LaunchItem Item, string GroupId, int Index, DateTimeOffset Until)? _deleted;
    public ObservableCollection<LaunchGroup> Groups { get; }
    public bool CanEdit => !_store.IsReadOnly && !_suspended;
    public bool IsReadOnly => _store.IsReadOnly;
    public HotkeyConfig Hotkey => _settings.Hotkey;
    public string HotkeyDisplay => Hotkey.Display;
    public int Columns => _settings.Columns;
    public int IconSize => _settings.IconSize;
    public double CellSize => Math.Max(84, IconSize + 52);
    public double TileSize => CellSize - 4;
    public Size? WindowSize => _settings.WindowWidth is { } width && _settings.WindowHeight is { } height ? new(width, height) : null;
    public long Revision => _revision;
    public string LastGroupId => _lastGroupId;
    public string StorageStatus
    {
        get => _storageStatus;
        private set { if (SetProperty(ref _storageStatus, value)) OnPropertyChanged(nameof(StorageWarning)); }
    }
    public string StorageWarning => StorageStatus.StartsWith("尚未保存", StringComparison.Ordinal) || StorageStatus == "只读配置" ? StorageStatus : "";

    public MainViewModel(ConfigService store, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _settings = store.Initial;
        Groups = new(_settings.Groups.Select(g => new LaunchGroup(g.Name, g.Items, g.Id)));
        _lastGroupId = _settings.LastGroupId!;
        _storageStatus = IsReadOnly ? "只读配置" : store.SavedRevision < 0 ? "等待保存" : "已保存";
        foreach (var group in Groups) Wire(group);
        Groups.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Move)
            {
                if (e.OldItems is not null) foreach (LaunchGroup group in e.OldItems) Unwire(group);
                if (e.NewItems is not null) foreach (LaunchGroup group in e.NewItems) Wire(group);
            }
            if (!_volatileChange) Changed();
        };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SaveWithFeedbackAsync(); };
        if (!IsReadOnly && store.SavedRevision < 0) _debounce.Start();
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { if (!_volatileChange) Changed(); }
    private void GroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (!_volatileChange) Changed(); }
    private void Wire(LaunchGroup group) { group.Items.CollectionChanged += ItemsChanged; group.PropertyChanged += GroupChanged; }
    private void Unwire(LaunchGroup group) { group.Items.CollectionChanged -= ItemsChanged; group.PropertyChanged -= GroupChanged; }
    public void Changed()
    {
        _owner.VerifyAccess();
        if (!CanEdit || _volatileChange) return;
        _revision++;
        StorageStatus = "等待保存";
        _debounce.Stop();
        _debounce.Start();
    }
    public void CommitGroup(string id)
    {
        if (_lastGroupId == id || !CanEdit) return;
        _lastGroupId = id;
        Changed();
    }
    public void ApplySettings(HotkeyConfig hotkey)
    {
        if (!CanEdit) return;
        _settings = _settings with { Hotkey = hotkey.Copy() };
        OnPropertyChanged(nameof(HotkeyDisplay));
        Changed();
    }
    public void SetIconSize(int size)
    {
        _owner.VerifyAccess();
        if (!CanEdit) return;
        if (size is < 16 or > 128) throw new ArgumentOutOfRangeException(nameof(size));
        if (IconSize == size) return;
        _settings = _settings with { IconSize = size };
        OnPropertyChanged(nameof(IconSize));
        OnPropertyChanged(nameof(CellSize));
        OnPropertyChanged(nameof(TileSize));
        Changed();
    }
    public void SetWindowSize(Size? size)
    {
        _owner.VerifyAccess();
        if (_suspended || WindowSize == size) return;
        // Read-only profiles keep the size for this session; Changed() will not save it.
        _settings = _settings with { WindowWidth = size?.Width, WindowHeight = size?.Height };
        Changed();
    }
    public AppConfig Snapshot()
    {
        _owner.VerifyAccess();
        return _settings with
        {
            Hotkey = _settings.Hotkey.Copy(), LastGroupId = _lastGroupId,
            Groups = Groups.Select(g => new GroupConfig { Id = g.Id, Name = g.Name,
                Items = g.Items.Where(i => !i.IsPending).Select(i => i with { ErrorMessage = null }).ToList() }).ToList()
        };
    }

    public void RecordLaunch(LaunchItem snapshot, LaunchResult result)
    {
        foreach (var group in Groups)
        {
            int index = group.Items.ToList().FindIndex(i => i.Id == snapshot.Id);
            if (index < 0) continue;
            var current = group.Items[index];
            if (current.Path != snapshot.Path || current.Arguments != snapshot.Arguments
                || current.WorkingDirectory != snapshot.WorkingDirectory || current.RunAsAdmin != snapshot.RunAsAdmin) return;
            _volatileChange = true;
            try
            {
                group.Items[index] = current with
                {
                    LaunchCount = result.Accepted && CanEdit && current.LaunchCount < long.MaxValue ? current.LaunchCount + 1 : current.LaunchCount,
                    ErrorMessage = result.Cancelled ? current.ErrorMessage : result.Accepted ? null : result.Message
                };
            }
            finally { _volatileChange = false; }
            if (result.Accepted && CanEdit) Changed();
            return;
        }
    }

    private async Task SaveWithFeedbackAsync()
    {
        try { await FlushAsync(); }
        catch (Exception ex) { StorageStatus = $"尚未保存：{ex.Message}"; }
    }
    public async Task FlushAsync()
    {
        _debounce.Stop();
        if (IsReadOnly) return;
        long revision = _revision;
        var snapshot = Snapshot();
        StorageStatus = "保存中…";
        try
        {
            await _store.SaveAsync(snapshot, revision);
            StorageStatus = _store.SavedRevision >= _revision ? "已保存" : "等待保存";
        }
        catch (Exception ex) { StorageStatus = $"尚未保存：{ex.Message}"; throw; }
    }
    public void Suspend(bool value)
    {
        _suspended = value;
        if (value) _debounce.Stop();
        OnPropertyChanged(nameof(CanEdit));
    }

    private void Mutate(Action change, bool save = true)
    {
        _owner.VerifyAccess();
        if (!CanEdit) throw new InvalidOperationException("当前不能修改配置。");
        bool previous = _volatileChange; _volatileChange = true;
        try { change(); }
        finally { _volatileChange = previous; }
        if (save && !previous) Changed();
    }
    public LaunchGroup AddGroup(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("分组名称不能为空。");
        var group = new LaunchGroup(name.Trim(), []);
        Mutate(() => Groups.Add(group)); return group;
    }
    public void RenameGroup(LaunchGroup group, string name)
    {
        if (!Groups.Contains(group) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("分组名称不能为空。");
        Mutate(() => group.Name = name.Trim());
    }
    public void RemoveGroup(LaunchGroup group)
    {
        if (!Groups.Contains(group)) return;
        Mutate(() =>
        {
            Groups.Remove(group);
            if (Groups.Count == 0) Groups.Add(new("常用", []));
            if (_lastGroupId == group.Id) _lastGroupId = Groups[0].Id;
            if (_deleted?.GroupId == group.Id) _deleted = null;
        });
    }
    public void MoveGroup(int oldIndex, int newIndex)
    { if (oldIndex != newIndex) Mutate(() => Groups.Move(oldIndex, newIndex)); }

    public bool IsDuplicate(LaunchGroup group, LaunchItem item, string? excludingId = null) => group.Items
        .Where(i => !i.IsPending && i.Id != excludingId).Any(i => ItemIdentity.Key(i) == ItemIdentity.Key(item));

    public void UpdateItem(LaunchGroup group, LaunchItem original, LaunchItem edited)
    {
        int index = group.Items.ToList().FindIndex(i => i.Id == original.Id);
        if (!Groups.Contains(group) || index < 0 || original.IsPending) throw new InvalidOperationException("条目已不存在。");
        if (IsDuplicate(group, edited, original.Id)) throw new InvalidOperationException("该组已有相同的启动条目。");
        bool iconChanged = original.Path != edited.Path || original.Kind != edited.Kind;
        Mutate(() => group.Items[index] = edited with { Id = original.Id, LaunchCount = original.LaunchCount,
            ErrorMessage = null, Visual = iconChanged ? new() : original.Visual });
    }
    public void DeleteItem(LaunchGroup group, LaunchItem item)
    {
        int index = group.Items.IndexOf(item);
        if (index < 0 || item.IsPending) return;
        Mutate(() => { group.Items.RemoveAt(index); _deleted = (item, group.Id, index, _now().AddSeconds(5)); });
    }
    public bool UndoDelete()
    {
        if (!CanEdit || _deleted is not { } deleted) return false;
        _deleted = null;
        var group = Groups.FirstOrDefault(g => g.Id == deleted.GroupId);
        if (deleted.Until < _now() || group is null || IsDuplicate(group, deleted.Item)) return false;
        Mutate(() => group.Items.Insert(Math.Min(deleted.Index, group.Items.Count), deleted.Item)); return true;
    }
    public bool MoveItem(LaunchGroup source, LaunchGroup target, LaunchItem item, int slot)
    {
        int old = source.Items.ToList().FindIndex(i => i.Id == item.Id);
        if (!Groups.Contains(source) || !Groups.Contains(target) || old < 0 || item.IsPending || slot < 0 || slot > target.Items.Count) return false;
        if (source != target && IsDuplicate(target, item)) return false;
        int index = source == target ? DragDrop.ReorderMath.FinalIndex(old, slot, source.Items.Count) : slot;
        if (source == target && index == old) return true;
        Mutate(() =>
        {
            if (source == target) source.Items.Move(old, index);
            else
            {
                var current = source.Items[old]; source.Items.RemoveAt(old);
                try { target.Items.Insert(index, current); }
                catch { source.Items.Insert(old, current); throw; }
            }
        });
        return true;
    }
    public LaunchGroup? MoveToNewGroup(LaunchGroup source, LaunchItem item, string name)
    {
        int index = source.Items.ToList().FindIndex(i => i.Id == item.Id);
        if (!Groups.Contains(source) || index < 0 || item.IsPending) return null;
        var group = new LaunchGroup(name, []);
        Mutate(() =>
        {
            Groups.Add(group); var current = source.Items[index]; source.Items.RemoveAt(index);
            try { group.Items.Add(current); }
            catch { source.Items.Insert(index, current); Groups.Remove(group); throw; }
        });
        return group;
    }

    public string[] BeginImport(LaunchGroup group, int slot, int count)
    {
        var pending = Enumerable.Range(0, count).Select(_ => new LaunchItem("正在读取…", "", LaunchKind.File) { IsPending = true }).ToArray();
        Mutate(() => { foreach (var item in pending) group.Items.Insert(slot++, item); }, save: false);
        return pending.Select(i => i.Id).ToArray();
    }
    public (int Added, LaunchItem? Duplicate) CompleteImport(LaunchGroup group, string[] pendingIds, IReadOnlyList<LaunchItem> items)
    {
        if (!CanEdit || !Groups.Contains(group)) return (0, null);
        int position = group.Items.ToList().FindIndex(i => pendingIds.Contains(i.Id));
        if (position < 0) return (0, null);
        int added = 0; LaunchItem? duplicate = null;
        Mutate(() =>
        {
            foreach (var pending in group.Items.Where(i => pendingIds.Contains(i.Id)).ToArray()) group.Items.Remove(pending);
            foreach (var item in items)
            {
                var existing = group.Items.FirstOrDefault(i => !i.IsPending && ItemIdentity.Key(i) == ItemIdentity.Key(item));
                if (existing is not null) { duplicate = existing; continue; }
                group.Items.Insert(Math.Min(position++, group.Items.Count), item); added++;
            }
        }, save: false);
        if (added > 0) Changed();
        return (added, duplicate);
    }
    public void CancelImport(LaunchGroup group, string[] pendingIds)
    {
        bool previous = _volatileChange; _volatileChange = true;
        try { foreach (var item in group.Items.Where(i => pendingIds.Contains(i.Id)).ToArray()) group.Items.Remove(item); }
        finally { _volatileChange = previous; }
    }
}
