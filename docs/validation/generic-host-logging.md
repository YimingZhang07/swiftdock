# Task 15 validation

> Historical report: the in-process smoke suite and its commands were retired in Task 19. Use the three tiers and coverage mapping in [test-consolidation.md](test-consolidation.md) for current validation.

The reviewer approved the migration after external validation: **366/366 unit tests, 548/548 desktop smoke checks, 12/12 real-desktop E2E scenarios, and +24.8 ms median startup**. These results are reviewer-reported. The owner selected standard Serilog shared daily/size rolling files with 20-file retention, replacing the original per-session filename requirement; no custom retention is permitted or implemented. Every event carries `Pid` and a GUID `Session`. See [logging details](../LOGGING.md).

The shared-file follow-up adds `shared: true`, updates both measurement scripts to select a process/session across the newest rolling segments, and tests concurrent independent loggers sharing one file. The original sandbox results below remain historical; follow-up results are recorded separately.

## Shared-file follow-up checks

- `scripts/Validate.ps1`: locked restore and Release solution build succeeded with **0 warnings/errors**; unit tests **362 passed, 5 failed, 367 total** (exit 1). The five failures are the same sandbox-denied named-pipe/file-replacement cases listed below. All **9 hosting/logging cases passed**, including the new concurrent shared-file/session-isolation test.
- Explicit `QuickLaunch.E2E` Release build with `--no-restore --disable-build-servers -m:1 -warnaserror`: **exit 0, 0 warnings/errors**. The reviewer will rerun desktop smoke and E2E for this follow-up.
- `ReadDiagnostics.ps1` fixture passed: multiple rolling segments, other processes, an earlier session sharing the PID, the launch-time boundary, legacy filename exclusion, an incomplete trailing line, and a missing-session error. Fixture: `artifacts/task15/shared-reader-5d392db5d1ad4dd9bcc6b836f2a8c580`.
- `MeasureStartup.ps1 -Count 1 -AllowUnavailableTray` successfully read the actual app's new `Pid`/GUID `Session` events. This was a functional reader check, not a new median startup comparison. Report: `artifacts/startup/5e48c5cee25b446896b0f135433106bf/startup.json`.
- Async batching, unwritable-directory fallback, 4 MiB rolling and 20-file retention tests all pass with the shared sink. The index hash remains unchanged; no package or E2E changes were made.

No package references or versions changed. No new packages or probe executables were added. The staged work was preserved: the SHA-256 of `.git/index` before and after is `61EF654211311BF1975437E8DB5B6CB00CA8F24D9A665CDFFF3D8B44173D8BEF`. The E2E suite, its launch recorder format, and `smoke-result.json` schema are unchanged.

## Implementation and removed code

- `Host.CreateEmptyApplicationBuilder` is called after client-only `--exit` and secondary-instance handling. Configuration/environment/console/EventLog defaults are not enabled. A WPF lifetime replaces the console lifetime.
- `ApplicationServices` registers the singleton launcher graph and dialog/settings factories. Registration and host start are lazy; the launcher is resolved only after host start. Factories avoid constructor scanning on the startup path.
- The host owns UI/service disposal; the view model stops its debounce timer even on a partial startup failure. Config is flushed before normal stop; the instance lease survives until config disposal. Serilog drains last. Session ending uses bounded, dispatcher-independent drains after UI cleanup.
- Deleted all 56 lines of `Services/DiagnosticLog.cs`, including its custom channel, JSON serialization, retention deletion loop, size accounting and stream writer. Removed App's manual config/model/theme/startup/icon/window/hotkey constructors and disposal chain, and moved production dialog creation to factories. Extracted the icon STA lifetime so DI can own it and the favicon service independently.
- Replaced every diagnostic `Write` with generated `LoggerMessage` methods and `ILogger<T>`. A small adapter uses Serilog's existing batch worker to initialize and feed the standard file sink off the logging caller's thread. The file sink owns rolling and retention. File failure disables output and preserves one `log.fallback` event in memory.
- Updated startup/benchmark scripts, the favicon log assertion, README, QUICKSTART and historical format references. E2E launch JSONL and benchmark/smoke report JSON stay separate.

## Startup measurement

Three baseline and three final process launches used Release `win-x64` apphost, fresh isolated profiles, and identical `--startup-probe --smoke-test --silent` flags. The smoke flag permits the already-supported tray-unavailable path on this restricted desktop. Measurements include the same readiness milestone; they do not establish successful Explorer tray creation or cold-boot performance. Filesystem caches were not cleared.

| processToReadyMs | Run 1 | Run 2 | Run 3 | Median |
| --- | ---: | ---: | ---: | ---: |
| Before | 525.1285 | 519.3884 | 510.4025 | 519.3884 |
| After | 545.1974 | 543.1081 | 544.1434 | 544.1434 |

Median increase: **24.7550 ms**, below the 40 ms target. Final samples: `artifacts/startup/b476e98fe530426d82c959f84c0ea851/startup.json` (also copied to `artifacts/task15/startup-after.json`).

The initial migrated median was 562.1109 ms (+42.7225 ms). `startup.host` instrumentation attributed approximately 9 ms to the empty builder, 12 ms to registration/logger setup, and 22 ms to build/start. Generated logging definitions were separated by consumer/lifecycle so the first window event does not initialize all event templates. URL sanitization avoids regex initialization for messages without web URLs. A later pre-optimization run reached 575.3734 ms median (+55.9850 ms). Independent logger and empty-host initialization now overlap after the single-instance guard; an exit arriving during that await prevents graph construction. File sink creation remains on Serilog's worker; no reflection-based assembly scanning or default configuration providers were introduced.

## Original sandbox checks

| Command/check | Result |
| --- | --- |
| `scripts/Validate.ps1` | Exit 1. Locked restore succeeded. Release solution build: 0 warnings, 0 errors. Tests: **361 passed, 5 failed, 0 skipped, 366 total**. |
| New hosting/logging cases | **8/8 passed** within the full suite. Covers lazy graph resolution, singleton lifetimes, fresh dialogs, UI-thread disposal and lease order, CLEF events/properties, blocked file creation off the caller, queue drain, 4 MiB rolling, 20-file retention, privacy and unwritable-path fallback. |
| `scripts/dotnet.ps1 build QuickLaunch.E2E/QuickLaunch.E2E.csproj -c Release --no-restore --disable-build-servers -m:1 -warnaserror` | Exit 0; **0 warnings, 0 errors**. FlaUI's 12 real-input scenarios were not run in this sandbox; reviewer desktop execution remains required. |
| `scripts/Run.ps1 -Smoke` | Exit 1; **521 passed, 3 failed, 524 checks recorded; completed=false**. Native tray/capture checks fail, followed by access-denied config replacement. |
| Existing apphost with a file blocking `--diagnostics-dir` | Exit 0 with `--startup-probe --smoke-test --silent`; no logging exception escaped. |
| `git diff --check` | Passed. |

The five unit failures all report `UnauthorizedAccessException` in existing infrastructure: `NamedPipeClientStream.TryConnect` for `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance`; `FileSystem.ReplaceFile` for `ConfigTests.LegacyAutoHideSettingIsRetiredWithoutLosingItems`, `SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`, `FailureKeepsOldFileAndAllowsSameRevisionRetry`, and `SlowWriteAndQueuedRevisionsKeepNewestSnapshot`. Their implementation/assertions were not weakened or changed.

The desktop smoke's failed native checks are `tray-created`, `root-capture-acquired`, and `capture-survives-hidden-source-group`. It then aborts at the existing denied atomic config replacement, logging `startup.failed`, `save.exit-failed`, and `shutdown` in CLEF. `completed` remains false in the unchanged smoke report. This sandbox run did not pass; the subsequent external reviewer results are recorded above.

Original raw evidence: `artifacts/task15/launcher-final.trx`, `artifacts/task15/before/startup.json`, the final startup directory listed above, `artifacts/task15/smoke-final.json`, and rolling files under `artifacts/validation` (current builds filter by `Pid`/`Session`; the original retained evidence used lowercase `pid`/`session`). Follow-up unit results are in `artifacts/tests/launcher.trx` and `artifacts/tests/task15-shared-logging.trx`.

## Files changed

| File | Change |
| --- | --- |
| `QuickLaunch.Tests/CustomIconTests.cs` | Modified |
| `QuickLaunch.Tests/DialogTests.cs` | Modified |
| `QuickLaunch.Tests/FaviconTests.cs` | Modified |
| `QuickLaunch.Tests/HostingTests.cs` | Added |
| `QuickLaunch.Tests/IconTests.cs` | Modified |
| `QuickLaunch.Tests/LauncherInputTests.cs` | Modified |
| `QuickLaunch.Tests/LoggingTests.cs` | Added |
| `QuickLaunch.Tests/TestLogging.cs` | Added |
| `QuickLaunch.Tests/ThemeTests.cs` | Modified |
| `QuickLaunch/App.xaml.cs` | Modified |
| `QuickLaunch/Diagnostics/BenchmarkRunner.cs` | Modified |
| `QuickLaunch/Diagnostics/FaviconSmoke.cs` | Modified |
| `QuickLaunch/Diagnostics/LauncherAppearanceSmoke.cs` | Modified |
| `QuickLaunch/Diagnostics/LauncherInputSmoke.cs` | Modified |
| `QuickLaunch/Diagnostics/LogEvents.cs` | Added |
| `QuickLaunch/Diagnostics/LogPrivacy.cs` | Added |
| `QuickLaunch/Diagnostics/PrototypeSmoke.cs` | Modified |
| `QuickLaunch/Diagnostics/SessionLogging.cs` | Added |
| `QuickLaunch/Diagnostics/WindowPositionSmoke.cs` | Modified |
| `QuickLaunch/Diagnostics/WindowVisibilitySmoke.cs` | Modified |
| `QuickLaunch/DragDrop/DragCoordinator.cs` | Modified |
| `QuickLaunch/Services/ApplicationServices.cs` | Added |
| `QuickLaunch/Services/DiagnosticLog.cs` | Removed |
| `QuickLaunch/Services/FaviconService.cs` | Modified |
| `QuickLaunch/Services/IconService.cs` | Modified |
| `QuickLaunch/Services/IconThread.cs` | Added |
| `QuickLaunch/ViewModels/MainViewModel.cs` | Modified |
| `QuickLaunch/Views/DialogFactory.cs` | Added |
| `QuickLaunch/Views/LauncherWindow.Management.cs` | Modified |
| `QuickLaunch/Views/LauncherWindow.xaml.cs` | Modified |
| `README.md` | Modified |
| `docs/LOGGING.md` | Added |
| `docs/QUICKSTART.zh-CN.md` | Modified |
| `docs/validation/M0.md` | Modified |
| `docs/validation/generic-host-logging.md` | Added |
| `docs/validation/wpf-ui.md` | Modified |
| `scripts/Benchmark.ps1` | Modified |
| `scripts/MeasureStartup.ps1` | Modified |
| `scripts/ReadDiagnostics.ps1` | Added |
