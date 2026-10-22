using System.ComponentModel;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Swiftdock.Diagnostics;

namespace Swiftdock.Services;

// Keep the language subscription testable without creating a native tray icon.
internal sealed class TrayTooltip : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Func<string> _hotkey;
    private readonly ILogger _log;

    internal TrayTooltip(TaskbarIcon icon, Func<string> hotkey, ILogger log)
    {
        _icon = icon; _hotkey = hotkey; _log = log;
        L.Observe(LanguageChanged);
        Update();
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => Update();

    internal void Update()
    {
        string text = L.Text("Swiftdock0", _hotkey());
        if (_icon.ToolTipText == text) return;
        _icon.ToolTipText = text;
        // H.NotifyIcon 2.4.1 synchronously calls UpdateToolTip here. For a created
        // icon it sets TrayIcon.ToolTip only after Shell_NotifyIcon succeeds.
        _log.TrayTooltipChanged(L.Instance.Culture.Name, _icon.ToolTipText, _icon.TrayIcon.ToolTip, _icon.IsCreated);
    }

    public void Dispose() => L.Unobserve(LanguageChanged);
}
