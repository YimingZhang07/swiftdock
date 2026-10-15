# Task 8 — launcher keyboard cues and menu separators

> Historical report: the in-process smoke suite and its commands were retired in Task 19. Use the three tiers and coverage mapping in [test-consolidation.md](test-consolidation.md) for current validation.

Changes build on the previously staged tasks. The git index was not written. No packages or standalone probe programs were added.

## Behavior

- The launcher owns a `KeyboardCues` dependency property, reset by every `ShowPanel` and every preview mouse-button press, including handled events.
- Arrow keys, Tab/Shift+Tab, Home/End, PageUp/PageDown and Ctrl group shortcuts enable cues. Modifier-only keys and Alt+Space do not.
- With cues hidden, Enter reveals the focused tile's ring without calling `_startLaunch`; the next Enter press launches. Enter autorepeat does not launch. Delete/F2 consume the key without acting while cues are hidden. Escape keeps its existing behavior.
- Tile, tab, icon-button and toast-link templates draw a 2-DIP AccentBrush ring with radius 6 only when keyboard-focused and cues are enabled. Their `FocusVisualStyle` is null, so Windows UI-state changes cannot activate WPF focus adorners. Dialog styles are unchanged.
- The application resource at `MenuItem.SeparatorStyleKey` derives from the retained implicit Separator style: a stretched 1-DIP BorderBrush line, 8-DIP horizontal margins, and 4-DIP space above and below.

## Files

- `QuickLaunch/Views/LauncherWindow.xaml` — launcher ring templates.
- `QuickLaunch/Views/LauncherWindow.xaml.cs` — cue state and key/mouse handling.
- `QuickLaunch/App.xaml` — menu separator resource.
- `QuickLaunch/Diagnostics/LauncherInputSmoke.cs` — deterministic input/template/menu checks and snapshots.
- `QuickLaunch/Diagnostics/PrototypeSmoke.cs` — runs the new smoke checks.
- `QuickLaunch/Diagnostics/LauncherAppearanceSmoke.cs` — updates the existing focus-style expectation.
- `QuickLaunch.Tests/LauncherInputTests.cs` — 28 navigation and routed-input tests.
- This validation note.

## Deterministic checks

The input tests raise routed events on the tile/control with a controlled KeyboardDevice. They use the existing `_startLaunch` injection and a fixed placement desktop. For visual assertions only, the isolated fixture sets and restores WPF's read-only `IsKeyboardFocused` DP using its internal key. It drains DataBind work before checking the actual template borders. This checks the cue/focus truth table without requesting native keyboard focus or relying on foreground, capture, or real cursor state. No synthetic Win32 input is sent.

The menu smoke opens the actual item ContextMenu programmatically with explicit placement and renders `Popup.Child`. It checks resolved style identity, the 1-DIP line height, 4-DIP vertical spacing, brush, and line width relative to the menu row minus the horizontal margins. It also checks the tray menu and the group menu's resource scope (the current group menu has no separator, so the fixture adds and removes one).

## Validation — 2026-10-13

- `scripts/Validate.ps1`: exit 1. Locked restore succeeds; Release build with `-warnaserror` has **0 warnings / 0 errors**. Tests: **281 passed / 6 failed / 0 skipped / 287 total**. All **28 new tests pass**.
- `scripts/Validate.ps1 -DesktopSmoke`: exit 1 before the smoke phase, with the same six access-denied failures.
- The six existing failures are four `ConfigTests` at `File.Replace` (`SlowWriteAndQueuedRevisionsKeepNewestSnapshot`, `LegacyAutoHideSettingIsRetiredWithoutLosingItems`, `SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`, `FailureKeepsOldFileAndAllowsSameRevisionRetry`), `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance` at named-pipe connection, and `InteractionTests.StartupRegistryRoundtripTouchesOnlyTestKey` at registry access. They match the prior tasks' recorded sandbox restrictions; no checks were skipped or weakened.
- Separate `scripts/Run.ps1 -Smoke`: exit 1, **367 passed / 3 failed / 370 recorded checks**, with all **52 Task 8 checks passing**. Existing failures are `tray-created`, `root-capture-acquired`, and `capture-survives-hidden-source-group`. A later access-denied configuration save prevents completion, so `smoke-result.json` remains `completed: false`.
- `git diff --check`: passes.

Raw results: `artifacts/tests/launcher.trx`, `artifacts/tests/launcher-input.trx`, `artifacts/validation/smoke-result.json`, and the latest `artifacts/validation/quicklaunch-*.jsonl`.

## Snapshots

Saved and visually inspected in the existing ignored artifacts directory:

- `artifacts/validation/item-menu.png` — the actual item ContextMenu popup child, with the full-width separator between 删除 and 刷新图标.
- `artifacts/validation/tile-focus-ring.png` — actual tile template with keyboard cues and the controlled focus DP enabled.

These are WPF RenderTargetBitmap snapshots, not desktop captures. Physical Alt/Alt+Space interaction remains for the reviewer's real-app check.

## Desktop follow-up — container focus

The owner's desktop check found a gap in the original fixture: it supplied tile focus and never exercised focus on the ScrollViewer or group ItemsControl. Those containers could take focus, display the native dotted rectangle after Alt, and scroll instead of selecting a tile.

The launcher window, Viewer, layout roots, group ItemsControls and scrollbar thumbs are now nonfocusable. ScrollViewer/group focus styles are null. `FocusFirst` chooses an enabled remembered/first tile, or the selected tab for an empty group; it never focuses a container. Normal show attempts tile focus even if activation has not yet completed. Drop/prewarm shows retain their no-focus behavior.

Directional/Home/End/Page keys are handled before ScrollViewer key handling. From outside the selected grid the first press focuses the remembered/first tile; subsequent presses navigate tiles, calling `BringIntoView`. Tab/Shift+Tab retain WPF's normal traversal. Empty-space mouse presses hide cues without replacing tile focus. The launcher's actual HWND hook consumes WM_CHANGEUISTATE (0x0127) and WM_UPDATEUISTATE (0x0128); dialog hooks are unaffected.

Follow-up files: `LauncherWindow.xaml`, `LauncherWindow.xaml.cs`, `LauncherWindow.Management.cs`, `Interop/NativeMethods.cs`, `Diagnostics/LauncherInputSmoke.cs`, `QuickLaunch.Tests/LauncherInputTests.cs`, and this note.

The new fixture uses an injected focus-acquisition delegate (production calls `Button.Focus`) to track target changes and drive the focus DP. It exercises the production navigation handler, remembered item identity, template ring, and `BringIntoView`, without requiring native focus. It checks recovery from no focus, Viewer, ItemsControl, GroupHost and a header button for all eight directional/Home/End/Page keys; also the full focusable visual tree, empty-space preview/bubbling mouse routes, empty-group fallback, UI-state hook handling, normal Tab traversal, and real ScrollViewer wheel/offset behavior. The original cue-only fixture no longer calls native focus while checking visual states.

Validation for this follow-up:

- Targeted `LauncherInputTests`: **29 passed / 0 failed**; `-warnaserror` build: **0 warnings / 0 errors**.
- `scripts/Validate.ps1` and `scripts/Validate.ps1 -DesktopSmoke`: exit 1; locked restore and build succeed; **365 passed / 6 failed / 0 skipped / 371 total**. The same six access-denied failures listed above prevent the scripted smoke phase.
- Separate `scripts/Run.ps1 -Smoke`: exit 1; **438 passed / 3 failed / 441 recorded checks**. All **62 new focus-routing checks** and the **52 existing Task 8 checks** pass. Existing tray/capture failures and the later denied config save remain; report completion is false.
- `git diff --check`: passes. Git index SHA-256 stayed `71E97CE9539928B328AED17D9C7140D142FCEF55B008297CAD222372617FB612` throughout this follow-up.
