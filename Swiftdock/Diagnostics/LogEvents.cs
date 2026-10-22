using Microsoft.Extensions.Logging;

namespace Swiftdock.Diagnostics;

internal static partial class ThemeLogEvents
{
    [LoggerMessage(32, LogLevel.Information, "Theme apply: {source}, {theme}/{accent}; {reloads} reloads in {milliseconds} ms; dictionaries {before}->{after}; {dialogs} dialogs", EventName = "theme.apply")]
    internal static partial void ThemeApplied(this ILogger logger, string source, string theme, string accent,
        int reloads, double milliseconds, int before, int after, int dialogs);
}

internal static partial class LoggingLogEvents
{
    [LoggerMessage(1, LogLevel.Warning, "File logging disabled: {Message}", EventName = "log.fallback")]
    internal static partial void LogFallback(this ILogger logger, string message);
}

internal static partial class AppLogEvents
{
    [LoggerMessage(35, LogLevel.Information, "Copied the legacy QuickLaunch configuration into Swiftdock; legacy files retained", EventName = "migration.config")]
    internal static partial void LegacyConfigCopied(this ILogger logger);

    [LoggerMessage(36, LogLevel.Information, "Migrated {count} legacy QuickLaunch startup values", EventName = "migration.startup")]
    internal static partial void LegacyStartupMigrated(this ILogger logger, int count);

    [LoggerMessage(37, LogLevel.Warning, "Legacy startup migration failed: {Message}", EventName = "migration.startup-failed")]
    internal static partial void LegacyStartupMigrationFailed(this ILogger logger, string message);
    [LoggerMessage(33, LogLevel.Information, "Tray menu: {input}; cursor physical {cursor}; H.NotifyIcon callback {callback} ({version}); anchor physical {anchor}; overflow {overflow}, cursor root {cursorRoot}; menu physical {bounds}; DPI {dpi}; WPF keyboard focus within {keyboardFocusWithin}", EventName = "tray.menu")]
    internal static partial void TrayMenuOpened(this ILogger logger, string input, string cursor, string callback,
        string version, string anchor, string overflow, string cursorRoot, string bounds, uint dpi, bool keyboardFocusWithin);

    [LoggerMessage(34, LogLevel.Information, "Tray menu closed after {milliseconds} ms; menu HWND {menuHwnd}; foreground HWND {foregroundHwnd}, class {foregroundClass}", EventName = "tray.menu.closed")]
    internal static partial void TrayMenuClosed(this ILogger logger, double milliseconds, long menuHwnd, long foregroundHwnd, string foregroundClass);

    [LoggerMessage(2, LogLevel.Information, "Hotkey {hotkey}: {Registered}, {Error}", EventName = "hotkey.registered")]
    internal static partial void HotkeyRegistered(this ILogger logger, bool registered, int error, string hotkey);

    [LoggerMessage(3, LogLevel.Warning, "Tray unavailable: {Message}", EventName = "tray.unavailable")]
    internal static partial void TrayUnavailable(this ILogger logger, string message);

    [LoggerMessage(4, LogLevel.Information, "Ready in {processToReadyMs} ms; {runtime}, {os}, {elevated}, {milestone}, {configDirectory}, {IsReadOnly}, {Notice}", EventName = "startup")]
    internal static partial void Startup(this ILogger logger, string runtime, string os, bool elevated, string milestone,
        double processToReadyMs, string configDirectory, bool isReadOnly, string? notice);

    [LoggerMessage(5, LogLevel.Error, "Startup failed: {Message} ({type})", EventName = "startup.failed")]
    internal static partial void StartupFailed(this ILogger logger, string message, string type);

    [LoggerMessage(6, LogLevel.Information, "Hotkey changed: {hotkey}", EventName = "hotkey.changed")]
    internal static partial void HotkeyChanged(this ILogger logger, string hotkey);

    [LoggerMessage(7, LogLevel.Information, "Tray created", EventName = "tray.created")]
    internal static partial void TrayCreated(this ILogger logger);
}

internal static partial class AppShutdownLogEvents
{
    [LoggerMessage(8, LogLevel.Error, "Exit save failed: {Message}", EventName = "save.exit-failed")]
    internal static partial void SaveExitFailed(this ILogger logger, string message);

    [LoggerMessage(9, LogLevel.Information, "Shutdown: {code}, saved revision {savedRevision}", EventName = "shutdown")]
    internal static partial void Shutdown(this ILogger logger, int code, long? savedRevision);

    [LoggerMessage(10, LogLevel.Information, "Session ending: saved {saved}", EventName = "session-ending")]
    internal static partial void SessionEnding(this ILogger logger, bool saved);

    [LoggerMessage(11, LogLevel.Error, "Session save failed: {Message}", EventName = "session-ending.failed")]
    internal static partial void SessionEndingFailed(this ILogger logger, string message);
}

internal static partial class BenchmarkLogEvents
{
    [LoggerMessage(12, LogLevel.Information, "Benchmark idle: {idleSeconds} seconds", EventName = "benchmark.idle-start")]
    internal static partial void BenchmarkIdle(this ILogger logger, int idleSeconds);


}

internal static partial class DragLogEvents
{
    [LoggerMessage(16, LogLevel.Information, "Move {Id}: {source} -> {target}", EventName = "drag.commit")]
    internal static partial void DragCommit(this ILogger logger, string id, string source, string target);

    [LoggerMessage(17, LogLevel.Information, "Drag cancelled", EventName = "drag.cancel")]
    internal static partial void DragCancel(this ILogger logger);
}

internal static partial class FaviconLogEvents
{
    [LoggerMessage(18, LogLevel.Information, "Favicon {origin}: {outcome} in {ms} ms", EventName = "favicon.result")]
    internal static partial void FaviconResult(this ILogger logger, string origin, string outcome, long ms);
}

internal static partial class IconLogEvents
{
    [LoggerMessage(19, LogLevel.Warning, "Icon cache write failed: {Message}", EventName = "icon.cache-write-failed")]
    internal static partial void IconCacheWriteFailed(this ILogger logger, string message);

    [LoggerMessage(20, LogLevel.Warning, "Icon unavailable: {Kind}, {Message}", EventName = "icon.unavailable")]
    internal static partial void IconUnavailable(this ILogger logger, int kind, string message);
}

internal static partial class WindowLogEvents
{
    [LoggerMessage(31, LogLevel.Information, "Panel activation: {isActive}, foreground {foregroundHwnd}, keep {keepBackdrop}, warming {warming}, message {message}", EventName = "window.activation")]
    internal static partial void PanelActivation(this ILogger logger, bool isActive, long foregroundHwnd, bool keepBackdrop, bool warming, int message);
    [LoggerMessage(21, LogLevel.Information, "Window initialized: {cornerHresult}, {backdrop}, {dpi}", EventName = "window.initialized")]
    internal static partial void WindowInitialized(this ILogger logger, int cornerHresult, string backdrop, uint dpi);

    [LoggerMessage(22, LogLevel.Information, "Window DPI: {dpi}, {Left}, {Top}, {ActualWidth}, {ActualHeight}", EventName = "window.dpi")]
    internal static partial void WindowDpi(this ILogger logger, uint dpi, double left, double top, double actualWidth, double actualHeight);
}

internal static partial class WindowInteractionLogEvents
{
    [LoggerMessage(23, LogLevel.Information, "Show {source}: {milliseconds} ms, {foreground}, {focus}, {forDrop}, {physicalPresentationMeasured}", EventName = "show.ui-proxy")]
    internal static partial void ShowUiProxy(this ILogger logger, string source, double milliseconds, bool foreground, bool focus, bool forDrop, bool physicalPresentationMeasured);

    [LoggerMessage(24, LogLevel.Warning, "Show timeout: {source}, {foreground}, {focus}", EventName = "show.timeout")]
    internal static partial void ShowTimeout(this ILogger logger, string source, bool foreground, bool focus);

    [LoggerMessage(25, LogLevel.Information, "Warmup: {completed}, {milliseconds} ms, {interrupted}", EventName = "warmup.completed")]
    internal static partial void WarmupCompleted(this ILogger logger, bool completed, double milliseconds, bool interrupted);

    [LoggerMessage(26, LogLevel.Information, "Launch: {Kind}, {Accepted}, {Cancelled}, {Message}", EventName = "launch.result")]
    internal static partial void LaunchResult(this ILogger logger, int kind, bool accepted, bool cancelled, string message);

    [LoggerMessage(27, LogLevel.Information, "Drop capture: {Formats}, {paths}, {milliseconds} ms", EventName = "drop.capture")]
    internal static partial void DropCapture(this ILogger logger, string[] formats, int paths, double milliseconds);

    [LoggerMessage(28, LogLevel.Information, "Drop resolved: {count}, {errors}", EventName = "drop.resolved")]
    internal static partial void DropResolved(this ILogger logger, int count, int errors);

    [LoggerMessage(29, LogLevel.Warning, "Drop failed: {Message}", EventName = "drop.failed")]
    internal static partial void DropFailed(this ILogger logger, string message);
}

internal static partial class HostLogEvents
{
    [LoggerMessage(30, LogLevel.Information, "Host timing: {emptyBuilderMs}, {registrationMs}, {buildStartMs} ms", EventName = "startup.host")]
    internal static partial void HostTiming(this ILogger logger, double emptyBuilderMs, double registrationMs, double buildStartMs);
}
