# Task 10 — ListBox tile selection

Implemented on `ui/round2`, starting from `4c30de2`. No packages, standalone probes, or git-index changes.

The tile groups now use single-selection `TileListBox` controls bound directly to `group.Items`. Their template has only an ItemsPresenter and a transparent hit surface; the existing outer Viewer owns scrolling. The WrapPanel retains CellSize slots. Non-focusable ListBoxItem containers contain the icon, label, error badge, and selected/hover/pressed visuals. Only IsSelected draws the accent ring. Tabs and header controls remain Buttons; the import-cancel link remains clickable and is excluded from keyboard focus.

Focus stays on the list. The small subclass translates wrap-grid and viewport-page keys into SelectedIndex changes because item containers are deliberately non-focusable and there is no internal ScrollViewer. No separate focused-item identity or cue state remains. Replacement of an immutable item record retains selection only when its ID is unchanged; removal clears selection normally. Nonactivating drop/prewarm shows clear selection and establish logical list focus without requesting foreground activation.

Single-click launch stays in DragCoordinator's release-without-drag path, including read-only mode. Container hit testing drives drag previews, insertion, ghost destinations, duplicate flashing, and context menus. The native hook handles only SC_KEYMENU within WM_SYSCOMMAND and retains UI-state suppression; WM_HOTKEY and unrelated system commands pass through.

Changed implementation files:

- `QuickLaunch/Views/TileListBox.cs` (new)
- `QuickLaunch/Views/LauncherWindow.xaml`
- `QuickLaunch/Views/LauncherWindow.xaml.cs`
- `QuickLaunch/Views/LauncherWindow.Management.cs`
- `QuickLaunch/DragDrop/DragCoordinator.cs`
- `QuickLaunch/Interop/NativeMethods.cs`

Changed validation/documentation files:

- `QuickLaunch.Tests/LauncherInputTests.cs`
- `QuickLaunch/Diagnostics/LauncherInputSmoke.cs`
- `QuickLaunch/Diagnostics/LauncherAppearanceSmoke.cs`
- `QuickLaunch/Diagnostics/IconPickerSmoke.cs`
- `README.md`
- `docs/QUICKSTART.zh-CN.md`
- This report. The Task 8 keyboard-cues report is historical and superseded by this implementation.

Validation on 2026-10-14:

- Ran `./scripts/Validate.ps1`, then `./scripts/Validate.ps1 -DesktopSmoke` after fixes. The final locked restore succeeded; Release `-warnaserror` build succeeded with **0 warnings, 0 errors**. The final test result was **337 passed, 6 failed, 0 skipped, 343 total**. The new shared routed-input/selection/drag fixture passed. See `artifacts/tests/launcher.trx`.
- The six failures were `UnauthorizedAccessException` in existing tests: `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance` (named pipe), `InteractionTests.StartupRegistryRoundtripTouchesOnlyTestKey` (registry), and `ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot`, `LegacyAutoHideSettingIsRetiredWithoutLosingItems`, `SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`, `FailureKeepsOldFileAndAllowsSameRevisionRetry` (atomic file replacement).
- Because Validate stops on those test failures, ran `./scripts/Run.ps1 -Smoke` directly on the final build. It recorded **449 passing and 3 failing checks, 452 total**, including **118/118 passing selection/input/drag checks**. The failed native checks were `tray-created`, `root-capture-acquired`, and `capture-survives-hidden-source-group`. The run then aborted during the existing configuration persistence stage with `UnauthorizedAccessException` (exit 1; report `completed=false`). Full desktop validation is therefore **not claimed**. See `artifacts/validation/smoke-result.json` and `quicklaunch-20261014-191355-41524.jsonl`.
- Existing layout, insertion, icon-size, icon picker, toast, window drag/resize, visibility, and prewarm checks reached in that run passed. The deterministic fixture exercises fresh activating/nonactivating shows, all eight first navigation keys, background/padding clicks, click-launch once, Alt down/up plus the hook mask, Enter repeat, real modal F2 editing, Delete/undo, keyboard/mouse group changes, focusable-tree restrictions, page scrolling, drag/reorder/cancel/tab/+ and read-only launch. It replaces only OS pointer-position/capture boundaries, never focus flags or selection state.
- `git diff --check` passed. The index remains unchanged.

[Dark selected-tile snapshot](../../artifacts/validation/tile-selected.png) was rendered from the actual panel and visually inspected: one selected tile beside two unselected tiles.
