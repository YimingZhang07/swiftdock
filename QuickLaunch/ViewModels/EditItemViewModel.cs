using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickLaunch.Models;
using QuickLaunch.Services;

namespace QuickLaunch.ViewModels;

public sealed partial class EditItemViewModel : ObservableObject, IDisposable
{
    private readonly IFilePicker _picker;
    private readonly IItemEditorEnvironment _environment;
    private CancellationTokenSource? _refresh;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initializing, _inferring, _settingName, _autoName, _closed;

    [ObservableProperty] private string _title = "添加项目", _saveLabel = "添加";
    [ObservableProperty] private string _name = "", _path = "", _arguments = "", _workingDirectory = "", _error = "";
    [ObservableProperty] private bool _runAsAdmin, _advancedExpanded, _isIconPickerOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLabel), nameof(TargetPlaceholder), nameof(CanBrowse), nameof(IsFolder), nameof(IsApplication), nameof(IsFile), nameof(IsUrl), nameof(CanSetArguments), nameof(PreviewGlyph))]
    private LaunchKind _selectedKind;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(BrowseCommand), nameof(SelectLocationCommand), nameof(SelectTypeCommand),
        nameof(OpenIconPickerCommand), nameof(SelectGlyphCommand), nameof(SelectColorCommand), nameof(SelectCatalogIconCommand), nameof(SelectIconFileCommand), nameof(ResetIconCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private bool _canEdit = true;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(BrowseCommand), nameof(SelectLocationCommand), nameof(SelectTypeCommand),
        nameof(OpenIconPickerCommand), nameof(SelectGlyphCommand), nameof(SelectColorCommand), nameof(SelectCatalogIconCommand), nameof(SelectIconFileCommand), nameof(ResetIconCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private bool _isSaving;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGlyph))]
    private string? _customIcon;
    [ObservableProperty] private int _glyphCode = IconCatalog.Glyphs[0].CodePoint;
    [ObservableProperty] private string _iconColor = IconCatalog.Colors[0].Hex;
    [ObservableProperty] private LaunchItem? _previewTarget;

    public IReadOnlyList<LibraryGlyph> Glyphs { get; }
    public IReadOnlyList<IconColor> Colors => IconCatalog.Colors;
    public int? SelectedGlyph => IconSpec.Parse(CustomIcon) is { IsGlyph: true } spec ? spec.CodePoint : null;
    public bool IsEditing => CanEdit && !IsSaving && !_closed;
    public bool IsApplication => SelectedKind == LaunchKind.App;
    public bool IsFolder => SelectedKind == LaunchKind.Folder;
    public bool IsFile => SelectedKind == LaunchKind.File;
    public bool IsUrl => SelectedKind == LaunchKind.Url;
    public bool CanBrowse => SelectedKind != LaunchKind.Url;
    public bool IsShortcut => System.IO.Path.GetExtension(ItemEditorValidation.NormalizeTarget(Path)).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    public bool CanSetArguments => IsApplication && !IsShortcut;
    public string TargetLabel => SelectedKind switch { LaunchKind.App => "程序路径", LaunchKind.File => "文件路径", LaunchKind.Folder => "文件夹路径", _ => "网址" };
    public string TargetPlaceholder => SelectedKind == LaunchKind.Url ? "https://" : TargetLabel;
    public string PreviewGlyph => SelectedKind switch { LaunchKind.File => "\uE8A5", LaunchKind.Folder => "\uE8B7", LaunchKind.Url => "\uE774", _ => "\uE71D" };
    public LaunchItem? Result { get; private set; }
    public Task TargetRefresh { get; private set; } = Task.CompletedTask;
    public event Action? Accepted;

    public EditItemViewModel(IFilePicker picker, IItemEditorEnvironment environment)
    { _picker = picker; _environment = environment; Glyphs = environment.AvailableGlyphs; }

    public void Initialize(LaunchItem? item, bool canEdit = true)
    {
        _initializing = true;
        try
        {
            Title = item is null ? "添加项目" : "编辑项目"; SaveLabel = item is null ? "添加" : "保存";
            Name = item?.Name ?? ""; Path = item?.Path ?? ""; Arguments = item?.Arguments ?? "";
            WorkingDirectory = item?.WorkingDirectory ?? ""; RunAsAdmin = item?.RunAsAdmin ?? false;
            SelectedKind = item?.Kind ?? LaunchKind.App; CanEdit = canEdit; Error = ""; Result = null; _autoName = false;
            AdvancedExpanded = false;
            CustomIcon = IconSpec.Normalize(item?.Icon);
            GlyphCode = IconCatalog.Glyphs[0].CodePoint; IconColor = IconCatalog.Colors[0].Hex;
            if (IconSpec.Parse(CustomIcon) is { IsGlyph: true } spec) { GlyphCode = spec.CodePoint; IconColor = spec.Color!; }
        }
        finally { _initializing = false; }
        RefreshPreview();
    }

    partial void OnNameChanged(string value) { if (!_settingName) _autoName = false; }
    private void FillName(string name)
    {
        if (!string.IsNullOrWhiteSpace(Name) && !_autoName) return;
        _settingName = true;
        try { Name = name[..Math.Min(name.Length, 160)]; _autoName = true; }
        finally { _settingName = false; }
    }
    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(IsShortcut)); OnPropertyChanged(nameof(CanSetArguments));
        if (_initializing || _closed) return;
        Error = "";
        if (ItemEditorValidation.InferUrlName(value) is { } host)
        {
            _inferring = true;
            try { SelectedKind = LaunchKind.Url; FillName(host); }
            finally { _inferring = false; }
        }
        QueueRefresh(true);
    }
    partial void OnSelectedKindChanged(LaunchKind value)
    {
        // Returning to App must not expand the dialog merely because its type changed.
        AdvancedExpanded = false;
        if (_initializing || _inferring) return;
        Error = ""; QueueRefresh(false);
    }
    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void SelectType(LaunchKind kind)
    {
        if (!IsEditing) return;
        SelectedKind = kind; Error = "";
        // Clicking an already checked card also cancels pending inference.
        QueueRefresh(false);
    }

    public void RefreshPreview() => QueueRefresh(false);
    private void CancelRefresh() { _refresh?.Cancel(); _refresh?.Dispose(); _refresh = null; }
    private void QueueRefresh(bool inferKind, bool immediate = false)
    {
        CancelRefresh(); PreviewTarget = null;
        if (_closed || IsSaving) return;
        _refresh = new();
        TargetRefresh = RefreshTargetAsync(Path, inferKind, immediate, _refresh.Token);
    }
    private async Task RefreshTargetAsync(string text, bool inferKind, bool immediate, CancellationToken cancellation)
    {
        try
        {
            if (!immediate) await Task.Delay(200, cancellation);
            string path = ItemEditorValidation.NormalizeTarget(text);
            bool web = LaunchService.IsWebUrl(path);
            if (!web) path = Environment.ExpandEnvironmentVariables(path);
            var facts = web || (!inferKind && CustomIcon is not null) ? (File: false, Directory: false)
                : await _environment.InspectAsync(path, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (inferKind && ItemEditorValidation.InferKind(path, facts.File, facts.Directory) is { } kind)
            {
                _inferring = true;
                try { SelectedKind = kind; }
                finally { _inferring = false; }
            }
            if (CustomIcon is not null || (SelectedKind == LaunchKind.Url ? web : facts.File || facts.Directory))
                PreviewTarget = new("", path, SelectedKind) { Icon = CustomIcon };
        }
        catch (Exception ex) when (ex is OperationCanceledException or ArgumentException or IOException or UnauthorizedAccessException)
        { /* Invalid or unavailable targets keep the kind glyph visible. */ }
    }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void Browse()
    {
        if (!IsEditing || !CanBrowse || _picker.PickTarget(SelectedKind) is not { } path) return;
        string name = IsFolder ? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) : System.IO.Path.GetFileNameWithoutExtension(path);
        FillPath(path, name.Length > 0 ? name : path);
    }
    private void FillPath(string path, string name) { Path = ItemIdentity.PortablePath(path); FillName(name); }
    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void SelectLocation(CommonLocation location)
    {
        if (!IsEditing) return;
        try
        {
            FillPath(_environment.ResolveLocation(location), location switch
            { CommonLocation.Desktop => "桌面", CommonLocation.Downloads => "下载", CommonLocation.Documents => "文档", CommonLocation.Pictures => "图片", _ => "用户目录" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = "无法读取该常用位置。"; }
    }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void OpenIconPicker() { if (IsEditing) IsIconPickerOpen = true; }
    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void SelectGlyph(int code) { if (IsEditing) { GlyphCode = code; ApplyGlyph(); } }
    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void SelectColor(string color) { if (IsEditing) { IconColor = color; ApplyGlyph(); } }
    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void SelectCatalogIcon(string spec) { if (IsEditing) CustomIcon = IconSpec.Normalize(spec); }
    private void ApplyGlyph()
    {
        if (!Glyphs.Any(g => g.CodePoint == GlyphCode))
        {
            if (Glyphs.FirstOrDefault() is not { } first) return;
            GlyphCode = first.CodePoint;
        }
        CustomIcon = $"glyph:{GlyphCode:X4}:{IconColor}";
    }
    partial void OnCustomIconChanged(string? value) { if (!_initializing) QueueRefresh(false, immediate: true); }
    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void SelectIconFile()
    {
        if (!IsEditing) return;
        IsIconPickerOpen = false;
        if (_picker.PickIcon() is { } path) SetFileIcon(path);
    }
    public void SetFileIcon(string path)
    { if (IsEditing) CustomIcon = IconSpec.Normalize("file:" + ItemIdentity.PortablePath(path)); }
    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void ResetIcon() { if (IsEditing) CustomIcon = null; }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    private async Task SaveAsync()
    {
        if (!IsEditing) return;
        string name = Name.Trim(), path = ItemEditorValidation.NormalizeTarget(Path), working = WorkingDirectory.Trim();
        string arguments = Arguments; bool admin = RunAsAdmin; LaunchKind selected = SelectedKind; string? icon = CustomIcon;
        if (name.Length == 0 || path.Length == 0) { Error = "名称和目标不能为空。"; return; }
        IsIconPickerOpen = false; CancelRefresh(); IsSaving = true; Error = "正在检查目标…";
        try
        {
            string expanded = selected == LaunchKind.Url ? path : Environment.ExpandEnvironmentVariables(path);
            var facts = selected == LaunchKind.Url ? (File: false, Directory: false) : await _environment.InspectAsync(expanded, _lifetime.Token);
            if (_closed || !CanEdit) return;
            var kind = ItemEditorValidation.Validate(selected, expanded, facts.Directory, Environment.ExpandEnvironmentVariables(working));
            if (kind != LaunchKind.Url) path = ItemIdentity.PortablePath(expanded);
            bool shortcut = kind == LaunchKind.App && System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            Result = new(name, path, kind, kind == LaunchKind.App && !shortcut ? arguments : "",
                kind == LaunchKind.App && !shortcut ? working : "", kind == LaunchKind.App && admin) { Icon = icon };
            Accepted?.Invoke();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { if (!_closed) Error = ex.Message; }
        finally { if (!_closed) IsSaving = false; }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true; CanEdit = false; CancelRefresh(); _lifetime.Cancel(); _lifetime.Dispose();
    }
}
