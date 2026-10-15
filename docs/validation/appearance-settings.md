# Task 18 — appearance settings and panel polish

> Historical report: the in-process smoke suite and its commands were retired in Task 19. Use the three tiers and coverage mapping in [test-consolidation.md](test-consolidation.md) for current validation.

Implemented on `ui/round3`. No packages added. All changes are unstaged; the existing index was preserved.

Settings has live theme, backdrop and accent choices, with optional configuration fields omitted at defaults. The seven accent presets use WPF-UI's accent API to generate light/dark variants. High contrast overrides personal choices and clears previous accent resource overrides. Unsupported materials, disabled transparency and high contrast use solid backgrounds; the description reports the active fallback. Dialogs use Mica unless solid is selected or a system fallback applies. Read-only profiles disable the controls.

The panel has a 48-DIP header, 16-DIP padding, an inset divider, aligned tab text, a 14×3 selected underline and quieter header buttons. Tile columns fill the viewport evenly. Layout, keyboard navigation and drop insertion share `TileGeometry`; cell heights and icon presets stay unchanged. Tile positions round once at the current monitor scale.

Changed files relative to the staged work:

| Area | Files |
| --- | --- |
| Configuration | `QuickLaunch/Models/AppConfig.cs`, `QuickLaunch/Services/ConfigValidation.cs`, `QuickLaunch/Services/AppearanceOptions.cs` (new), `QuickLaunch/ViewModels/MainViewModel.cs` |
| Appearance and composition | `QuickLaunch/Services/ThemeService.cs`, `QuickLaunch/Services/ApplicationServices.cs`, `QuickLaunch/Services/DialogDependencies.cs`, `QuickLaunch/Services/DialogService.cs`, `QuickLaunch/ViewModels/SettingsViewModel.cs`, `QuickLaunch/Views/SettingsWindow.xaml` |
| Panel and input | `QuickLaunch/Views/LauncherWindow.xaml`, `QuickLaunch/Views/LauncherWindow.xaml.cs`, `QuickLaunch/Views/TilePanel.cs` (new), `QuickLaunch/Views/TileListBox.cs`, `QuickLaunch/DragDrop/LauncherDropInfoBuilder.cs`, `QuickLaunch/Services/WindowPlacement.cs` |
| Smoke diagnostics | `QuickLaunch/Diagnostics/AppearanceSmoke.cs` (new), `QuickLaunch/Diagnostics/PrototypeSmoke.cs`, `QuickLaunch/Diagnostics/DialogSmoke.cs`, `QuickLaunch/Diagnostics/DialogAppearanceSmoke.cs`, `QuickLaunch/Diagnostics/DialogBehaviorSmoke.cs`, `QuickLaunch/Diagnostics/LauncherAppearanceSmoke.cs`, `QuickLaunch/Diagnostics/LauncherInputSmoke.cs` |
| Unit tests | `QuickLaunch.Tests/AppearanceTests.cs` (new), `QuickLaunch.Tests/TileGeometryTests.cs` (new), `QuickLaunch.Tests/LauncherInputTests.cs`, `QuickLaunch.Tests/SettingsViewModelTests.cs`, `QuickLaunch.Tests/ThemeTests.cs`, `QuickLaunch.Tests/WindowPlacementTests.cs` |
| Documentation | `README.md`, `docs/QUICKSTART.zh-CN.md`, this report (new) |

Validation in the restricted Windows workspace:

- `.\scripts\Validate.ps1`: locked restore succeeded; Release solution build, including E2E, succeeded with **0 warnings and 0 errors** under `-warnaserror`. Unit results: **470 total, 464 passed, 6 failed, 0 skipped**. Exit code 1.
- `.\scripts\dotnet.ps1 build QuickLaunch.E2E/QuickLaunch.E2E.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'`: **succeeded, 0 warnings, 0 errors**, exit code 0. The FlaUI suite was built but not executed here.
- `git diff --check`: passed.
- `.\scripts\Run.ps1 -Smoke`: exit code 1, **incomplete**. It recorded **360 checks: 359 passed, 1 failed** (`tray-created`, unavailable in this desktop). It subsequently stopped at an access-denied save during the existing favicon smoke, and exit saving also failed. **All 71 appearance/panel checks passed**, including native Acrylic/Mica attributes on this OS. This is not a passing full-smoke result.

The final smoke produced outputs in `artifacts/validation/`: 12 `appearance-{dark|light}-{blue|purple}-{acrylic|mica|solid}.png` images, 4 `appearance-settings-{dark|light}-{blue|purple}.png` images and `panel-polish.png`. Visually inspected representative panel and light/dark settings samples: aligned content, unclipped controls and matching accents. Detailed machine-readable results are in `artifacts/tests/launcher.trx` and `artifacts/validation/smoke-result.json`.

The six unit failures all threw `UnauthorizedAccessException` in this sandbox. Tests and the production atomic writer were not weakened to work around it:

| Test | Failing operation |
| --- | --- |
| `ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot` | `File.Replace` |
| `ConfigTests.LegacyAutoHideSettingIsRetiredWithoutLosingItems` | `File.Replace` |
| `ConfigTests.SnapshotRoundtripCreatesBackupAndOmitsVolatileFields` | `File.Replace` |
| `ConfigTests.FailureKeepsOldFileAndAllowsSameRevisionRetry` | `File.Replace` |
| `AppearanceTests.LivePreferencesPersistResetAndRespectReadOnly` | `File.Replace` when saving reset defaults; the initial save and read-only assertions passed |
| `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance` | `NamedPipeClientStream.TryConnect` |

The passing tests cover every persisted appearance combination, default/invalid normalization, real WPF-UI variant generation and high-contrast cleanup, theme/accent call routing, open and preloaded dialog fallback, read-only settings, live controls, distributed columns, keyboard navigation and actual Gong insertion across rows and scroll offsets. The appearance snapshot test produced all 17 expected images.

Snapshots use the existing WPF `RenderTargetBitmap` capture composited on the theme background. They show layout and accent resources; they do not capture the physical DWM material, shadow or border. The smoke separately checks native material attributes on Windows 11 22621 and newer. Physical screenshots and the FlaUI run remain reviewer checks outside this sandbox.

Index SHA-256 before and after implementation: `FA43E4D32936EEEC17A133B386CFD9A2731C77C75D02E993096E24ABF03DC809`.

## Follow-up — preset accent rendered grey

The reviewer reported the initial implementation passing 470/470 unit tests, 619/619 desktop smoke checks and 14/14 E2E scenarios twice, and approved the panel layout. The subsequent real-startup purple screenshot revealed a gap in the accent assertions.

Root cause was reproduced without activation or a watcher: WPF-UI 4.3.0's [single-color accent overload](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Appearance/ApplicationAccentColorManager.cs) subtracts 45 percentage points of saturation for the dark secondary shade used by `AccentFillColorDefaultBrush`. Purple `#8764B8` has about 46% saturation. The new regression failed before the fix with **`#FFE2E2E3`, saturation 0.44%**, matching the reviewer's screen pixel. The previous smoke checked the unmodified `SystemAccentColor`, without checking the generated fill's hue/saturation.

Preset shades now use WPF-UI's `ColorExtensions.UpdateBrightness` and the explicit-palette `ApplicationAccentColorManager.Apply` overload. Dark variants brighten the preset while preserving its saturation; light variants darken it. There is no handwritten RGB/HSV conversion or new dependency. The existing theme service still reapplies the chosen preset on settings and Windows notification updates; only the system option calls `ApplySystemAccent`. High contrast continues to take precedence. Approved panel layout files were not changed in this follow-up.

Added real-process E2E scenarios seed dark/Mica/purple and light/Acrylic/green, launch the ordinary executable, acquire foreground, then sample three interior screen pixels using the selected tab's UIA bounds. Hue must be within 15 degrees of the preset and saturation at least 20%, stable for 350 ms. Assertions and screen captures repeat after hotkey reactivation, native appearance notifications and theme/material changes. These checks run before any settings interaction at startup and use neither `--smoke-test` nor product-private hooks. See [E2E instructions](../E2E.md).

Follow-up changes: `QuickLaunch/Services/ThemeService.cs`, `QuickLaunch/Diagnostics/AppearanceSmoke.cs`, `QuickLaunch.Tests/AppearanceTests.cs`, `QuickLaunch.E2E/Desktop.cs`, `QuickLaunch.E2E/LauncherSession.cs`, new `QuickLaunch.E2E/LauncherSession.Appearance.cs`, `QuickLaunch.E2E/LauncherTests.cs`, `docs/E2E.md` and this report.

Follow-up validation:

- Before the fix: the targeted two-case color regression had 1 passed / 1 failed, with dark/purple producing the exact grey above. Evidence: `artifacts/tests/accent-regression-before.trx`.
- After the fix: the targeted regression plus existing application-call test passed 3/3. Evidence: `artifacts/tests/accent-regression-after.trx`.
- `.\scripts\Validate.ps1`: locked restore and Release solution build succeeded with **0 warnings / 0 errors**; **474 unit tests: 468 passed, 6 failed, 0 skipped** (exit 1). All four new hue/lifecycle cases and the strengthened appearance smoke test passed. The same six `File.Replace` / named-pipe access-denied failures listed above remain in this sandbox. Evidence: `artifacts/tests/launcher.trx`.
- Explicit Release E2E build with `-warnaserror`: **succeeded, 0 warnings / 0 errors** (exit 0). There are now 16 desktop scenarios. Their real desktop execution remains for the reviewer; no E2E pass is claimed here.
- `git diff --check` passed; index SHA-256 remains the value above.
