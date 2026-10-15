# Task 17: Gong drag/drop migration

> Historical report: the in-process smoke suite and its commands were retired in Task 19. Use the three tiers and coverage mapping in [test-consolidation.md](test-consolidation.md) for current validation.

Status: Release application/E2E compilation succeeds and all 15 Gong/launcher-input cases pass. The complete unit suite has 420 passed, 5 failed, 0 skipped, 425 total; the failures are the existing sandbox access-denied cases listed below. Desktop smoke and the 14 real-input FlaUI scenarios remain for the reviewer.

The reviewer supplied `gong-wpf-dragdrop` 4.0.0 in the project, lock files, and `.tools/packages`. No additional package or version changes were made by this task.

Implementation:

- `LauncherViewModel` implements Gong `IDragSource` / `IDropTarget`; a small separate target routes the plus button. Internal moves use `IDropInfo.InsertIndex`, retaining remove-then-insert semantics in the model. Group tab drops append, plus drops create a group, and tabs reorder through an ItemsControl.
- A 400 ms DispatcherTimer previews another group. Leaving, dropping, cancellation, and shutdown stop it. Cancellation restores the original group without persisting the preview. OLE owns capture, the drag threshold, and cancellation; the view forwards QueryContinueDrag and clears click state when dragging starts.
- External data is captured synchronously through DropParser and passed to the existing asynchronous import pipeline. A thin Gong DropInfo subclass preserves allowed OLE effects without replacing Gong's geometry. Pending items, cancellation, duplicates, and status remain in the existing model pipeline.
- Gong insertion/highlight adorners use the WPF-UI accent pen at 3 DIP. Drag templates show icon/name with WPF-UI tokens; reduced-motion disables the tile enlargement. Custom ghost, insertion marker, geometry, preview transforms, capture, and scroll timers are removed.
- Smokes construct real Gong DragInfo/DropInfo with controlled WPF event coordinates. Handler tests use IDragInfo/IDropInfo doubles, covering insertion positions, tab/plus/group branches, hover/cancel, read-only/pending/invalid sources, and external import/cancel/duplicates.
- LauncherSession treats unsupported/unavailable visibility properties and element-not-available COM errors as invisible, refreshes the panel for visibility polling/foreground acquisition, and retries within Eventually. Foreground acquisition retries for up to three seconds; the 48 px cross-row reorder adds one real-input scenario.

Tab click follow-up:

The migration replaced the tab's ICommand binding with a Click event handler. `DialogSmoke.Click` invokes the bound command, so the old mouse-tab check no longer reached group selection. Restoring the binding routes the button through `SelectGroup`, which clears selection and focuses the selected group's TileListBox. `SelectGroupCommand` keeps the tab enabled during dragging; the action retains its interaction/membership guard, so clicking cannot switch groups while OLE owns input. The redundant view Click handler is removed.

The existing `selection-mouse-tab-clears-and-focuses-list` check is unchanged and now passes. A new regression test invokes the actual ButtonAutomationPeer, checking selection/focus restoration, enabled Gong surfaces during a drag, ignored selection during a drag, and selection after cancellation. The existing group-reorder smoke also passes.

Large icon smoke and foreground follow-up:

The failing sub-condition was `DragDropSmoke.Insertion`, specifically its early return when `At` finds no drop target. The `aligned` first-row index checks passed at every size. Reproduction with the smoke's 292 × 168 DIP saved window measured a 254 × 100 DIP viewport:

| Icon size | Cell size | Actual WrapPanel width | Columns | Next-row (8,8) point in viewport | Result before correction |
| --- | --- | --- | --- | --- | --- |
| 32 | 84 | 252 | 3 | (10,94) | Visible; insertion check passed |
| 40 | 92 | 184 | 2 | (10,102) | Clipped below viewport; no drop target |
| 48 | 100 | 200 | 2 | (10,110) | Clipped below viewport; no drop target |

Both column formulas produce the same count in this fixture. The failure was an offscreen test coordinate, not an incorrect Gong index or half-tile rule. The smoke now derives columns from `WrapPanel.ActualWidth / ItemWidth`, as keyboard navigation does, then calls BringIntoView on the next-row tile before hit testing. It retains the insertion index, Insert adorner, horizontal orientation, 3-DIP accent pen, and ScrollViewer checks. No product drop handler or panel geometry changed for this correction.

Three new WPF cases reproduce the clipping at 32/40/48 px, verify both halves of the visible first-row tiles, verify the next-row insertion after scrolling, and commit an internal cross-row reorder through real Gong DragInfo/DropInfo. The coordinate evidence permits one physical pixel of layout rounding at fractional DPI.

`Large_icon_reorder_crosses_row_boundary_and_is_persisted` seeds 48 px icons and a 340-DIP-wide window, asserts that Delta is fully visible on the next row, drags Alpha to Delta's right half using real mouse input, and verifies Bravo/Charlie/Delta/Alpha in both UIA geometry and JSON after --exit. The suite now contains 14 desktop scenarios.

`LauncherSession.Foreground` refreshes the target, retries SetForeground/Focus at 200 ms intervals for up to three seconds, and uses a single standalone Alt down/up followed by SetForegroundWindow if activation remains denied. It skips Alt while any key or mouse button is held and always releases it in finally. The documented [Alt foreground-lock behavior](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-locksetforegroundwindow) does not replace the final HWND check: scenario keys/text are still refused unless the intended window is verified foreground.

Startup selection and input isolation follow-up:

The retained Header_drag_moves_and_persists_position screenshot shows Alpha (index 0) selected. Its startup log records normal foreground/focus acquisition and no saved configuration revision. The old harness recorded no input history, so the exact event responsible cannot be reconstructed. Cross-test queued input is a plausible cause, not a proven identification of the triggering key.

A concrete isolation gap existed: each gesture sent releases in finally, but there was no shared ownership ledger, no verification that those releases had been consumed, and no input-drain barrier before closing one instance and opening the next. Startup checked only modifiers and mouse buttons, omitting navigation keys and Enter. Returning from a SendInput wrapper does not establish that the target has processed the input.

The harness now tracks all pressed keys/buttons, records ownership before each call, and removes held ownership only after a successful release call. It releases every used key/button before --exit and before the next instance starts, retries still-owned releases on failure cleanup, allows queued input to advance, waits for a live app to become input-idle, then requires all virtual keys/buttons up and an unchanged Windows last-input timestamp for 150 ms. Boundaries also run after cursor restoration and startup foreground acquisition. Cleanup failures still permit the old app to shut down and are reported as failures. No app selection is cleared and the startup selection timeout remains 3000 ms.

A shared 128-entry history records UTC time, scenario/profile identity, foreground HWND, keyboard/button down/up calls, cursor moves, foreground-unlock Alt, and boundary completion. A selection failure now identifies selected tile indices/names, includes the last successful UIA snapshot and current down-key state, and dumps the history across the scenario boundary into the exception/TRX and failure artifact.

No product-only selection path was reproduced. The new ShowAndFocusWithoutNavigationNeverSelectATile regression exercises 30 show/hide cycles, alternating ordinary/drop shows and groups, with deliberately populated prior selection, focus transfer, a routed focus event, collection-current-item changes, and delayed dispatcher work. Three local runs of all six launcher-input cases passed (18/18 cases, 90 cycles), plus the initial targeted run and the complete unit run. Product selection behavior was not changed speculatively; repeat desktop runs remain necessary to attribute any recurrence using the new trace.

Validation performed:

| Command/check | Result |
| --- | --- |
| Initial launcher-input run with the new focus regression | Exit 0; 6 passed, 0 failed |
| Three runs of `.\scripts\dotnet.ps1 test QuickLaunch.Tests/QuickLaunch.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~LauncherInputTests' --logger 'trx;LogFileName=task17-input-loop-<iteration>.trx' --results-directory artifacts/tests` | Exit 0 each; 6/6, 6/6, 6/6; 18 total passes; TRX files task17-input-loop-1/2/3.trx |
| `.\scripts\Validate.ps1` | Exit 1; locked restore passed; Release solution build passed with 0 warnings / 0 errors; unit tests 420 passed, 5 failed, 0 skipped, 425 total; 50 seconds |
| Gong and launcher-input cases within the full suite | 15 passed, 0 failed |
| `.\scripts\dotnet.ps1 build QuickLaunch.E2E/QuickLaunch.E2E.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'` | Exit 0; application and E2E built with 0 warnings / 0 errors; 0.56 seconds |
| Input call-site audit | All explicit keyboard down/up, mouse button down/up and cursor changes use the shared tracking wrappers |
| `git diff --check` | Exit 0; no whitespace errors |
| Desktop smoke / 14 real-input FlaUI scenarios after input isolation changes | Not run in the sandbox; reviewer reruns these externally |

The five full-suite failures are unchanged from [task 16b](launcher-mvvm.md), all `UnauthorizedAccessException`:

- `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance`: `NamedPipeClientStream.TryConnect`.
- `ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot`: `FileSystem.ReplaceFile`.
- `ConfigTests.LegacyAutoHideSettingIsRetiredWithoutLosingItems`: `FileSystem.ReplaceFile`.
- `ConfigTests.SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`: `FileSystem.ReplaceFile`.
- `ConfigTests.FailureKeepsOldFileAndAllowsSameRevisionRetry`: `FileSystem.ReplaceFile`.

Neither these tests nor their production code was changed. Latest full-suite results are in `artifacts/tests/launcher.trx`.

Index preservation: SHA-256 before and after the migration and follow-ups is `2FA72EBFA359FCCE8C9E39729F213DCD9AE6F652B1637F069DBC38985D811A4A`. No index-mutating command was used. The reviewer owns the package-file changes.

Deletion counts against the pre-existing staged baseline: DragCoordinator.cs **168 lines**, ReorderMath.cs **21 lines**, both deleted entirely. Old drag/view plumbing also accounts for **125 removed lines** in LauncherWindow.xaml.cs, **135 removed lines** in LauncherWindow.Management.cs, and **11 removed XAML lines** (gross removed lines in those file diffs).

File inventory (this report is additional documentation; counts below exclude this report):

| File (relative to repository) | Added | Removed |
| --- | ---: | ---: |
| QuickLaunch.E2E/Desktop.cs | 10 | 0 |
| QuickLaunch.E2E/LauncherSession.cs | 134 | 47 |
| QuickLaunch.E2E/LauncherTests.cs | 20 | 0 |
| QuickLaunch.Tests/LauncherInputTests.cs | 130 | 0 |
| QuickLaunch.Tests/LauncherViewModelTests.cs | 1 | 1 |
| QuickLaunch.Tests/PrototypeTests.cs | 0 | 26 |
| QuickLaunch/Diagnostics/LauncherAppearanceSmoke.cs | 17 | 15 |
| QuickLaunch/Diagnostics/LauncherInputSmoke.cs | 61 | 63 |
| QuickLaunch/Diagnostics/WindowDragSmoke.cs | 6 | 21 |
| QuickLaunch/Diagnostics/WindowLayoutSmoke.cs | 8 | 23 |
| QuickLaunch/DragDrop/DragCoordinator.cs | 0 | 168 |
| QuickLaunch/DragDrop/ReorderMath.cs | 0 | 21 |
| QuickLaunch/Services/DialogService.cs | 4 | 4 |
| QuickLaunch/ViewModels/LauncherViewModel.cs | 6 | 3 |
| QuickLaunch/ViewModels/MainViewModel.cs | 1 | 1 |
| QuickLaunch/Views/LauncherWindow.Management.cs | 24 | 135 |
| QuickLaunch/Views/LauncherWindow.xaml | 61 | 11 |
| QuickLaunch/Views/LauncherWindow.xaml.cs | 13 | 125 |
| QuickLaunch/Views/TileListBox.cs | 19 | 0 |
| QuickLaunch.E2E/LauncherSession.Input.cs (new) | 123 | 0 |
| QuickLaunch.Tests/LauncherDropTests.cs (new) | 217 | 0 |
| QuickLaunch/Diagnostics/DragDropSmoke.cs (new) | 62 | 0 |
| QuickLaunch/DragDrop/LauncherDropInfoBuilder.cs (new) | 18 | 0 |
| QuickLaunch/ViewModels/LauncherViewModel.DragDrop.cs (new) | 186 | 0 |
| QuickLaunch/Views/GroupSelectionConverter.cs (new) | 12 | 0 |

Total task source/test diff: 1133 added lines, 664 removed lines. New files are included; the earlier staged work, reviewer package changes, and documentation are excluded. Documentation changes: docs/validation/gong-dragdrop.md and docs/E2E.md.
