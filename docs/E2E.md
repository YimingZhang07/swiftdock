# Real desktop regression tests

Run from an unlocked Windows desktop with the repository SDK installed:

```powershell
.\scripts\E2E.ps1
.\scripts\E2E.ps1 -Filter Add_url_with_icon
.\scripts\E2E.ps1 -Filter 'Add_url_with_icon,Settings_live_preferences'
```

The script restores the existing lock file, builds Release with `-warnaserror`, then runs only `Swiftdock.E2E`. Results go to `artifacts/e2e/e2e.trx`. `Validate.ps1` restores/builds only the app + unit project and runs the fast unit cases. `Snapshots.ps1` independently builds a small rendering tool and emits 110 review PNGs in Chinese and English plus `artifacts/validation/snapshots.json`, with a text-contrast guard for dark settings pages.

`-Filter` selects scenario method names by a case-insensitive literal substring. Comma-separated terms are ORed, whitespace is trimmed, and all data rows of each matching scenario run. The script prints the selected names before starting. No match fails explicitly; omitting the filter runs the full suite. The per-scenario supervisor, 60-second deadline, overall timeout and cleanup are unchanged. Use the focused run while iterating and run the full suite once before merge.

Task 27 is covered by `Add_url_with_icon_then_edit_and_undo_delete`: real clicks through all four type chips in both Add and Edit check unchanged window height; system and colour choices are saved and checked in JSON; screen pixels on each tile must match the app's PNG rendering of the chosen spec at the tile's physical pixel size. It also checks system preselection on reopening, keyboard navigation in all three grids, rename, delete and undo. Run this scenario on light/dark desktops at 100% and 200% scale for native window review. The WPF-host test covers the full logical layout/DPI matrix without a desktop.

This icon scenario uses a solid backdrop. Before each tile capture it clears selection with Escape when needed and moves the pointer into the panel's empty bottom strip. The comparison reads the selected spec's PNG from this run's isolated icon cache, preserving its alpha mask. It composites over the surface measured around the tile image, compares every pixel with a small antialiasing filter and one global ±1 physical-pixel alignment tolerance, and requires colour/shape correlation plus low error on both ink and transparent regions. The colour check also rejects a match to the preceding system icon. It does not compare differently sized editor screenshots or infer transparency from the preview's corner. Failure diagnostics include the cache filename, pixel size, correlation, ink/clear-space errors and alignment. A shared WPF-host test uses the actual 32/64px renderings in dark/light backgrounds, including half-pixel positioning, and verifies rejection of the URL globe, previous system icon, another Fluent icon and an absent image; it also verifies cache filenames against the app's writer. The scenario restores selection with real keyboard navigation before its F2 check.

The snapshot plan includes `add-dialog` (App), `add-dialog-file`, `add-dialog-folder`, `add-dialog-url`, `icon-picker-system` and `icon-picker-color`, each in dark and `-light` variants. System snapshots reflect the icons installed on the rendering machine; no Windows artwork is bundled. The renderer waits through background image loads and row repacking until the realized page settles. Empty, faint-frame and unusable system resources are removed and remembered for the session at each pixel size; replacement cells fill the viewport. Catalog grids use the simple palette's space while the popup keeps one height. The existing `Add_url_with_icon_then_edit_and_undo_delete` E2E also checks that the first system page has complete rows with visible ink in every cell. `Snapshots.ps1` validates the completed inventory against `snapshots.json.expected` and reports that count, without a separate hard-coded matrix size.

The suite sends real keyboard and mouse input. Leave the desktop unlocked and do not use the keyboard or mouse during the run. Tests run in one nonparallel collection, with a fresh app/profile per scenario. Background services, a locked/disconnected desktop, and runs without explicit opt-in report **Skipped**, not Passed. For direct `scripts/dotnet.ps1 test Swiftdock.E2E -c Release`, set `SWIFTDOCK_E2E=1`; `E2E.ps1` sets and restores that environment variable automatically. Do not opt in from a sandbox.

The harness resolves `Swiftdock/bin/Release/net10.0-windows10.0.19041.0/win-x64/Swiftdock.exe` relative to `Swiftdock.slnx`, and reports a missing Release build clearly. It launches the apphost, preserving the app's manifest. The keyboard case starts with `--silent`, waits for the ordinary `warmup.completed` log event, verifies the panel is hidden, then starts a real secondary process and checks forwarding/exit before real input begins. It also activates a test-owned native window while the panel is visible to check deactivation behavior. Other cases start visibly. Each profile has two groups, five local folders with glyph icons, remembered panel geometry, and Ctrl+Alt+Shift+F11. F11 avoids [Windows' reservation of F12 for debuggers](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey). Favicons are off, except in the settings scenario, which starts enabled so it can test switching them off; that profile contains no URLs. The add-URL test stays offline. A primary work area below 1000 × 840 DIPs is skipped so the settings dialog and drag destinations fit.

`--e2e-launch-log <file>` is a test-only alternative to actual tile/Enter/context-menu launches in the ordinary interactive app. It requires an explicit isolated `--config-dir` and appends one JSON object per accepted launch, for example:

```json
{"id":"00000000000000000000000000000012","name":"Bravo","elevated":false}
```

Without this switch, the production launch delegate is unchanged. The harness checks the log immediately and again after shutdown, so late duplicate launches fail too; nonlaunch scenarios expect an empty log. Config, icon cache, diagnostics, and the launch log are isolated under a fresh `%TEMP%/Swiftdock.E2E/<guid>` directory. The owner's running instance is neither addressed nor stopped.

Controls are located through UIA using their existing `x:Name` AutomationIds or accessible names, and tile selection is read through `SelectionItemPattern`. Keyboard and mouse scenarios use real input; text entry uses UIA `ValuePattern.SetValue` on the real text control, including its `TextChanged` handling, to avoid IME composition. Invoke/Selection setters and app-private hooks are not used. The mouse guard still refuses to click a point covered by another process, including an IME candidate window. Geometry assertions use physical UIA pixels; saved width/height are compared in DIPs using the window's DPI. Foreground acquisition retries for up to three seconds using FlaUI SetForeground/Focus. If activation is still denied, it sends one standalone Alt down/up before SetForegroundWindow, as described by [Windows foreground-lock documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-locksetforegroundwindow); this fallback is skipped while any key or mouse button is held. Scenario input remains blocked until GetForegroundWindow matches the intended HWND, and a timeout still refuses to type into another app. Owned app dialogs are explicitly activated and verified before entering text. Held keys/buttons are released in `finally`, and mouse scenarios restore the original cursor position even on failure.

Every harness key/button press is tracked before the input call, and every release is recorded. Before --exit and before the next instance starts, the harness releases all keys/buttons it has used, including navigation keys and Enter. Failure cleanup still attempts every release and still shuts down the old instance. At each instance boundary it lets input advance, waits for a live app to become input-idle, and requires all virtual keys/buttons up plus an unchanged GetLastInputInfo timestamp for 150 ms. A final check also follows cursor restoration and startup foreground acquisition. These checks do not clear app selection or relax the startup assertion.

A 128-entry input history survives across scenarios. It records UTC timestamps, test/profile identity, foreground HWND, key/button down/up requests (including foreground-unlock Alt), mouse positions, and boundary completion. Selection timeouts identify the selected tile indices/names, include the last UIA selection snapshot and currently down keys/buttons, and dump recent inputs from both the current and preceding scenario. Failure artifacts also include the history; it describes input calls, not proof of delivery to a particular window.

The cross-row reorder scenario seeds 48 px icons in a 340-DIP-wide window. It checks that Alpha and Delta occupy different visible rows, uses the same real mouse drag helper as the other reorder scenario, then verifies Bravo/Charlie/Delta/Alpha visually and in persisted JSON after --exit. The other scenarios retain their existing default 32 px fixture.

One parameterized accent test (two cases) seeds `dark` + `mica` + `purple` and `light` + `acrylic` + `green` before starting the real apphost. They require Windows high contrast to be off; they never change Windows preferences. Before any settings interaction, the harness waits for the panel to be foreground, uses the selected tab's UIA bounds to locate the center of its 14×3-DIP underline, and reads three interior screen pixels. All three must have saturation of at least 20% and hue within 15 degrees of the preset, stable for 350 ms. It repeats the pixel assertion after hiding/showing via the global hotkey, normal `WM_SETTINGCHANGE` / `WM_THEMECHANGED` / `WM_DWMCOLORIZATIONCOLORCHANGED` messages sent to the panel, and real settings clicks that change and restore the theme/material. Config is checked after graceful exit; missing/null `theme`, `backdrop` or `accent` means `system`, `acrylic` or `system` respectively, matching the product's omitted-default format. The green/acrylic case therefore does not call GetProperty on an omitted default. On Windows 11 22H2+, startup/restored panel material and changed/restored settings material also use DWM attribute checks with Windows transparency-off fallback. Successful screen captures are saved as `artifacts/e2e/Accent_preset_{dark_mica_purple|light_acrylic_green}-{phase}.png`; the normal failure screenshot and retained profile still apply. These tests exercise the ordinary host, native activation and appearance-message hook, without app-private hooks.

Every app is shut down using `Swiftdock.exe --exit --config-dir <its directory>`, then awaited before final persisted JSON is read. The pin scenario also polls the atomic config file while running to prove each click is saved. A timeout is a failure and only then permits last-resort termination, with a diagnostic warning about a possible ghost tray icon. The harness does not use FlaUI's application disposal as a termination shortcut.

Appearance notifications use the explicit Unicode `SendMessageTimeoutW` entry point with pointer-sized HWND/WPARAM/LPARAM, a `uint` message ID and a pointer-sized result. Each of `0x001A`, `0x031A` and `0x0320` is sent only to the test panel, with `wParam = lParam = 0`, `SMTO_ABORTIFHUNG` and a 2000 ms timeout. There are no cross-process string pointers or broadcasts to other applications. `WM_SETTINGCHANGE` is a pointer-bearing system message, for which asynchronous cross-process posting is unsuitable ([PostMessage restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)). Delivery is checked using the nonzero API return, not the window procedure's result, which can validly be zero ([SendMessageTimeout contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)). `SetLastError = true` preserves the error read immediately through `Marshal.GetLastWin32Error`; failures report the message ID, HWND, timeout and numeric Win32 error. An error of zero on failed delivery means no extended error was supplied. All three messages remain mandatory, including on UIPI/access-denied failures; none is silently dropped. The later pixel and settings phases remain unchanged.

The hidden → visible half of the global-hotkey scenario necessarily needs a foreground target other than the hidden panel. The harness creates a temporary inert Win32 window inside the test process, verifies its PID and foreground HWND, and sends only Ctrl+Alt+Shift+F11 there. It closes that window immediately afterward. The visible → hidden half uses the panel. Scenario keys target only the verified panel, its dialogs, or this test-owned window. The standalone Alt activation fallback above is the only input permitted before the target is verified foreground.

Failures save `artifacts/e2e/<test>-<guid>.png`, preferring the app's foreground window and falling back to the target window or panel. The per-test directory also retains `panel.png` and `failure.txt`. If the app has already exited, the cached panel capture is used. A dialog lookup timeout additionally writes `<test>-<guid>.windows.txt` beside the screenshot: a native inventory includes hidden/cloaked HWNDs and their classes, while UIA details (names, automation IDs and parent chains) are queried only for visible, uncloaked windows. Native enumeration includes owned dialogs; discovery does not traverse descendant Window providers that may already be tearing down. If startup or diagnostics fail, the test output records that without replacing the original failure. TRX output includes the retained temp profile/diagnostics path; successful profiles are removed.

Every HWND-to-UIA lookup checks `IsWindowVisible` and `DWMWA_CLOAKED` first. Per-HWND
TimeoutException, COMException and ElementNotAvailableException failures write one
`UIA discovery retry/failed` diagnostic with the operation, HWND and cached native class.
Inside `Eventually`, discovery skips that handle and marks the poll incomplete: even a
negative check such as `!HasMenuItem` must retry instead of treating a failed scan as
proof of absence. Outside a retry, required lookups log and rethrow. Tray overflow
discovery uses the same per-HWND policy, while its cover/identity checks remain fail-closed.
The UIA connection and transaction timeouts remain two seconds.

Desktop review should check the real input/foreground behavior, native header/edge drag bounds at the reviewer's DPI, context-menu/confirmation UIA names, and that tray icons disappear after the run. These cannot be validated by compiling or by the sandbox's skipped test run.

## Scenarios

| Cases | Scenario | Behaviors |
| --- | --- | --- |
| 2 | `Accent_preset_survives_startup_and_appearance_changes` | Purple/dark/mica and green/light/acrylic, all five color phases, config defaults, native backdrop. |
| 2 | `Inactive_material_pixels_and_real_focus_survive_prewarm` | Acrylic/mica inactive pixels, real EDIT typing, pin clicks and persistence, native topmost bit, pinned hide retaining focus and unpinned activation. |
| 1 | `Settings_navigation_and_transparency_drag_persist_after_exit` | Four settings pages, live transparency drag snapping to 5%, inverted backdropOpacity persistence, and inactive-material toggle. |
| 1 | `Keyboard_pointer_menu_escape_and_hotkey` | Alt/arrows, empty-area click, menu Escape, two-stage panel Escape, hotkey hide/show, Ctrl+Tab and mouse tab focus. |
| 1 | `Tab_click_switches_both_ways_and_drag_reorders` | Real clicks on the other tab and back verify tile names, cleared selection and list focus; dragging the inactive tab verifies visible/persisted tab order without selecting it. |
| 1 | `Click_and_held_enter_each_launch_once` | Click then held Enter, one record for each, no delayed duplicate. |
| 1 | `Header_drag_moves_and_persists_position` | Native header drag and persisted position. |
| 1 | `Right_edge_resize_persists_size` | Native resize and persisted DIPs. |
| 1 | `Tile_reorder_is_visible_and_persisted` | Default-size real Gong reorder. |
| 1 | `Large_icon_reorder_crosses_row_boundary_and_is_persisted` | Legacy iconSize 48 migrates to scale 120; real Gong cross-row reorder and persisted migration. |
| 1 | `Settings_live_preferences_are_persisted` | Real slider keys to 150% and back to 100%, tile bounds/label height/window size ratios and persistence, unchanged Settings size; website icons off, follow-mouse, reset. |
| 1 | `Add_url_with_icon_then_edit_and_undo_delete` | Stable Add/Edit height through all four types, URL host naming, all three picker grids and keyboard navigation, current-item preselection, persisted system/Fluent specs and tile pixels, F2 rename, Delete and Ctrl+Z. |
| 1 | `Delete_group_confirmation_can_cancel_then_delete` | Cancel then accept, persisted surviving group. |

Unit tests cover decision tables and persistence edge cases; E2E covers input/binding/native integration. The snapshot matrix provides visual evidence for review.

For Task 28 iteration, run `Settings_navigation_and_transparency_drag_persist_after_exit`,
`Settings_live_preferences_are_persisted`, and
`Large_icon_reorder_crosses_row_boundary_and_is_persisted`; run the full suite once before merge.
Scale checks use actual tile and label UIA bounds and persisted window DIPs, with small
layout-rounding tolerances. Input stays in Settings throughout the live resize.
The label lookup walks the tile's raw-view children with FlaUI's `RawViewWalker` and
matches AutomationId `TileLabel`. Template TextBlocks are not control elements, so
control-view descendant searches cannot find this label. Geometry reads do not filter
on `IsOffscreen`, since Settings can cover the panel.
Bounds must be positive and contained within the tile before comparing label height.
The no-HWND WPF layout test verifies the peer's ID, Text type and `IsControlElement=false`;
E2E logs its actual ID, offscreen flag and bounds at each scale.
The snapshot plan adds `launcher-80-dark.png`, `launcher-80-light.png`,
`launcher-150-dark.png`, and `launcher-150-light.png`; existing `settings-appearance`
variants capture the updated transparency and scale controls.

Snapshot rendering is independent of this suite: `Snapshots.ps1` uses non-activating offscreen surfaces, six-second per-image worker deadlines and a 90-second overall renderer deadline. It continuously writes rendered/failed/current progress to `snapshots.json`, including partial failure. It never runs modal dialogs or requests human input.

Settings tests navigate through `SettingsGeneralNav`, `SettingsAppearanceNav`,
`SettingsPanelNav`, and `SettingsAboutNav` using control AutomationIds.
The NavigationView uses `Transition=None` and `TransitionDuration=0`, including its
first page. The existing navigation unit test checks both properties before navigating.
The transparency test drags the real slider, observes a change before mouse-up, checks
5% snapping and inverted `backdropOpacity`, and checks both settings in JSON after
`--exit` while Settings is still open. Solid/fallback disables
both indented backdrop controls.

The keyboard/hotkey scenario ends with an optional real tray-input phase. It searches
the notification area for the unique fixture tooltip `迅启 · Control+Alt+Shift+F11`.
If needed, it snapshots native shell windows, clicks the chevron with real mouse input,
then searches the desktop UIA root's top-level children. It descends only into visible,
uncloaked HWNDs owned by explorer, ShellHost or ShellExperienceHost which appeared or
became visible after that click. Overflow host class names are not assumed. It verifies
click-to-show with tile focus, a later click-to-hide, and double-click-to-hide only once.
Double-click starts with the pinned panel visible so hiding does not dismiss the overflow
between its two real mouse presses. Shell HWND/process checks precede input; no synthetic
tray callbacks or UIA Invoke are used. The system double-click interval supplies the waits.
The same phase then right-clicks the verified icon with the panel visible and pinned,
and again with the panel hidden. It finds the test process's menu through the
`显示迅启` MenuItem. Its UIA screen rectangle must have a corner within 24 physical
pixels of the click, or contain that point when clamped at a screen edge. In both cases
the same popup HWND must remain visible, foreground and at the same native rectangle
for at least 1.5 seconds with no input or focus repair. Disappearance fails immediately;
reopening cannot restart the interval. A real click on `设置` must then open Settings,
which is closed with real input. A third opening must survive the same stability check;
real Down presses must move WPF keyboard focus between menu items, then real Escape
closes it, with no foreground repair. A fourth stable opening is dismissed with one
real click in the client area of the existing test-owned probe fixture. HWND/PID hit
testing must confirm that exact probe before input; its WM_LBUTTONDOWN counter must
advance and it must gain foreground. The harness never clicks the desktop or another
application to dismiss a menu. Once the
right-click is sent, missing, misplaced, self-closing or noninteractive menus fail the case.
A finally block closes any menu/Settings window before flyout cleanup.
Before every overflow-icon click gesture, including after reopening the flyout, its
rectangle must be non-empty and identical for three consecutive samples about 100 ms
apart, with IsOffscreen false and the expected WindowFromPoint root throughout. The
settle deadline is two seconds; expiry skips with the full target diagnostic. Final
verification and the moved-before-mouse-down guard still run after settling. The two
presses within a double-click retain their ordinary timing, with no settle delay between them.
Each rejected target logs one `Tray verification FAILED` line identifying the taskbar
icon, chevron or overflow-icon step, root HWND/class/PID/process, element PID/name/rect/
IsOffscreen, and WindowFromPoint plus GA_ROOT HWND/class/PID/process. The hit-test root
must be the discovered HWND and its PID must equal the element PID. Unusable taskbar
copies are diagnosed before trying overflow; ambiguous matches never receive input.
Discovery failures or ambiguous icons log `SKIP tray input (remaining steps), step=...`
in the worker TRX; this is a partial skip, not proof of tray behavior. Once a tray click
is sent, visibility/focus failures are test failures. A finally block sends real Escape to
the identified flyout (or a newly visible shell host already owning foreground), falls
back to a verified real chevron click, and requires all newly visible shell windows from
that opening to be gone or cloaked. Cleanup failure fails the case. Worker deadlines are unchanged.

Each product menu opening logs one `tray.menu` event: input source, physical `GetCursorPos`,
the raw H.NotifyIcon callback point and protocol version, the selected physical anchor,
overflow classification (unknown for unrecognized ShellHost classes), cursor root class,
final native menu rectangle and menu DPI. H.NotifyIcon 2.4.1's
[mouse path](https://raw.githubusercontent.com/HavenDV/H.NotifyIcon/v2.4.1/src/libs/H.NotifyIcon.Shared/TaskbarIcon.MouseEvents.cs)
scales the callback point and its
[menu path](https://raw.githubusercontent.com/HavenDV/H.NotifyIcon/v2.4.1/src/libs/H.NotifyIcon.Shared/TaskbarIcon.ContextMenu.Wpf.cs)
overwrites AbsolutePoint offsets; it does not use Shell_NotifyIconGetRect there. Its cached
DPI factor comes from a temporary HwndSource, falls back to 1, and refreshes only on the
message window's DpiChanged event. A stale 1 at 192 DPI makes WPF double the coordinates
and clamp the menu to the screen corner. Swiftdock
now opens the same menu at the captured mouse position, using WPF's physical absolute
placement rectangle and built-in edge clamping. Keyboard invocation uses the Shell icon
rectangle instead of the cursor. Desktop review should compare callback/cursor/menu
coordinates and check Shift+F10/Menu-key placement,
outside-click dismissal, menu commands and the autostart checkmark. No old/new desktop
comparison is implied by the unit tests or build.
Opening runs from H.NotifyIcon's `PreviewTrayContextMenuOpen` hook with its original
ordering: set placement, finish setting IsOpen, foreground the popup HWND (message
window only as the library's missing-HWND fallback), then raise TrayContextMenuOpen.
It then calls ContextMenu.Focus() so WPF keyboard focus follows native foreground;
Escape and arrow keys use the built-in menu handling. The open diagnostic also records
IsKeyboardFocusWithin, separately from native foreground/focus/capture.
Neither the hidden message window nor the panel is activated before opening, and the
Opened handler only records diagnostics. Raw callbacks only supply diagnostic points.
The mouse gesture suppresses paired WM_CONTEXTMENU callbacks during button-down and
for the system double-click interval after button-up, even if the popup has closed.
`tray.menu.closed` records the close timestamp, elapsed open milliseconds, popup HWND,
and foreground HWND/class, so a spontaneous close is visible in the product log.

Other application animations remain unchanged: the status line's 150 ms fade, duplicate
tile opacity flash (100 ms, auto-reverse twice), WPF-UI ToggleSwitch thumb/fill transitions,
CheckBox checkmark drawing, the editor's Advanced CardExpander reveal/chevron rotation,
context-menu slide-in, and scrollbar hover expansion/fades. WPF-UI also contains pane and
submenu animations, but the fixed-open flat Settings navigation does not exercise them.
The drag preview has a fixed 1.1 scale rather than a timed transition. Native window-open
and DWM material animations continue to follow Windows preferences.

The acrylic and mica inactive-material cases start silently and wait for offscreen prewarm.
On a supported DWM material they sample the empty panel background over a test-owned white
window. OFF must visibly change from active (maximum RGB-channel distance greater than
three); its DWM fallback need not equal the theme's solid colour. ON must remain within three RGB levels of the active sample and differ from the
fallback by more than three. Samples must settle for 800 ms. An indistinguishable active
sample fails explicitly instead of accepting a no-op. No Windows preferences are changed.
Frame checks exercise owned dialogs: both cancel and delete in the existing group-delete
scenario, plus the first Settings visit in the inactive-material case. They capture the
same screen bands before opening and after the owned HWND closes, before UIA window
rediscovery. The pure `FrameDetector`, shared with unit tests, samples device-pixel
insets 2–7 on all four sides and compares them with insets 18–23, excluding corners.
A frame pixel has all RGB channels at least 210 and luminance at least 50 above its
inner reference. Two sides with at least 20% coverage fail; a post-dialog increase of
more than 10 percentage points on any side also fails. The clean result must persist
for 350 ms within three seconds. Per-side coverage and mean edge/inner luminance are
written to ordinary scenario diagnostics; only the existing failure screenshots remain.

Calibration uses the reviewer's old/fixed desktop PNGs in `Swiftdock.Tests/TestData/Frames`.
Their sampled pixels are unchanged; the unused centers are blanked to keep both fixtures
under 47 KB combined. The old frame scores 25.7%/100%/100%/100% (top/right/bottom/left),
the fixed frame 0% everywhere. Pixels 0–1 look normal in both images, which is why the
former outer-ring check missed this bug. The native-call unit tests still check `lParam=-1`
and deactivation return semantics without an HWND. Material ON/OFF pixel assertions remain.
The same case types into a test-owned EDIT control and presses Esc there. With the pinned
panel still visible, one Ctrl+Alt+Shift+F11 hides it while the fixture retains foreground
and EDIT focus; more real typing must append to the same text without a focus repair.
A real click on `PinButton` turns pinning off, exposes UIA TogglePattern OFF, clears native
`WS_EX_TOPMOST`, and persists `"alwaysOnTop": false`. From the fixture's foreground, one
hotkey then activates the unpinned panel with tile focus and the next hides it. Clicking
the pin back on restores the native bit and removes the JSON key. These pin/hotkey checks
also run when material pixel checks are unavailable. The existing supervised worker and
60-second deadline apply; the scenario ends with panel Esc and show verification.

DWM attribute support alone does not prove that `WM_NCACTIVATE` preserves
`DWMSBT_TRANSIENTWINDOW`. These pixel cases are the desktop acceptance check. If acrylic
becomes solid on the target OS, the feature has failed validation. The hook does not change `WM_ACTIVATE`,
keyboard focus or WPF `IsActive`; see the
[WM_NCACTIVATE contract](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-ncactivate).

Settings snapshots include `settings-general`,
`settings-appearance`, `settings-panel`, and `settings-about`, each with `.png` and
`-light.png` variants, alongside accent/material images in the 51-image matrix.
`launcher-status.png` and `launcher-status-light.png` show the drag hint as plain text in
the reserved bottom strip. Review its spacing, ellipsis and cancel link on the desktop
in both themes with acrylic, mica and solid; offscreen renders do not capture DWM material.
Also check owned settings/editors/confirmations remain above the panel with pin on and off.

## Deadlines and focus control

Each scenario runs the E2E assembly in an isolated `dotnet vstest`
process. The outer xUnit host never initializes UIA and imposes a 60-second
execution deadline. A timeout captures a native HWND/foreground/focus dump and
screen PNGs with an independent two-second diagnostic budget, sends `--exit`
without calling UIA first, then terminates any remaining product/worker as a
reported last resort. Shutdown waits are bounded (four seconds for the exit
client and four for the product; two seconds after a kill). The input ownership
journal lets the supervisor release only its worker's keys/buttons. A timed-out
worker is terminated before another scenario runs; abandoned UIA threads cannot
resume input in the next case. Worker stdout/stderr, TRX, focus phases and timeout
artifacts live in `artifacts/e2e/<scenario>-<guid>/`.

`E2E.ps1 -TimeoutSeconds 300` adds an independent overall execution deadline. Its
finally block stops this run's test hosts, then enumerates Swiftdock command
lines and sends `--exit` only for config directories canonically below
`%TEMP%/Swiftdock.E2E/`. PID start times guard against PID reuse. The script also
uses recorded host identities if process-tree enumeration fails, releases this
run's recorded input, and reports any last-resort kill in `cleanup.log`. Restore
and build precede the timed desktop execution. Direct `dotnet test` still has the
per-scenario deadline; the script supplies the overall deadline and orphan sweep.

Blocking-call audit:

| Operation | Bound |
| --- | --- |
| UIA discovery, properties, patterns, disposal and failure diagnostics | UIA3 connection/transaction timeouts remain 2 seconds; every provider call is contained by the independent 60-second process deadline, including providers that ignore their timeout. Discovery now starts from native process HWNDs, not desktop-wide UIA traversal. |
| Foreground | Native SetForegroundWindow plus the existing standalone-Alt fallback, with a 3-second poll; removed UIA SetFocus/SetForeground helpers. No AttachThreadInput. |
| Input drain | Removed FlaUI Wait.UntilInputIsProcessed; input-idle is 1 second and quiet-state polling is 3 seconds. |
| Other WaitForExit calls | Secondary show 8 seconds; ordinary --exit client 6 seconds, product 8 seconds; post-kill waits 2 seconds. All remain inside scenario isolation. |
| SendMessage | Only SendMessageTimeoutW: appearance notifications 2 seconds; test EDIT WM_GETTEXT/WM_SETTEXT 500 ms. |
| Fixture thread | Startup 3 seconds, posted WM_CLOSE and join 2 seconds; the message loop belongs to the disposable worker process. |
| Supervisor diagnostics/output | Native diagnostic capture 2 seconds, each output drain 1 second. No UIA in teardown. |
| Overall process discovery | CIM operation timeout 3 seconds; recorded PID/start-time fallback. |

The focus fixture is now a registered top-level Win32 window with an EDIT child,
not the predefined STATIC class used as a top-level window. In the same material
case, OFF is the control and ON repeats exactly the same foreground, real mouse
click, native focus verification and Unicode SendInput typing. The fixture detaches
IME contexts from its top-level HWND and existing EDIT child on their owning thread,
before input, and verifies that both contexts are null. Unicode input alone does not
prevent WeType composition. This uses [ImmAssociateContextEx](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immassociatecontextex)
only on the disposable test fixture; the user's IME settings are unchanged. Before typing
in each phase, the fixture clears its EDIT with bounded WM_SETTEXT and asserts an empty
readback, without relying on Ctrl+A support. Every focus/typing failure includes expected
and actual EDIT text (or an explicit read failure). Both phases
verify text with bounded WM_GETTEXT and log GetForegroundWindow/owner PID,
GetGUIThreadInfo active/focus HWNDs for the fixture and panel threads, and a fresh
`window.activation` product log containing the actual WPF `IsActive` value.
The product log runs after message processing; it does not infer IsActive from
the foreground HWND or change activation handling. Both phases run even if the
OFF control fails. OFF-pass/ON-fail is reported as a product regression; both-fail
identifies an unproven harness/foreground path. The inactive pixel checks remain.

Snapshot manifests retry three atomic writes with 25/50 ms delays, then try a
direct write. A failure of both paths is collected without interrupting the
image loop or cleanup. Manifest errors are reported only after rendering, with
the full final inventory on stderr if the final file is still locked. Rendering
has a 90-second overall deadline.

Independent cleanup accepts `--exit` code 2 (no instance) when the tracked process
has already exited, including when the worker already shut it down successfully.
Both the scenario supervisor and script orphan sweep judge shutdown by the tracked
process lifetime. Exit-client timeouts and any required last-resort kill still fail.

Dark settings renders also run a small text-contrast guard after writing the PNG.
It walks the active page's visible TextBlocks and fails the worker below 3:1,
which records the image/error in `snapshots.json` and makes the batch exit nonzero.
Plain page text uses NavigationView's page background composited over the theme
background; template labels/checkmarks use their painted control background so
intentional black-on-accent text is not mistaken for black-on-dark-page text.
Errors include the text, foreground/background colours and computed ratio.

## Language switching

Every scenario seeds `language: "zh-CN"` so its existing Chinese element names are independent of Windows language. `Settings_live_preferences_are_persisted` also switches to English and back with real input, checks navigation, the Language label, panel add name/tooltip, tray menu items and the saved `language`. The language checks require a reachable tray icon; they fail if tray verification cannot send input safely.

Before querying the shell, each language step waits up to four seconds for a fresh `tray.tooltip` event from the test process and checks both WPF/native tooltip values and `shellUpdated`. The harness logs all taskbar and candidate flyout Button names containing the test hotkey suffix, including stale names, and includes them in a final failure. For language checks it allows two three-second name waits with one verified flyout close/reopen. Windows 11 Explorer can concatenate the original and updated tooltips in the overflow icon's UIA Name, so a name must equal the expected-language tooltip or end with a space followed by that complete tooltip. A name containing only the old tooltip or ending with it is rejected. The matching element must still be unique, and the same rule is checked again before input.

For Task 30 desktop review, run `Settings_live_preferences_are_persisted`, `Settings_navigation_and_transparency_drag_persist_after_exit`, and the existing editor/icon-picker and tray scenarios. `scripts/Snapshots.ps1 -TimeoutSeconds 300` renders both languages offscreen; English images have an `-en.png` suffix.
