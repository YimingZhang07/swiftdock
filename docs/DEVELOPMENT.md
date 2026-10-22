# Developing Swiftdock

Windows 11 x64, PowerShell, .NET 10, WPF. The UI remains Chinese in 3.0.0. Product text is kept as ordinary view strings for the following localisation task; assemblies, paths and command-line identifiers use Swiftdock.

## Setup and build

```powershell
.\scripts\Setup.ps1
.\scripts\Validate.ps1
.\scripts\Run.ps1
.\scripts\Run.ps1 -Silent
.\scripts\Run.ps1 -ConfigDirectory 'D:\LauncherProfile'
```

Setup installs SDK 10.0.401 and Inno Setup 7.1.0 under ignored `.tools/`, verifies downloads and leaves the system SDK alone. NuGet packages are locked in each project's `packages.lock.json`; no global runtime or environment changes are needed. Scripts launch the apphost, preserving its asInvoker / PerMonitorV2 manifest. Login startup requires a self-contained release or a system .NET 10 Windows Desktop Runtime; the repository-only runtime does not suffice.

Use `scripts/dotnet.ps1` for SDK commands. For a fully cached offline restore:

```powershell
.\scripts\dotnet.ps1 restore Swiftdock.slnx --source .tools/packages --force-evaluate --disable-parallel '-m:1' '-nr:false'
```

Regenerate locks only when necessary and review resolved package versions and content hashes. WPF-UI 4.3.0 provides standard controls, themes and accents; CommunityToolkit.Mvvm provides observable models and commands. DI uses an empty generic host with WPF owning the lifetime. SharpVectors renders embedded Fluent colour SVGs on the icon worker. Keep dependency and licence updates together.

## Validation tiers

| Command | When | Coverage / output |
| --- | --- | --- |
| `scripts/Validate.ps1` | Every change, including sandbox work | Locked restore, app and unit tests in Release with warnings as errors; `artifacts/tests/launcher.trx`. |
| `scripts/E2E.ps1` | UI changes, on an unlocked desktop | Real input, windows, tray and appearance; `artifacts/e2e/e2e.trx`. |
| `scripts/Snapshots.ps1` | Visual review, on an unlocked desktop | WPF composition PNGs and dark settings text contrast; `artifacts/validation/snapshots.json`. |

Default validation neither builds nor runs desktop tools. Unit tests cover config, view models, parsing, icons, geometry, resources and DI lifetime. E2E and snapshots use isolated profiles; do not use an everyday profile for validation. See [E2E.md](E2E.md) for desktop requirements and filters. For the 3.0 rename, run the entire existing E2E suite and snapshot plan once, particularly tray tooltip/menu, hotkey/second instance, settings/About/exit, portable/profile handling and appearance. No new desktop scenarios were added.

Build desktop tools without running them:

```powershell
.\scripts\dotnet.ps1 build Swiftdock.E2E/Swiftdock.E2E.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'
.\scripts\dotnet.ps1 build Swiftdock.Snapshots/Swiftdock.Snapshots.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'
```

`--e2e-launch-log <file>` requires an isolated `--config-dir`; it replaces Shell launch with JSONL records containing `id`, `name` and `elevated`. All CLI switches retain their existing spelling, including `--exit`, `--silent`, `--diagnostics-dir` and benchmark flags.

Snapshots use one bounded worker process per image (six seconds each), attempt remaining images after a failure and update `snapshots.json` throughout. The script has a 90-second overall default deadline (`-TimeoutSeconds` overrides it). Inspect the manifest, not stale PNGs. Windows remain offscreen without activation; confirm dialogs avoid modal loops. Images show WPF composition, without native DWM material, borders or shadows; E2E checks native materials.

The plan includes dark/light launcher, launcher status, all settings pages, add/edit/name dialogs, icon pickers, item menu and delete confirmation; blue/purple material combinations; panel polish; and 80%/150% launcher scales. See `SnapshotRunner.CreatePlan` for the complete inventory. README images in `docs/images/` are unmodified renderer output copied from `artifacts/validation/launcher.png`, `launcher-light.png` and `settings-appearance.png`; refresh those three after visual review. They were reused for the rename because these views contain no old branding.

## Release and versioning

```powershell
.\scripts\Release.ps1
.\scripts\Release.ps1 -SkipTests
.\scripts\Release.ps1 -E2E
```

The single version source is `<Version>` in `Swiftdock/Swiftdock.csproj`. About, executable versions, installer and artifact names follow it. A release publishes only the self-contained win-x64 `Swiftdock.exe` into `artifacts/staging/<version>/`, without PDBs, bundle compression or ReadyToRun. It rebuilds `artifacts/release/<version>/` with `Swiftdock-<version>-Setup.exe`, `Swiftdock-<version>-Portable.zip` and `SHA256SUMS.txt`. The ZIP contains `Swiftdock/Swiftdock.exe`, an empty `Swiftdock/Data/` and licence notices. Installer and ZIP provide compression. Rebuilding a version replaces those outputs; `-E2E` still runs when combined with `-SkipTests`.

`GenerateIcon.ps1` regenerates the same application artwork as `Assets/Swiftdock.ico` and `artifacts/icon/preview.png`; the rename does not change artwork. Dependency notices are embedded in the executable and shipped in the portable ZIP.

## Installer upgrade review

The permanent production AppId is `{22501D8C-9899-4325-8628-268B36134004}`. Keep it unchanged. Before installing, Setup reads the previous `InstallLocation` and `UninstallString` from that identity's HKCU uninstall entry. If `InstallLocation` is absent, production builds also check `%LOCALAPPDATA%\Programs\QuickLaunch` for `QuickLaunch.exe`. It requests bounded graceful shutdown of both executable names by full path, never force-kills, and refuses to install back into the legacy directory. After copying new files, it deletes the old executable and recorded uninstaller's `.exe`/`.dat`/`.msg`; the missing-key fallback instead permits only `unins000.exe`/`.dat`/`.msg` in that exact folder. The same-directory guard always preserves the new uninstaller and skips directory removal. Otherwise the old folder is removed only if empty. Old QuickLaunch shortcuts are removed only when WScript.Shell reports a target inside the previous legacy directory; unrelated or unreadable shortcuts remain. Normal Swiftdock upgrades retain their previous directory. Uninstall's optional data deletion defaults to No and includes both names; silent uninstall retains data.

For **reviewer isolation only**, compile with a fresh AppId; Release.ps1 never uses this override:

```powershell
& .\.tools\innosetup\ISCC.exe '/DTestAppId={7E3FCD4D-5CD0-4ECA-BC54-3F07472B503B}' /DAppVersion=3.0.0 "/DPublishDir=$PWD\artifacts\staging\3.0.0" "/DReleaseDir=$PWD\artifacts\installer-test" .\installer\Swiftdock.iss
```

Both braced and unbraced GUIDs are accepted. Use a separate install directory. This isolates the Installed apps entry; shared shortcut names and default data paths still apply. For upgrade review, seed a legacy **test** installation under that same test identity and use a separate Windows user for data-deletion checks. Never point the test entry at the owner's actual installation. Verify one Installed apps entry, the new directory, graceful shutdown and saved data, old-file/shortcut cleanup, autostart migration, and uninstall's No/Yes choices manually.

To review the missing-key fallback, add `"/DTestLegacyDir=$PWD\artifacts\legacy-test"` to that compiler command and place a legacy test installation there without an `InstallLocation` in the test uninstall key. `TestLegacyDir` is honoured only together with `TestAppId`; test builds without it have no fallback, and never inspect the owner's default QuickLaunch folder. Release.ps1 uses neither define. Check that a QuickLaunch shortcut targeting the test legacy directory is removed while one targeting a different directory is retained.

## Profiles and migration

Default config is `%APPDATA%\Swiftdock\config.json`; icons and logs are under `%LOCALAPPDATA%\Swiftdock`. A sibling `Data/` selects portable mode, placing config, icons and logs there. `--config-dir` overrides that choice; `--diagnostics-dir` independently overrides logs.

Only a normal default-profile startup copies the legacy `%APPDATA%\QuickLaunch` config and optional backup, without overwriting new files or deleting old ones. Backup is copied first so failure before the primary copy remains retryable. The copy happens under the new single-instance lease, before opening config; a successful copy emits `migration.config` once. Cache and logs are not copied. Legacy `QuickLaunch_*` Run values are migrated only when their command uses the old default config or a `QuickLaunch.exe` without `--config-dir`. The new value is written before matching old values are deleted; unrelated entries remain. Registry access uses the existing injected seam in unit tests.

Config writes are atomic, with backup recovery and preserved broken originals; future versions open read-only. Appearance defaults are omitted. Legacy `iconSize` 32/40/48 maps to scale 80/100/120 and is retired on save. Scale is 80–150 in steps of 10, default 100; `backdropOpacity` is 0–100 and the UI displays `100 - backdropOpacity`. Theme/accent default to system, backdrop to acrylic, placement to last position and always-on-top to true. High contrast overrides visual choices without rewriting preferences.

## Performance and diagnostics

```powershell
.\scripts\Benchmark.ps1 -Interactive
.\scripts\Benchmark.ps1 -SingleGroup -Prewarm
.\scripts\Benchmark.ps1 -Prewarm -IdleSeconds 300
.\scripts\Benchmark.ps1 -Executable 'artifacts\staging\3.0.0\Swiftdock.exe' -Interactive
.\scripts\MeasureStartup.ps1 -Executable 'artifacts\staging\3.0.0\Swiftdock.exe'
```

Benchmarks generate isolated data (four groups × 50 items, or one group of 200), show/hide and switch groups without launching items, and disable website requests. Background processes may lack foreground permission; focus failures are reported separately. First, prewarmed, idle-resume and first/visited-group timings remain distinct. Do not force GC or trim the working set. Logs go to `artifacts/benchmarks/` and `artifacts/startup/`. Startup measurements may explicitly use `-AllowUnavailableTray` in a trayless environment; this allowance applies only in performance mode. See [LOGGING.md](LOGGING.md) for CLEF events, privacy and diagnostic-reading scripts.

To check resources inside a single-file release without starting the application, run after `Release.ps1 -SkipTests`:

```powershell
$env:SWIFTDOCK_PUBLISHED_EXE = (Resolve-Path artifacts/staging/3.0.0/Swiftdock.exe).Path
.\scripts\dotnet.ps1 test Swiftdock.Tests/Swiftdock.Tests.csproj -c Release --no-build --no-restore --filter BothCulturesResolveFromPublishedBundleWhenProvided
Remove-Item Env:SWIFTDOCK_PUBLISHED_EXE
```

The existing unit host reads the bundled app and Chinese satellite into an isolated assembly context and resolves both resource cultures. It does not launch the app or installer.
