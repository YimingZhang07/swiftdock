using System.Diagnostics;
using Swiftdock.DragDrop;
using Swiftdock.Models;
using Swiftdock.Views;

namespace Swiftdock.Services;

// The panel model requests interactions without knowing about dialog owners or windows.
public interface ILauncherDialogs
{
    bool HasOpenEditor { get; }
    LaunchItem? EditItem(LaunchItem? item, bool openIconPicker);
    string? Name(string title, string value);
    Task<bool> ConfirmDeleteGroupAsync(string name);
}

internal sealed class LauncherDialogs(IDialogService dialogs, Func<LauncherWindow> owner) : ILauncherDialogs
{
    public bool HasOpenEditor => dialogs.HasOpenEditor;
    public LaunchItem? EditItem(LaunchItem? item, bool openIconPicker) => dialogs.EditItem(owner(), item, openIconPicker);
    public string? Name(string title, string value) => dialogs.Name(owner(), title, value);
    public Task<bool> ConfirmDeleteGroupAsync(string name) => dialogs.ConfirmDeleteGroupAsync(owner(), name);
}

public interface ILauncherActions
{
    Task<LaunchResult> LaunchAsync(LaunchItem item, bool elevate);
    Task OpenLocationAsync(LaunchItem item);
    void RefreshIcon(LaunchItem item, IEnumerable<LaunchItem> items);
    Task<DropResolution> ResolveDropAsync(DropSnapshot snapshot, CancellationToken cancellation);
}

internal sealed class LauncherActions(IconService icons, Func<LaunchItem, bool, Task<LaunchResult>> startLaunch) : ILauncherActions
{
    public Task<LaunchResult> LaunchAsync(LaunchItem item, bool elevate) => startLaunch(item, elevate);
    public Task<DropResolution> ResolveDropAsync(DropSnapshot snapshot, CancellationToken cancellation) => DropParser.ResolveAsync(snapshot, cancellation);
    public Task OpenLocationAsync(LaunchItem item) => Task.Run(() =>
    {
        string path = Environment.ExpandEnvironmentVariables(item.Path);
        if (path.Contains('"')) throw new ArgumentException("路径包含非法引号。");
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    });
    public void RefreshIcon(LaunchItem item, IEnumerable<LaunchItem> items)
    {
        icons.Refresh(item);
        var affected = IconService.UsesFavicon(item) && FaviconService.TryUrl(item.Path, out var url)
            ? items.Where(i => IconService.UsesFavicon(i) && FaviconService.TryUrl(i.Path, out var other)
                && FaviconParser.Origin(url) == FaviconParser.Origin(other)) : [item];
        foreach (var current in affected) { current.Visual.BeginRequest(0); current.Visual.Image = null; }
    }
}
