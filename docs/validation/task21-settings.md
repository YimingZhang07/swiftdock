# Task 21 review record

Settings now uses WPF-UI's left NavigationView in a 760 x 540 DIP window, with
常规 / 外观 / 面板 / 关于 pages sharing one SettingsViewModel. The existing named
controls/AutomationIds are retained. Cards use the existing WPF-UI style with
compact spacing; page titles are 20 DIP SemiBold. About includes the assembly
version, selectable configuration directory and the configured log-directory action.

`backdropOpacity` is clamped to 0–100 and omitted at zero. It changes only the
panel root's theme-colour tint; slider changes paint immediately and use the
existing 500 ms persistence debounce. `keepBackdropWhenInactive` defaults to true
and is written only when false. Both controls are indented below material and
disabled for solid, unsupported material, transparency/high-contrast fallback,
application failure, or read-only state.

The panel-only WM_NCACTIVATE hook calls DefWindowProc with TRUE for an effective
acrylic/mica material. It permits real deactivation to complete, leaves
WM_ACTIVATE/focus processing alone, and excludes hidden/prewarming windows.
Changing the choice while Settings owns foreground refreshes non-client painting.
Dialogs do not receive the hook or overlay tint.

## Verification performed

- `scripts/Validate.ps1`: Release build succeeds with zero warnings; 483 tests,
  476 passed and 7 failed. All failures throw UnauthorizedAccessException in
  this restricted environment: one named-pipe connection and six atomic
  `File.Replace` operations. Retrying with TEMP/TMP inside the workspace produced
  the same access-denied results. Product persistence was not changed to bypass
  the environment restriction.
- The new tests pass for normalization/default omission, overlay alpha, message
  decisions, VM behaviour, page fit (including a long configuration path), and
  real NavigationView selection with a shared VM, without showing windows.
  The new disk save/reset test is one of the seven access-denied failures.
- E2E and snapshot projects: locked restore and Release builds with
  `-warnaserror` succeed, zero warnings/errors.
- `git diff --check` succeeds; no staged changes. No packages added.
- Source comparison confirms no original SettingsWindow named controls were lost.

The failed tests are:

- `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance`
- `ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot`
- `ConfigTests.LegacyAutoHideSettingIsRetiredWithoutLosingItems`
- `ConfigTests.SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`
- `ConfigTests.FailureKeepsOldFileAndAllowsSameRevisionRetry`
- `AppearanceTests.LivePreferencesPersistResetAndRespectReadOnly`
- `BackdropSettingsTests.LiveChangesClampPersistResetAndRespectReadOnly`

## Desktop verification outstanding

Per the task, E2E and snapshots were built but not run. Actual inactive DWM
retention, real input/focus/hotkeys, rendered page appearance, the eight new
settings PNGs, and completion of the 39-image matrix within 90 seconds remain
unverified. In particular, this record does **not** establish that
WM_NCACTIVATE retains DWMSBT_TRANSIENTWINDOW on this OS.

The new acrylic/mica E2E cases fail if ON produces the same solid fallback as
OFF. They also test real typing into a test-owned EDIT window, Esc, hotkey
activation from an inactive panel, hide/show and silent prewarm. If acrylic
fails that acceptance check, the next option to evaluate is a panel-only
compositor-hosted acrylic brush with explicit activation state; mica needs a
separate material decision. See [E2E details](../E2E.md).

## Files changed

| Area | Files |
| --- | --- |
| Settings UI | `QuickLaunch/Views/SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, new `SettingsGeneralPage.xaml`, `SettingsAppearancePage.xaml`, `SettingsPanelPage.xaml`, `SettingsAboutPage.xaml`, `SettingsPages.cs`, `SettingsResources.xaml` (all under the same Views directory) |
| Panel/native | `QuickLaunch/Views/LauncherWindow.xaml.cs`, `QuickLaunch/Interop/NativeMethods.cs` |
| Configuration/VM | `QuickLaunch/Models/AppConfig.cs`, `QuickLaunch/ViewModels/MainViewModel.cs`, `SettingsViewModel.cs`, `QuickLaunch/Services/ConfigValidation.cs`, `DialogDependencies.cs` |
| Appearance/actions | `QuickLaunch/Services/AppearanceOptions.cs`, `ThemeService.cs`, `ApplicationServices.cs` |
| Unit tests | new `QuickLaunch.Tests/BackdropSettingsTests.cs`, `QuickLaunch.Tests/SettingsViewModelTests.cs` |
| E2E | `QuickLaunch.E2E/HotkeyInputWindow.cs`, `LauncherSession.Appearance.cs`, `LauncherTests.cs` |
| Snapshots | `QuickLaunch.Snapshots/ImageRenderer.cs`, `SnapshotRunner.cs`, `scripts/Snapshots.ps1` |
| Documentation | `docs/E2E.md`, this record |

## Reviewer follow-up

The reviewer confirmed the original 483 unit tests pass on the real desktop.
This follow-up adds isolated 60-second scenario execution, a 300-second script
watchdog with orphan cleanup, OFF/ON focus controls and actual WPF activation
logging, and resilient snapshot progress writes. It changes the E2E session,
input/appearance/native fixture helpers, adds `LauncherSession.Supervision.cs`,
updates `scripts/E2E.ps1`, adds `QuickLaunch.Snapshots/ManifestWriter.cs` and three
unit tests, and adds activation diagnostics in `LogEvents.cs`/`LauncherWindow`.
The product's WM_NCACTIVATE behaviour itself is unchanged pending the decisive
OFF/ON desktop result. No ad hoc probe program was created or run.

Local follow-up validation: 486 tests, 479 passed and the same seven sandbox
UnauthorizedAccessException failures listed above. All three new manifest tests
pass. E2E and snapshots build with zero warnings/errors; PowerShell parsing and
`git diff --check` pass. The reviewer must run the desktop cases to validate the
hard deadlines/cleanup, OFF/ON focus and pixel results, and snapshot completion.

## Settings visual review follow-up

The reviewer subsequently confirmed E2E 15/15 in 115 seconds with no leftovers,
working OFF/ON focus and backdrop pixels, and 39 snapshots in 42 seconds with no
on-screen windows. The latest changes address the PNG findings: a dynamic
page-level foreground, restored keyboard icon/title, an untruncated hotkey
selector, distinct exit/log icons, an aligned path-only configuration card, and
the hidden unused navigation footer separator.

Dark settings snapshots now check visible TextBlock contrast and record failures
in the manifest with a nonzero exit. Page text uses the dark page background;
template labels/checkmarks use their painted control fills to avoid false failures
for intentionally black-on-accent text. Unit checks cover live dark/light/dark
updates, rejection of an injected black title, and every supported main-key label.

Latest validation: 488 tests, 481 passed, with the same seven sandbox access-denied
failures. E2E and snapshot Release builds have zero warnings/errors. The updated
PNG appearance and desktop scenarios await the reviewer's rerun.
