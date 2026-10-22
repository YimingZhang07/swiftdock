using System.ComponentModel;
using System.IO;
using Swiftdock.Models;

namespace Swiftdock.Services;

public interface ISettingsState : INotifyPropertyChanged
{
    bool CanEdit { get; }
    string DirectoryPath { get; }
    HotkeyConfig Hotkey { get; }
    int Scale { get; }
    string Placement { get; }
    string Theme { get; }
    string Backdrop { get; }
    int BackdropOpacity { get; }
    bool KeepBackdropWhenInactive { get; }
    string Accent { get; }
    bool AutoFetchWebsiteIcons { get; }
    void ApplySettings(HotkeyConfig hotkey);
    void SetScale(int scale);
    void SetPlacement(string placement);
    void SetTheme(string theme);
    void SetBackdrop(string backdrop);
    void SetBackdropOpacity(int opacity);
    void SetKeepBackdropWhenInactive(bool enabled);
    void SetAccent(string accent);
    void SetAutoFetchWebsiteIcons(bool enabled);
}

public interface IHotkeyService
{
    bool Registered { get; }
    int Error { get; }
    HotkeyConfig? Current { get; }
    bool TryChange(HotkeyConfig candidate);
}

public interface ISettingsActions
{
    void ResetWindowSize();
    void HotkeyChanged();
    Task ExitAsync();
    void OpenLogs();
}

internal sealed class SettingsActions(Action reset, Action hotkeyChanged, Func<Task> exit, Action? openLogs = null) : ISettingsActions
{
    public void ResetWindowSize() => reset();
    public void HotkeyChanged() => hotkeyChanged();
    public Task ExitAsync() => exit();
    public void OpenLogs() => openLogs?.Invoke();
}

public interface IFilePicker
{
    string? PickTarget(LaunchKind kind);
    string? PickIcon();
}

public interface IItemEditorEnvironment
{
    IReadOnlyList<LibraryGlyph> AvailableGlyphs { get; }
    string ResolveLocation(CommonLocation location);
    Task<(bool File, bool Directory)> InspectAsync(string path, CancellationToken cancellation);
}

internal sealed class ItemEditorEnvironment : IItemEditorEnvironment
{
    public IReadOnlyList<LibraryGlyph> AvailableGlyphs => IconCatalog.AvailableGlyphs.ToArray();
    public string ResolveLocation(CommonLocation location) => CommonLocations.Resolve(location);
    public Task<(bool File, bool Directory)> InspectAsync(string path, CancellationToken cancellation) =>
        Task.Run(() => (File.Exists(path), Directory.Exists(path)), cancellation);
}
