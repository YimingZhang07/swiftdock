# Task 5: application icon

Validated on Windows 11 / .NET SDK 10.0.401, 2026-10-13. Earlier staged work was preserved; this task does not modify the index.

## Changes

| Files | Change |
| --- | --- |
| `scripts/GenerateIcon.ps1`, `QuickLaunch/Assets/QuickLaunch.ico` | Offline WPF renderer, hinted geometry, seven 32-bit DIB frames and one 256px PNG frame. Writes the ignored `artifacts/icon/preview.png`. |
| `QuickLaunch/QuickLaunch.csproj` | ApplicationIcon and WPF Resource use the same ICO. |
| `QuickLaunch/Services/AppIcon.cs`, `QuickLaunch/App.xaml.cs` | Select a native ICO frame using the taskbar HWND's DPI and SM_CXSMICON; refresh on tray DPI, taskbar recreation and display changes. Own and dispose the selected System.Drawing.Icon. Existing tooltip/menu/commands are retained. |
| `QuickLaunch/Views/DialogTheme.cs`, `QuickLaunch/Interop/NativeMethods.cs` | On SourceInitialized, retain existing styles, add WS_EX_DLGMODALFRAME, clear both WM_SETICON handles and refresh the non-client frame without activation. Then apply the immersive title-bar attribute. |
| `QuickLaunch.Tests/AppIconTests.cs`, `QuickLaunch.Tests/QuickLaunch.Tests.csproj` | Check ICO directory, all eight decoded frames, PNG signature, embedded resource bytes, native frame selection at 100/125/150/200/250/300/400% DPI and alpha. |
| `QuickLaunch/Diagnostics/PrototypeSmoke.cs`, `QuickLaunch/Diagnostics/DialogAppearanceSmoke.cs` | Inspect the actual tray object, compare selected icon pixels, extract and compare the exe icon, and check dialog HWND icons/styles/DWM attributes. |
| `scripts/Validate.ps1` | Restore uses one MSBuild node with parallel restore/node reuse disabled, matching the existing single-node build. Default parallel restore exited 1 without a reported error in this sandbox. |
| `README.md`, `docs/validation/app-icon.md` | Generator command and this validation record. |

The launcher panel, built-in URL globe, item icon service and delete-group MessageBox are unchanged.

## Small-frame review revision

Reviewer validation of the original revision outside the sandbox: 163/163 unit tests and 275/275 desktop smoke checks passed.

Re-hinted 16/20/24px frames with square, whole-pixel 4/5/6px tiles, 2px gaps and secondary white opacity of 72/70/65%. The 16/20px backgrounds fill the canvas in solid #2A73E3; the 16px disc uses explicit 45% corner pixels. The 32px secondary tiles now use 65% white; all other 32px geometry and the complete 40/48/64/256px frame payloads are unchanged. Accepted integration code, Validate.ps1 and the git index are unchanged.

Regenerated and visually inspected the preview. Two runs produced identical ICO and preview bytes. Current ICO SHA-256: `AB833969ED59AE2C778725634C807BB63D9B90D2146E183E1B64F64C201FAD25`.

`Validate.ps1`: Release build has 0 warnings/errors with `-warnaserror`; 157/163 tests pass, including all 9 icon tests. The same six sandbox access-denied failures listed below remain. The alpha assertion now permits minimal corner coverage (up to 8/255), required by the full-canvas rounded 16px background; frame-count, format and decode checks are unchanged. Desktop smoke was not repeated for this geometry-only revision.

## Initial results (before small-frame review)

- `scripts/GenerateIcon.ps1`: exit 0. Repeated generation produced identical ICO and preview bytes. Visually inspected all eight sizes on both backgrounds and the explicit 4x pixel-replicated zooms. Small grid edges and 2px gaps are integral; 16/20px have no gradient.
- ICO SHA-256: `9B41307F0F73CC472E1DBD2E171BDDBA0CC1F062C480AC35E4491E1D8B96E29E`.
- `scripts/Validate.ps1` and `scripts/Validate.ps1 -DesktopSmoke`: restore and Release build succeed, **0 warnings / 0 errors**, with `-warnaserror`. Final unit result: **157 passed / 6 failed / 0 skipped / 163 total**, including **all 9 AppIconTests passing**. Script exit 1, before its DesktopSmoke step.
- The six failures are access-denied exceptions in existing tests: four `ConfigTests` using File.Replace (`SlowWriteAndQueuedRevisionsKeepNewestSnapshot`, `LegacyAutoHideSettingIsRetiredWithoutLosingItems`, `SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`, `FailureKeepsOldFileAndAllowsSameRevisionRetry`); `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance` using a named pipe; and `InteractionTests.StartupRegistryRoundtripTouchesOnlyTestKey` writing a test registry key. No checks were skipped or weakened.
- Ran `scripts/Run.ps1 -Smoke` separately: exit 1; **251 recorded checks, 248 pass, 3 fail**, then an access-denied configuration save prevents completion. Existing failures are tray registration (`TryCreate failed`) and two mouse-capture checks. **All 51 icon/title-bar checks pass**, including exe icon pixels, tray ICO pixels/size, and WM_GETICON (small, big, small2), WS_EX_DLGMODALFRAME, WS_SYSMENU and DWM attribute 20 at initialization, dark/light display and theme update for SettingsWindow, EditItemDialog and NameDialog.
- `git diff --check`: pass. No new packages or network access.

Validation used only cached packages:

```powershell
$env:RestoreSources = Join-Path (Get-Location) '.tools/packages'
$env:NuGetAudit = 'false'
.\scripts\GenerateIcon.ps1
.\scripts\Validate.ps1 -DesktopSmoke
.\scripts\Run.ps1 -Smoke
```

Raw evidence: `artifacts/tests/launcher.trx`, `artifacts/validation/smoke-result.json` and the validation JSONL log. The report deliberately remains `completed: false` after the save failure.

## Not verified here

Actual Explorer/tray presentation, physical title-bar appearance, live monitor/DPI changes and Explorer restart need a normal desktop. Native HWND/DWM checks and extracted exe pixels passed, but WPF window snapshots omit the native title bar. The full validation suite is not claimed to pass in this sandbox.
