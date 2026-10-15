# WPF-UI and graceful exit — task 11

> Historical report: the in-process smoke suite and its commands were retired in Task 19. Use the three tiers and coverage mapping in [test-consolidation.md](test-consolidation.md) for current validation.

Validated on 2026-10-14, Windows x64, .NET 10.0.12 / SDK 10.0.401, branch `ui/round2`. These are worktree changes relative to the reviewer's staged work. The index, application package references and both lock files are unchanged.

`App.xaml` merges WPF-UI 4.3.0 `ThemesDictionary` and `ControlsDictionary`. Standard controls use the library's implicit styles; the launcher tiles, tabs, icon buttons, toast link, segmented controls and icon picker retain their custom templates with library resources. Primary dialog actions use `ui:Button Appearance="Primary"`; the advanced section uses the implicit Expander. Dialogs remain ordinary Window instances.

Resource names were inspected in the installed 4.3.0 assembly's theme dictionaries, then tested against the actual net10.0 library in light, dark and high contrast. No old brush aliases remain. Accent text/rings and accent fills have separate resources. Popups and the drag ghost use flyout surfaces. A symbol font is applied only to icon-button content, so tooltips inherit the normal text font. Modifier checkboxes explicitly allow a smaller minimum width; all four fit the existing settings row.

ThemeService now delegates palette/accent application to ApplicationThemeManager and ApplicationAccentColorManager.ApplySystemAccent. Its remaining work is transparency preferences, Windows' app-mode override, and change notifications. The app-mode override is necessary because [WPF-UI 4.3.0 SystemThemeManager](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Appearance/SystemThemeManager.cs) prioritizes the `.theme` filename over AppsUseLightTheme. The launcher's existing HWND hook handles setting, theme, system-color and accent messages even before its first Loaded event; updates are coalesced. This equivalent change hook preserves the fallback policy instead of letting a watcher unconditionally restore a backdrop.

The launcher uses WindowBackdrop.IsSupported / ApplyBackdrop / RemoveBackdrop, with Acrylic only on Windows build 22621+ when transparency is enabled, high contrast is off, and native application succeeds. The existing OS gate is retained because [WPF-UI's Acrylic support check](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Controls/Window/WindowBackdrop.cs) also accepts older systems. Solid fallback restores dynamic ApplicationBackgroundBrush on the window and root, and the matching composition-target color. WindowBackdrop also sets the panel's immersive-dark state. The generic DWM P/Invoke remains in use for rounded corners and the ordinary dialogs' SourceInitialized title-bar update; the launcher's handwritten backdrop/title-bar attribute writes are gone. WindowChrome, resize hit testing and placement policy remain. Layout reserves the library scrollbar's 14 DIP plus the existing rounding allowance, increasing automatic/minimum width by 2 DIP.

`QuickLaunch.exe --exit [--config-dir <dir>]` connects using the existing user/session/config identity before profile-write checks or application UI startup. It never claims the primary mutex or starts a replacement. Command byte 2 requests exit; byte 1 continues to request show. The server flushes its acknowledgement before queuing the existing ExitAsync path, which flushes pending changes and disposes the tray. Return code 0 means acknowledged, 2 means no acknowledgement; existing save-failure prompts can still cancel shutdown after acknowledgement. Other startup errors return 1. README and QUICKSTART include script usage.

Removed code, counted against the index:

| Block | Lines removed |
| --- | ---: |
| App.xaml standard-control styles/templates, helpers and AccentButton | 209 |
| EditItemDialog AdvancedExpander template | 24 |
| ThemeService ApplyPalette and accent-luminance calculation | 25 |
| App.xaml brush/shadow defaults | 14 |
| Custom DialogFocus template (now library focus style) | 5 |

App.xaml shrank from 251 to 28 lines; ThemeService from 84 to 70. The old WinRT accent reading/subscription is also removed. No source file was deleted, no package was added or changed, and no analyzer suppression was needed.

This historical measurement used the pre-CLEF `startup.data.processToReadyMs` field (current builds use `EventId.Name == startup` and top-level `processToReadyMs`; see [logging](../LOGGING.md)). Startup was measured using fresh isolated profiles and the Release apphost. Both sides used `--startup-probe --smoke-test --silent`: the ordinary startup probe cannot create the tray in this sandbox, while the existing smoke flag logs that limitation and continues to the same readiness milestone. These are process launches with filesystem caching, not cold-boot measurements or a claim about successful desktop tray creation. Final-build samples are in `artifacts/validation/startup-before.json` and `startup-after.json`; per-run logs are in `startup-before-1..3/logs` and `startup-after-final-1..3/logs`.

| processToReadyMs | Run 1 | Run 2 | Run 3 | Median |
| --- | ---: | ---: | ---: | ---: |
| Before | 502.2292 | 500.8843 | 499.4835 | 500.8843 |
| Final build | 546.8493 | 525.3288 | 521.1476 | 525.3288 |

Median change: +24.4445 ms (+4.88%).

Validation:

- Locked offline restore and Release `-warnaserror` build: pass, 0 warnings, 0 errors.
- Baseline `scripts/Validate.ps1`: 337 passed, 6 failed, 343 total.
- Final `scripts/Validate.ps1` and `scripts/Validate.ps1 -DesktopSmoke`: 346 passed, 6 failed, 352 total; exit 1 at the test stage. All six failures are the same sandbox access restrictions seen before changes: four ConfigTests File.Replace failures (`SlowWriteAndQueuedRevisionsKeepNewestSnapshot`, `LegacyAutoHideSettingIsRetiredWithoutLosingItems`, `SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`, `FailureKeepsOldFileAndAllowsSameRevisionRetry`), the existing single-instance pipe integration test, and the startup-registry integration test. These tests were not skipped or weakened. The existing pipe integration test also exercises same-profile exit when run outside the sandbox.
- Focused theme/input/exit tests: 17 passed, 0 failed, exit 0 (`artifacts/tests/task11-focused.trx`). Tests cover production dictionaries and source resource references, live brush switching, all standard control styles, menu separator width, critical delete color, tray-menu style, backdrop policy, input/selection/drag routing, command rejection, acknowledgement ordering, acknowledgement-write failure, and no-instance exit without a profile or primary claim. WPF tests share one STA dispatcher and do not run App.OnStartup.
- Real no-instance CLI invocation: exit 2, configuration directory not created (`artifacts/validation/exit-absent.json`).
- Final `scripts/Run.ps1 -Smoke`: exit 1, 476 checks reached, 473 passed and 3 failed (`tray-created`, `root-capture-acquired`, `capture-survives-hidden-source-group`). These require native tray/capture access unavailable in the sandbox. All reached theme, backdrop, control, menu, tooltip, dialog, layout and input assertions passed. The run then stopped at the existing main-profile reorder/save flush with UnauthorizedAccessException from atomic file replacement, before the later real secondary-process checks. The report therefore correctly retains `completed: false`; this is not a fully passing desktop smoke. Graceful exit through a real pipe with pending saves and an actual tray still needs the reviewer's unrestricted desktop.

Popup scopes pump opening and closing events, and layout checks work without relying on capture or foreground. Snapshots wait for WPF-UI's entrance animation and capture the popup presentation root, including its decoration area. Images are composited over solid ApplicationBackgroundBrush; they do not capture native DWM Acrylic, the title bar or physical screen presentation.

All ten required final-build snapshots were generated and visually inspected in `artifacts/validation/`: `window.png` / `window-light.png`, `item-menu.png` / `item-menu-light.png`, `tooltip.png` / `tooltip-light.png`, `settings.png` / `settings-light.png`, and `edit-dialog.png` / `edit-dialog-light.png`. Menus and tooltips include their full content and decoration bounds; settings shows all four modifiers; the edit dialog uses the library Expander. Additional add/edit variants, icon-picker and ComboBox images are generated by the existing smoke suite. `git diff --check` passes.

Files changed:

| Area | Files |
| --- | --- |
| Application / services | `QuickLaunch/App.xaml`, `App.xaml.cs`; `Services/ThemeService.cs`, `SingleInstanceService.cs`, `WindowPlacement.cs` |
| Launcher / dialogs | `Views/LauncherWindow.xaml`, `LauncherWindow.xaml.cs`, `LauncherWindow.Management.cs`, `DialogTheme.cs`, `EditItemDialog.xaml`, `NameDialog.xaml`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs` |
| Diagnostics | `Diagnostics/DialogAppearanceSmoke.cs`, `EditItemDialogSmoke.cs`, `FaviconSmoke.cs`, `IconPickerSmoke.cs`, `LauncherAppearanceSmoke.cs`, `LauncherInputSmoke.cs`, `PopupSmokeScope.cs`, `PrototypeSmoke.cs`, `WindowLayoutSmoke.cs`; new `ThemeSmokeScope.cs` |
| Tests | `QuickLaunch.Tests/HotkeyAndInstanceTests.cs`, `LauncherInputTests.cs`, `ThemeTests.cs`, `WindowPlacementTests.cs`, `QuickLaunch.Tests.csproj`; new `WpfTestHost.cs` |
| Documentation | `README.md`, `docs/QUICKSTART.zh-CN.md`, this report |
