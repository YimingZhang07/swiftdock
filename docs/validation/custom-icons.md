# Task 7 — custom item icons

Implemented against the earlier tasks already staged in the index. All Task 7 changes are unstaged; new source/test files are untracked. The index was not written. No NuGet packages, image packs, network requests, or standalone probe programs were added.

## Behavior and decisions

- `LaunchItem.Icon` is optional and omitted when null. Normalization accepts only the 60 catalog glyphs and eight approved colors, canonicalizes hex and portable file paths, and drops malformed specs. A non-string JSON `icon` is also ignored. Membership is independent of the installed font, so Windows 10 does not erase a valid configuration.
- The picker uses the installed Segoe Fluent Icons font, or Segoe MDL2 Assets with unsupported glyphs hidden. Chinese tooltips and automation names accompany the eight-column, 36-DIP grid. Glyphs are ordered by application/development, network, communication, media, office, files, system, and life.
- `IconService` draws actual glyph outlines in white at 56% em size on a full-box rounded square (22% radius), with +6% / -6% lightness stops. Bounds are centered and translated to physical pixels. Cache identity includes the canonical spec and font choice. The normal DPI pipeline requests 32/40/48 pixels at 100% and 64/80/96 at 200%; the editor refreshes its preview on DPI changes.
- Image decoding chooses the nearest frame by maximum dimension, preferring the larger frame on a tie and then higher bit depth. Images retain their aspect ratio inside the requested square. EXE/DLL without an index use the existing Shell extraction; explicit indices use `PrivateExtractIconsW` (negative values address resource IDs), with owned HICONs released. Missing or corrupt files fall back to the target's default without setting a launch error. URLs retain their built-in globe by default.
- The popup follows the existing menu brushes/shadow, supports arrow keys, Home/End, Tab, Space/Enter, Escape and standard WPF outside-click dismissal. Selecting an icon updates the preview without saving the item; Save commits, Cancel discards. File paths are referenced, not copied. Restore Default removes the spec. The context command follows Edit and is disabled whenever editing is unavailable.
- Icon changes invalidate the tile visual; rename, editing other fields, same-group/cross-group moves, move-to-new-group and delete/undo preserve the spec. Duplicate identity is unchanged.

## Changed files

| Area | Files |
| --- | --- |
| Model / normalization | `QuickLaunch/Models/LaunchItem.cs`, `QuickLaunch/Services/ConfigValidation.cs`, new `QuickLaunch/Services/IconSpec.cs` |
| Catalog / rendering / extraction | new `QuickLaunch/Services/IconCatalog.cs`, `QuickLaunch/Services/IconService.cs`, `QuickLaunch/Interop/NativeMethods.cs` |
| Editor and popup | `QuickLaunch/Views/EditItemDialog.xaml`, `QuickLaunch/Views/EditItemDialog.xaml.cs`, new `QuickLaunch/Views/EditItemDialog.Icons.cs` |
| Tile context menu / updates | `QuickLaunch/Views/LauncherWindow.xaml`, `QuickLaunch/Views/LauncherWindow.Management.cs`, `QuickLaunch/ViewModels/MainViewModel.cs` |
| Unit tests | new `QuickLaunch.Tests/CustomIconTests.cs`, `QuickLaunch.Tests/InteractionTests.cs` |
| Desktop smoke | new `QuickLaunch/Diagnostics/IconPickerSmoke.cs`, `QuickLaunch/Diagnostics/EditItemDialogSmoke.cs`, `QuickLaunch/Diagnostics/PrototypeSmoke.cs` |
| Documentation | `README.md`, `docs/QUICKSTART.zh-CN.md`, this file |

## Validation — 2026-10-13

Validation used only the existing package cache:

```powershell
$env:RestoreSources = Join-Path (Get-Location) '.tools/packages'
$env:NuGetAudit = 'false'
.\scripts\Validate.ps1 -DesktopSmoke
.\scripts\Validate.ps1
.\scripts\Run.ps1 -Smoke
```

- **Final `Validate.ps1`: exit 1.** Locked restore succeeds. Release build with `-warnaserror`: **0 warnings, 0 errors**. **253 passed / 6 failed / 0 skipped / 259 total**. All **50 new tests pass**, including the full 60-glyph `GlyphTypeface.CharacterToGlyphMap` check (Fluent is installed), exact legacy JSON bytes, optional/icon round trips, invalid values, portable paths, duplicate identity, rendering at 32/40/48/64/80/96 pixels, white filled-star center / transparent corners / gradient, memory/disk cache, colored multi-frame ICO selection, PNG/JPG/BMP aspect ratio, PE resource indices, default fallback, target-independent preview, and preservation through edit/rename/moves/delete/undo.
- **`Validate.ps1 -DesktopSmoke`: exit 1 before its smoke phase**, with the same six access-denied tests. This earlier invocation had 252 passed / 6 failed / 0 skipped / 258 total; the final invocation above includes the additional target-independent file-preview regression test.
- The six failures match the prior tasks' recorded sandbox restrictions: `ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot`, `LegacyAutoHideSettingIsRetiredWithoutLosingItems`, `SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`, and `FailureKeepsOldFileAndAllowsSameRevisionRetry` fail at `File.Replace`; `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance` fails on a named pipe; `InteractionTests.StartupRegistryRoundtripTouchesOnlyTestKey` fails on the test registry key. These checks were not skipped or weakened.
- **Separate final `Run.ps1 -Smoke`: exit 1**, **315 passed / 3 failed / 318 recorded checks**. All **17 new custom-icon checks pass**. The existing failures are `tray-created`, `root-capture-acquired`, and `capture-survives-hidden-source-group`. An access-denied configuration save later prevents completion; `smoke-result.json` correctly remains `completed: false`. The popup outside-dismiss check verifies WPF `StaysOpen=false`; it is not a physical mouse-input test. Arrow/Enter/Escape checks use routed input inside the real WPF dispatcher.
- **`git diff --check`: pass.** Index SHA-256 remained `A2F940EE578BEC2EC8E303A5840EA5FB5CE334332D60523F5C01CFAC57D5FE58` across validation.

Raw results: `artifacts/tests/launcher.trx`, `artifacts/validation/smoke-result.json`, and `artifacts/validation/quicklaunch-*.jsonl`.

## Screenshots

Generated and visually inspected:

- `artifacts/validation/icon-picker.png` — dark editor with the popup open.
- `artifacts/validation/icon-picker-light.png` — light editor with the popup open.
- `artifacts/validation/launcher-custom-icons.png` — four custom glyph items alongside the default Shell icons and URL globe.
- `artifacts/validation/icon-library.png` — all 60 final glyphs with Chinese labels and code points for visual review.

These are WPF render snapshots. The picker capture composites the real editor and the popup's separate presentation source at their screen-relative DIP positions. They do not capture DWM/native title bars or prove physical presentation at 200% DPI. Pixel-size rendering is unit-tested; live monitor transitions, real outside clicks and the native file-dialog interaction still need desktop review. Artifacts remain in the repository's existing ignored `artifacts/` directory.
