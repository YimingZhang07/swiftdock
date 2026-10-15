# Fluent dialogs — task 12

> Historical report: the in-process smoke suite and its commands were retired in Task 19. Use the three tiers and coverage mapping in [test-consolidation.md](test-consolidation.md) for current validation.

Validated on 2026-10-14, branch `ui/round2`, against the staged task 11 implementation. All task 12 changes are in the working tree. No packages or lock files changed. The git index SHA-256 before and after is `1FFA1D5EF4DF4FF7FC0B2F78A61C53445F9735145B93FFC2C355617328BA79BE`.

SettingsWindow, EditItemDialog and NameDialog now inherit WPF-UI FluentWindow, request Mica and extend content into a WPF-UI TitleBar. Title bars explicitly have no icon, no minimize/maximize buttons, and retain Close. The dialogs remain non-resizable, centered over their launcher owner and absent from the taskbar. Mica fallback belongs to WPF-UI; the launcher's existing Acrylic policy is unchanged.

Settings uses six CardControl rows with SymbolIcon, header, one-line description and a right-aligned control. Registration/status text is the hotkey card's description; its full text is also available as a tooltip. Small/medium/large and placement segments use library surface, stroke and accent tokens. Website icons and startup use ToggleSwitch with the existing Click handlers and live application behavior. The directory remains selectable and the footer retains critical-colored Exit and Close.

Editors use WPF-UI TextBox placeholders and primary buttons, and CardExpander for advanced options. Custom type cards and the icon picker keep their templates and library tokens. Every prior XAML name is retained (20 in Settings, 33 in EditItemDialog, 2 in NameDialog, including template names). `UrlHint` remains the named source of the URL placeholder text; the library TextBox renders the placeholder instead of a hand-drawn overlay. NameDialog overrides FluentWindow's default minimum dimensions to retain its compact form.

DeleteGroupAsync awaits WPF-UI MessageBox.ShowDialogAsync. The primary action is “删除” with Danger appearance; Close is “取消”. `_editor` and the existing owned-window count cover the full modal lifetime and are released in `finally`, so Toggle/OpenSettings still recognize the confirmation as an owned editor. Empty groups bypass confirmation, and a closed/exiting launcher cannot apply a delayed deletion.

Removed: `Views/DialogTheme.cs` (41 lines), its constructor arguments/subscriptions/calls, dialog icon/frame constants and unused GetWindowLongPtr/SetWindowLongPtr imports (7 interop lines), the system delete-group MessageBox call, the old settings grids/checkbox rows, and the drawn URL-hint overlay. No replacement native title-bar code was added.

StartupService has an internal read/write storage seam for deterministic testing. Its public constructor still uses the same HKCU Run key operations; executable/runtime validation, profile-specific value name, quoted command and error behavior are unchanged. Tests exercise the real service through in-memory storage, including read/write failures, without changing login startup on the machine. The existing registry round-trip unit test now uses that seam too.

New DialogTests run the same dialog appearance, behavior and editor/icon-picker smokes on the shared STA dispatcher. WindowSmoke.Pump drains dispatcher work after window open/close; PopupSmokeScope handles popup lifetime. Assertions use control state and routed input rather than foreground, capture, successful native popup creation or keyboard-focus ownership. The icon-picker request is observed before native popup handling. Theme snapshots still settle the library's popup animations.

Validation:

- Release build with `-warnaserror`: 0 warnings, 0 errors; locked restore passed.
- `scripts/Validate.ps1`: exit 1, 357 tests, 352 passed, 5 failed, 0 skipped.
- Final `scripts/Validate.ps1 -DesktopSmoke`: exit 1 at the test stage, same 357 / 352 / 5 / 0 result (`artifacts/tests/launcher.trx`).
- All three new DialogTests pass, covering FluentWindow/Mica/title bars/names, dark/light layout, toggles, startup failures, name validation/accept/cancel, delete confirm/cancel, ownership/Toggle, type cards, chips, advanced options, URL inference, saves and icon picking. The updated startup registration test also passes.
- The five failures are existing sandbox access restrictions: ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot, LegacyAutoHideSettingIsRetiredWithoutLosingItems, SnapshotRoundtripCreatesBackupAndOmitsVolatileFields and FailureKeepsOldFileAndAllowsSameRevisionRetry fail at File.Replace; HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance fails connecting to its named pipe. These are not dialog assertions.
- `scripts/Run.ps1 -Smoke`: exit 1, 516 checks reached, 513 passed and 3 failed (`tray-created`, `root-capture-acquired`, `capture-survives-hidden-source-group`), reflecting unavailable native tray/capture in the sandbox. All reached dialog checks passed. The run subsequently stopped at the main-profile reorder/save flush with access denied; `smoke-result.json` correctly reports `completed: false`. No unrestricted desktop success is claimed.
- `git diff --check`: passed. All ten required dark/light dialog snapshots were generated and visually inspected.

Snapshots in `artifacts/validation/`: `settings.png`, `settings-light.png`, `edit-dialog.png`, `edit-dialog-light.png`, `add-folder.png`, `add-folder-light.png`, `name-dialog.png`, `name-dialog-light.png`, `delete-group.png`, `delete-group-light.png`. These are WPF visual snapshots including the library title bars, composited over a solid theme background; they do not prove native DWM Mica rendering. Physical material appearance and unrestricted desktop integration remain for the owner's manual test.

Files changed relative to the index:

| Area | Files |
| --- | --- |
| Application | `QuickLaunch/App.xaml`, `App.xaml.cs` |
| Dialogs | `Views/SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `EditItemDialog.xaml`, `EditItemDialog.xaml.cs`, `NameDialog.xaml`, `NameDialog.xaml.cs`, `LauncherWindow.Management.cs`; deleted `DialogTheme.cs` |
| Services / interop | `Services/StartupService.cs`, `Interop/NativeMethods.cs` |
| Diagnostics | `Diagnostics/DialogAppearanceSmoke.cs`, `EditItemDialogSmoke.cs`, `FaviconSmoke.cs`, `IconPickerSmoke.cs`, `PrototypeSmoke.cs`, `WindowPositionSmoke.cs`; new `DialogBehaviorSmoke.cs`, `WindowSmoke.cs` |
| Tests | new `QuickLaunch.Tests/DialogTests.cs`; `InteractionTests.cs` |
| Documentation | `README.md`, `docs/QUICKSTART.zh-CN.md`, this report |
