# Host and diagnostics

Swiftdock creates an empty .NET Generic Host only after the single-instance handshake. `--exit` remains a client-only path. No appsettings, environment configuration, user secrets, console logging, or Windows Event Log providers are loaded. WPF owns the message loop through a no-op `IHostLifetime`.

The config store/writer, view model, theme, startup service, icon STA thread, icon/favicon services, hotkey and launcher are singletons. Settings, editors, name prompts and deletion confirmations are created on demand by factories; each call creates a fresh window. Normal exit suspends changes and flushes config before stopping the host. DI disposes the hotkey/window before their dependencies and releases the single-instance lease after config disposal. Serilog drains last. Session ending retains the 800 ms best-effort save and allows up to two seconds for cleanup without waiting for the UI dispatcher.

## Location and format

The default log directory is `%LOCALAPPDATA%\Swiftdock\logs`. Portable mode uses `Data\logs` beside the executable; `--diagnostics-dir <directory>` overrides either location. Diagnostic files use Serilog compact JSON (CLEF), one JSON object per UTF-8 line:

```json
{"@t":"2026-10-15T00:00:00Z","@mt":"Hotkey changed: {hotkey}","hotkey":"Alt+Space","EventId":{"Id":6,"Name":"hotkey.changed"},"SourceContext":"Swiftdock.Views.LauncherWindow","Session":"a18b4d02e97a4c1ca81b83f3846496d9","Pid":1234}
```

`@t` is a UTC timestamp, `@mt` is a message template, and `@l` is the level (omitted for Information). The stable event name is `EventId.Name`. Structured fields are top-level properties, including `Kind`, `Accepted`, `Cancelled`, `Message`, `processToReadyMs`, `origin`, and `saved`. Filter on `EventId.Name`, not `@mt` or CLEF's reserved `@i`. `SourceContext` contains the `ILogger<T>` category. Every event, including the in-memory fallback, carries numeric `Pid` and a GUID `Session`. The session GUID remains constant for the process session and distinguishes sessions even when Windows reuses a PID.

The sink configuration is `swiftdock-.jsonl` with `RollingInterval.Day`, `rollOnFileSizeLimit: true`, `fileSizeLimitBytes: 4 * 1024 * 1024`, `retainedFileCountLimit: 20`, and `shared: true`. Each diagnostics directory has one shared daily rolling file, named `swiftdock-yyyyMMdd.jsonl`, with size-roll suffixes such as `_001`. Several processes, including independent `--config-dir` instances, can write the same file. Serilog manages rolling and retention; there is no custom session filename or retention routine. Legacy timestamp/PID files do not match the rolling pattern and are left untouched.

Serilog 4.3's built-in batching queues at most 512 events, in batches of up to 64 with a 250 ms buffering interval. A small `IBatchedLogEventSink` adapter creates the standard file sink on that worker. Opening files, formatting CLEF, writing, flushing and retention never run in a UI logging call. Queue saturation may drop events. If file creation or writing fails, file logging is disabled for the session and the first `log.fallback` event remains in the DI singleton `LoggingDiagnostics.Failure`; there is no second directory fallback or recursive file logging.

Source-generated `LoggerMessage` methods define the event names. Favicon diagnostics include origins only; Shell/error messages strip web URL paths and queries. File contents are never diagnostic payloads. `benchmark.json` contains performance measurements. The E2E launch recorder's `id`, `name`, `elevated` JSONL is a separate test fixture.

`theme.apply` records the source, target theme/accent, number of theme-dictionary reloads,
synchronous elapsed milliseconds, recursive merged-dictionary counts before/after, and
tracked dialog count. Sources distinguish startup, Theme/Accent/Backdrop setting changes,
Windows user-preference categories, and panel messages `0x001A` (WM_SETTINGCHANGE),
`0x0015` (WM_SYSCOLORCHANGE), `0x031A` (WM_THEMECHANGED), and `0x0320`
(WM_DWMCOLORIZATIONCOLORCHANGED). Notifications queued before the background dispatcher
callback are combined into one Apply; later notifications can schedule another. Opening
or closing a dialog does not itself call Apply, but its native material work can coincide
with these notifications. Use timestamps and sources to distinguish repeated notification
batches from a slow Apply when investigating UIA timeouts.

Normal Apply publishes the palette before one dictionary reload. A custom palette changing
the actual WPF-UI theme needs a preliminary reload because the explicit-palette overload
reads `GetAppTheme()`; system accent uses the overload accepting a target theme directly.
High contrast performs one reload and keeps the existing override cleanup.

## Reading and measuring

```powershell
# Run after the selected process exits and drains its logger. Capture $startedAfter
# immediately before launching it; $process.Id is the returned process ID.
. ./scripts/ReadDiagnostics.ps1
$events = @(Read-SwiftdockSession -Directory $logs -ProcessId $process.Id -StartedAfter $startedAfter)
$events | Where-Object { $_.EventId.Name -eq 'startup' } |
    Select-Object Pid, Session, processToReadyMs
$events | Where-Object { $_.EventId.Name -eq 'show.ui-proxy' } |
    Select-Object source, milliseconds, foreground, focus
```

Both measurement scripts use `ReadDiagnostics.ps1` to discover retained rolling segments newest-first. The reader identifies the latest startup event for the launched `Pid` after its launch boundary, then selects only that `Session` across all matching segments. It excludes other processes, earlier sessions, and legacy filenames, and tolerates a trailing incomplete line from another active writer. Do not select a session by filename or read only the latest segment: a session may cross a size/date boundary.

`scripts/MeasureStartup.ps1 -Executable <exe> -Count 3` reads the selected session's CLEF startup event and rejects missing measurements. For restricted desktops only, `-AllowUnavailableTray` adds the performance-only `--allow-unavailable-tray` flag to the startup probe so tray creation failure is recorded and measurement can continue. Use the same flags on both sides of a comparison. These are process launches with filesystem caching, not cold-boot measurements. `startup.host` reports empty-builder, registration, and build/start durations separately; it is emitted after the readiness measurement.

`scripts/Benchmark.ps1` reads CLEF for startup time and the UI-proxy event count, then prints the benchmark report. Neither script treats a missing event as a zero-duration result.

Snapshot rendering writes `artifacts/validation/snapshots.json`.
