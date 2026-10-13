using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    public ObservableCollection<LaunchGroup> Groups { get; }
    public bool CanEdit => !_store.IsReadOnly && !_suspended;
    public bool IsReadOnly => _store.IsReadOnly;
    public HotkeyConfig Hotkey => _settings.Hotkey;
    public string HotkeyDisplay => Hotkey.Display;
    public bool HideOnDeactivate => _settings.HideOnDeactivate;
    public int Columns => _settings.Columns;
    public long Revision => _revision;
    public string LastGroupId => _lastGroupId;
    public string StorageStatus { get => _storageStatus; private set => SetProperty(ref _storageStatus, value); }

    public MainViewModel(ConfigService store)
    {
        _store = store;
        _settings = store.Initial;
        Groups = new(_settings.Groups.Select(g => new LaunchGroup(g.Name, g.Items, g.Id)));
        _lastGroupId = _settings.LastGroupId!;
        _storageStatus = IsReadOnly ? "只读配置" : store.SavedRevision < 0 ? "等待保存" : "已保存";
        foreach (var group in Groups) group.Items.CollectionChanged += ItemsChanged;
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SaveWithFeedbackAsync(); };
        if (!IsReadOnly && store.SavedRevision < 0) _debounce.Start();
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { if (!_volatileChange) Changed(); }
    public void Changed()
    {
        _owner.VerifyAccess();
        if (!CanEdit) return;
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
    public void ApplySettings(HotkeyConfig hotkey, bool hideOnDeactivate)
    {
        if (!CanEdit) return;
        _settings = _settings with { Hotkey = hotkey.Copy(), HideOnDeactivate = hideOnDeactivate };
        OnPropertyChanged(nameof(HotkeyDisplay));
        OnPropertyChanged(nameof(HideOnDeactivate));
        Changed();
    }
    public AppConfig Snapshot()
    {
        _owner.VerifyAccess();
        return _settings with
        {
            Hotkey = _settings.Hotkey.Copy(), LastGroupId = _lastGroupId,
            Groups = Groups.Select(g => new GroupConfig { Id = g.Id, Name = g.Name,
                Items = g.Items.Select(i => i with { ErrorMessage = null }).ToList() }).ToList()
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
}
