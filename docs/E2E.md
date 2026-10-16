# Real desktop regression tests

Run from an unlocked Windows desktop with the repository SDK installed:

```powershell
.\scripts\E2E.ps1
```

The script restores the existing lock file, builds Release with `-warnaserror`, then runs only `QuickLaunch.E2E`. Results go to `artifacts/e2e/e2e.trx`. `Validate.ps1` restores/builds only the app + unit project and runs the 453 fast unit cases (target ≤40 s end to end). `Snapshots.ps1` independently builds a small rendering tool and emits 33 review PNGs plus `artifacts/validation/snapshots.json`; it has no behavior assertions. No new packages are needed. The embedded smoke suite and its switches have been removed; see the [exhaustive coverage mapping](validation/test-consolidation.md).

The suite sends real keyboard and mouse input. Leave the desktop unlocked and do not use the keyboard or mouse during the run. Tests run in one nonparallel collection, with a fresh app/profile per scenario; the reviewer measured 11/11 passing in 125 seconds and accepted the additional real coverage moved from smokes. The earlier 75-second target is retired; no scenarios were cut. Background services, a locked/disconnected desktop, and runs without explicit opt-in report **Skipped**, not Passed. For direct `scripts/dotnet.ps1 test QuickLaunch.E2E -c Release`, set `QUICKLAUNCH_E2E=1`; `E2E.ps1` sets and restores that environment variable automatically. Do not opt in from a sandbox.

The harness resolves `QuickLaunch/bin/Release/net10.0-windows10.0.19041.0/win-x64/QuickLaunch.exe` relative to `QuickLaunch.slnx`, and reports a missing Release build clearly. It launches the apphost, preserving the app's manifest. The consolidated keyboard case starts with `--silent`, waits for the ordinary `warmup.completed` log event, verifies the panel is hidden, then starts a real secondary process and checks forwarding/exit before real input begins. It also activates a test-owned native window while the panel is visible to check deactivation behavior. Other cases start visibly. Each profile has two groups, five local folders with glyph icons, remembered panel geometry, and Ctrl+Alt+Shift+F11. F11 avoids [Windows' reservation of F12 for debuggers](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey). Favicons are off, except in the settings scenario, which starts enabled so it can test switching them off; that profile contains no URLs. The add-URL test stays offline. A primary work area below 1000 × 840 DIPs is skipped so the settings dialog and drag destinations fit.

`--e2e-launch-log <file>` is a test-only alternative to actual tile/Enter/context-menu launches. Unlike `--smoke-test`, it runs the ordinary interactive app. It requires an explicit isolated `--config-dir` and appends one JSON object per accepted launch, for example:

```json
{"id":"00000000000000000000000000000012","name":"Bravo","elevated":false}
```

Without this switch, the production launch delegate is unchanged. The harness checks the log immediately and again after shutdown, so late duplicate launches fail too; nonlaunch scenarios expect an empty log. Config, icon cache, diagnostics, and the launch log are isolated under a fresh `%TEMP%/QuickLaunch.E2E/<guid>` directory. The owner's running instance is neither addressed nor stopped.

Controls are located through UIA using their existing `x:Name` AutomationIds or accessible names, and tile selection is read through `SelectionItemPattern`. Keyboard and mouse scenarios use real input; text entry uses UIA `ValuePattern.SetValue` on the real text control, including its `TextChanged` handling, to avoid IME composition. Invoke/Selection setters and app-private hooks are not used. The mouse guard still refuses to click a point covered by another process, including an IME candidate window. Geometry assertions use physical UIA pixels; saved width/height are compared in DIPs using the window's DPI. Foreground acquisition retries for up to three seconds using FlaUI SetForeground/Focus. If activation is still denied, it sends one standalone Alt down/up before SetForegroundWindow, as described by [Windows foreground-lock documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-locksetforegroundwindow); this fallback is skipped while any key or mouse button is held. Scenario input remains blocked until GetForegroundWindow matches the intended HWND, and a timeout still refuses to type into another app. Owned app dialogs are explicitly activated and verified before entering text. Held keys/buttons are released in `finally`, and mouse scenarios restore the original cursor position even on failure.

Every harness key/button press is tracked before the input call, and every release is recorded. Before --exit and before the next instance starts, the harness releases all keys/buttons it has used, including navigation keys and Enter. Failure cleanup still attempts every release and still shuts down the old instance. At each instance boundary it lets input advance, waits for a live app to become input-idle, and requires all virtual keys/buttons up plus an unchanged GetLastInputInfo timestamp for 150 ms. A final check also follows cursor restoration and startup foreground acquisition. These checks do not clear app selection or relax the startup assertion.

A 128-entry input history survives across scenarios. It records UTC timestamps, test/profile identity, foreground HWND, key/button down/up requests (including foreground-unlock Alt), mouse positions, and boundary completion. Selection timeouts identify the selected tile indices/names, include the last UIA selection snapshot and currently down keys/buttons, and dump recent inputs from both the current and preceding scenario. Failure artifacts also include the history; it describes input calls, not proof of delivery to a particular window.

The cross-row reorder scenario seeds 48 px icons in a 340-DIP-wide window. It checks that Alpha and Delta occupy different visible rows, uses the same real mouse drag helper as the other reorder scenario, then verifies Bravo/Charlie/Delta/Alpha visually and in persisted JSON after --exit. The other scenarios retain their existing default 32 px fixture.

One parameterized accent test (two cases) seeds `dark` + `mica` + `purple` and `light` + `acrylic` + `green` before starting the real apphost. They require Windows high contrast to be off; they never change Windows preferences. Before any settings interaction, the harness waits for the panel to be foreground, uses the selected tab's UIA bounds to locate the center of its 14×3-DIP underline, and reads three interior screen pixels. All three must have saturation of at least 20% and hue within 15 degrees of the preset, stable for 350 ms. It repeats the pixel assertion after hiding/showing via the global hotkey, normal `WM_SETTINGCHANGE` / `WM_THEMECHANGED` / `WM_DWMCOLORIZATIONCOLORCHANGED` messages sent to the panel, and real settings clicks that change and restore the theme/material. Config is checked after graceful exit; missing/null `theme`, `backdrop` or `accent` means `system`, `acrylic` or `system` respectively, matching the product's omitted-default format. The green/acrylic case therefore does not call GetProperty on an omitted default. On Windows 11 22H2+, startup/restored panel material and changed/restored settings material also use DWM attribute checks with Windows transparency-off fallback. Successful screen captures are saved as `artifacts/e2e/Accent_preset_{dark_mica_purple|light_acrylic_green}-{phase}.png`; the normal failure screenshot and retained profile still apply. These tests exercise the ordinary host, native activation and appearance-message hook, without app-private hooks.

Every app is shut down using `QuickLaunch.exe --exit --config-dir <its directory>`, then awaited before persisted JSON is read. A timeout is a failure and only then permits last-resort termination, with a diagnostic warning about a possible ghost tray icon. The harness does not use FlaUI's application disposal as a termination shortcut.

Appearance notifications use the explicit Unicode `SendMessageTimeoutW` entry point with pointer-sized HWND/WPARAM/LPARAM, a `uint` message ID and a pointer-sized result. Each of `0x001A`, `0x031A` and `0x0320` is sent only to the test panel, with `wParam = lParam = 0`, `SMTO_ABORTIFHUNG` and a 2000 ms timeout. There are no cross-process string pointers or broadcasts to other applications. `WM_SETTINGCHANGE` is a pointer-bearing system message, for which asynchronous cross-process posting is unsuitable ([PostMessage restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)). Delivery is checked using the nonzero API return, not the window procedure's result, which can validly be zero ([SendMessageTimeout contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)). `SetLastError = true` preserves the error read immediately through `Marshal.GetLastWin32Error`; failures report the message ID, HWND, timeout and numeric Win32 error. An error of zero on failed delivery means no extended error was supplied. All three messages remain mandatory, including on UIPI/access-denied failures; none is silently dropped. The later pixel and settings phases remain unchanged.

The hidden → visible half of the global-hotkey scenario necessarily needs a foreground target other than the hidden panel. The harness creates a temporary inert Win32 window inside the test process, verifies its PID and foreground HWND, and sends only Ctrl+Alt+Shift+F11 there. It closes that window immediately afterward. The visible → hidden half uses the panel. Scenario keys target only the verified panel, its dialogs, or this test-owned window. The standalone Alt activation fallback above is the only input permitted before the target is verified foreground.

Failures save `artifacts/e2e/<test>-<guid>.png`, preferring the app's foreground window and falling back to the target window or panel. The per-test directory also retains `panel.png` and `failure.txt`. If the app has already exited, the cached panel capture is used. A dialog lookup timeout additionally writes `<test>-<guid>.windows.txt` beside the screenshot, listing process UIA windows (including owned/offscreen windows), their names, classes, automation IDs, native handles, and parent chains. Discovery includes native process window handles as well as UIA process roots and descendant windows. If startup or diagnostics fail, the test output records that without replacing the original failure. TRX output includes the retained temp profile/diagnostics path; successful profiles are removed.

Desktop review should check the real input/foreground behavior, native header/edge drag bounds at the reviewer's DPI, context-menu/confirmation UIA names, and that tray icons disappear after the run. These cannot be validated by compiling or by the sandbox's skipped test run.

## Consolidated scenarios

| Cases | Scenario | Behaviors |
| --- | --- | --- |
| 2 | `Accent_preset_survives_startup_and_appearance_changes` | Purple/dark/mica and green/light/acrylic, all five original color phases, config defaults, native backdrop. |
| 1 | `Keyboard_pointer_menu_escape_and_hotkey` | Alt/arrows, empty-area click, menu Escape, two-stage panel Escape, hotkey hide/show, Ctrl+Tab and mouse tab focus. |
| 1 | `Tab_click_switches_both_ways_and_drag_reorders` | Real clicks on the other tab and back verify tile names, cleared selection and list focus; dragging the inactive tab verifies visible/persisted tab order without selecting it. |
| 1 | `Click_and_held_enter_each_launch_once` | Click then held Enter, one record for each, no delayed duplicate. |
| 1 | `Header_drag_moves_and_persists_position` | Native header drag and persisted position. |
| 1 | `Right_edge_resize_persists_size` | Native resize and persisted DIPs. |
| 1 | `Tile_reorder_is_visible_and_persisted` | Default-size real Gong reorder. |
| 1 | `Large_icon_reorder_crosses_row_boundary_and_is_persisted` | 48 px real Gong cross-row reorder. |
| 1 | `Settings_live_preferences_are_persisted` | Website icons off, follow-mouse, and reset of all four saved geometry fields in one settings visit. |
| 1 | `Add_url_with_icon_then_edit_and_undo_delete` | URL host naming, picker arrows/Enter/Escape, context edit, F2 rename, Delete, Ctrl+Z, persisted glyph. |
| 1 | `Delete_group_confirmation_can_cancel_then_delete` | Cancel then accept, persisted surviving group. |

The 16 original cases are retained in these flows; only redundant process/profile startup and shutdown were removed. The preset pair still counts as two cases. Unit tests own decision tables and persistence edge cases; E2E owns input/binding/native integration. The 39-image snapshot matrix is visual evidence for review, not another pass/fail regression suite. Existing historical smoke reports remain historical and their old commands are obsolete.

The snapshot follow-up is independent of this suite: `Snapshots.ps1` uses non-activating offscreen surfaces, six-second per-image worker deadlines and a 90-second overall renderer deadline. It continuously writes rendered/failed/current progress to `snapshots.json`, including partial failure. It never runs modal dialogs or requests human input.

Task 21 adds real settings navigation through `SettingsGeneralNav`, `SettingsAppearanceNav`,
`SettingsPanelNav`, and `SettingsAboutNav`; existing control AutomationIds are retained.
The opacity test drags the real slider, observes a change before mouse-up, and checks both
new settings in JSON after `--exit` while Settings is still open. Solid/fallback disables
both indented backdrop controls.

The acrylic and mica inactive-material cases start silently and wait for offscreen prewarm.
On a supported DWM material they sample the empty panel background over a test-owned white
window. OFF must visibly change from active (maximum RGB-channel distance greater than
three); its DWM fallback need not equal the theme's solid colour. ON must remain within three RGB levels of the active sample and differ from the
fallback by more than three. Samples must settle for 800 ms. An indistinguishable active
sample fails explicitly instead of accepting a no-op. No Windows preferences are changed.
The same case types into a test-owned EDIT control, presses Esc there, then uses the global
hotkey from that inactive state and verifies panel focus, hide/show and panel Esc.

DWM attribute support alone does not prove that `WM_NCACTIVATE` preserves
`DWMSBT_TRANSIENTWINDOW`. These pixel cases are the desktop acceptance check. If acrylic
still becomes solid on the target OS, do not treat the feature as verified; the next option
to evaluate is a panel-only compositor-hosted acrylic brush with explicit activation state
(and a separate implementation decision for mica). The hook does not change `WM_ACTIVATE`,
keyboard focus or WPF `IsActive`; see the
[WM_NCACTIVATE contract](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-ncactivate).

Snapshots replace `settings.png`/`settings-light.png` with `settings-general`,
`settings-appearance`, `settings-panel`, and `settings-about`, each with `.png` and
`-light.png` variants. Existing accent/material images remain, bringing the matrix to 39.
The six-second worker and 90-second batch limits and offscreen rendering policy are unchanged.

## Task 21 desktop follow-up: deadlines and focus control

Each scenario now runs the existing E2E assembly in an isolated `dotnet vstest`
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
finally block stops this run's test hosts, then enumerates QuickLaunch command
lines and sends `--exit` only for config directories canonically below
`%TEMP%/QuickLaunch.E2E/`. PID start times guard against PID reuse. The script also
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
still has the original 90-second overall deadline.

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
