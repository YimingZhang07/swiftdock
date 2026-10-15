Task 16a — dialog MVVM, 2026-10-14, branch `ui/round3`.

> Historical report: the in-process smoke suite and its commands were retired in Task 19. Use the three tiers and coverage mapping in [test-consolidation.md](test-consolidation.md) for current validation.

Settings, item editing and naming now use CommunityToolkit.Mvvm generated properties and commands. Settings dependencies include configuration state, StartupService, IHotkeyService, application actions and ILogger. The editor injects file-picker and filesystem/common-location dependencies. Its validation, name inference, type inference, icon state and result construction can run without windows.

The DI dialog service creates windows in per-dialog scopes, assigns the launcher owner, returns modal results and maintains owned-window counting. Views retain focus, keyboard navigation, native file dialogs, popup presentation and DPI-aware icon rendering. Existing factories were removed. All changes are unstaged; no packages or project/lock files changed.

Validation:

| Command / check | Result |
| --- | --- |
| `.\scripts\Validate.ps1` | Locked restore passed; Release solution build passed with 0 warnings / 0 errors; tests: 394 passed, 5 failed, 0 skipped, 399 total (42 s); exit 1 |
| Same script with TEMP and TMP under `artifacts/test-temp` | Same result: 394 passed, 5 failed, 0 skipped, 399 total (42 s); exit 1 |
| `.\scripts\dotnet.ps1 build QuickLaunch.E2E/QuickLaunch.E2E.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'` | Passed; 0 warnings / 0 errors; exit 0 |
| Focused tests, filter `FullyQualifiedName~ViewModelTests\|FullyQualifiedName~DialogTests` | 35 passed, 0 failed, 0 skipped (31 s); exit 0 |
| New headless tests in full run | Settings 5/5, EditItem 24/24, NameDialog 3/3 passed |
| Existing dialog smokes and expanded DI/ownership test in full run | DialogTests 3/3 and HostingTests 1/1 passed |
| XAML comparison against starting index | Same x:Name, explicit AutomationIds, IME preferences, IsDefault and IsCancel values |
| `git diff --check` | Passed |
| FlaUI real-input E2E execution | Not run here; E2E build completed for external reviewer execution |

The five failing existing tests throw `UnauthorizedAccessException` in this sandbox, including when temporary files are under the workspace:

- `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance`: `NamedPipeClientStream.TryConnect`.
- `ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot`: `FileSystem.ReplaceFile`.
- `ConfigTests.LegacyAutoHideSettingIsRetiredWithoutLosingItems`: `FileSystem.ReplaceFile`.
- `ConfigTests.SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`: `FileSystem.ReplaceFile`.
- `ConfigTests.FailureKeepsOldFileAndAllowsSameRevisionRetry`: `FileSystem.ReplaceFile`.

These tests and the failing SingleInstanceService/AtomicConfigWriter code were not changed. This is not a claim that the full suite passed. Reports: `artifacts/tests/task16a-default.trx`, `artifacts/tests/launcher.trx`, `artifacts/tests/mvvm.trx`.

Code-behind line counts compare the working tree with the starting index (including blank lines):

| File in QuickLaunch/Views | Before | After | Net removed |
| --- | ---: | ---: | ---: |
| SettingsWindow.xaml.cs | 109 | 27 | 82 |
| EditItemDialog.xaml.cs | 203 | 88 | 115 |
| EditItemDialog.Icons.cs | 117 | 81 | 36 |
| NameDialog.xaml.cs | 15 | 17 | -2 |
| Total dialog code-behind | 444 | 213 | 231 |

The diff removes 314 dialog code-behind lines and adds 83. Separately, moving dialog ownership and settings application out of App.xaml.cs and LauncherWindow.Management.cs removes another 35 net lines.

The git index SHA-256 before and after is identical:
`5B7F382DC9E9AE7E5812F39FA5E44BEDE69513B01987B38C799426176B98205B`.

Complete file inventory relative to the starting index:

- `QuickLaunch.Tests/DialogTests.cs` — modified
- `QuickLaunch.Tests/EditItemViewModelTests.cs` — added
- `QuickLaunch.Tests/HostingTests.cs` — modified
- `QuickLaunch.Tests/LauncherInputTests.cs` — modified
- `QuickLaunch.Tests/NameDialogViewModelTests.cs` — added
- `QuickLaunch.Tests/SettingsViewModelTests.cs` — added
- `QuickLaunch.Tests/ThemeTests.cs` — modified
- `QuickLaunch/App.xaml.cs` — modified
- `QuickLaunch/Diagnostics/DialogAppearanceSmoke.cs` — modified
- `QuickLaunch/Diagnostics/DialogBehaviorSmoke.cs` — modified
- `QuickLaunch/Diagnostics/DialogSmoke.cs` — added
- `QuickLaunch/Diagnostics/EditItemDialogSmoke.cs` — modified
- `QuickLaunch/Diagnostics/FaviconSmoke.cs` — modified
- `QuickLaunch/Diagnostics/IconPickerSmoke.cs` — modified
- `QuickLaunch/Diagnostics/LauncherAppearanceSmoke.cs` — modified
- `QuickLaunch/Diagnostics/LauncherInputSmoke.cs` — modified
- `QuickLaunch/Diagnostics/PrototypeSmoke.cs` — modified
- `QuickLaunch/Diagnostics/WindowPositionSmoke.cs` — modified
- `QuickLaunch/Diagnostics/WindowVisibilitySmoke.cs` — modified
- `QuickLaunch/Services/ApplicationServices.cs` — modified
- `QuickLaunch/Services/CommonLocations.cs` — modified
- `QuickLaunch/Services/DialogDependencies.cs` — added
- `QuickLaunch/Services/DialogService.cs` — added
- `QuickLaunch/Services/HotkeyService.cs` — modified
- `QuickLaunch/ViewModels/EditItemViewModel.cs` — added
- `QuickLaunch/ViewModels/MainViewModel.cs` — modified
- `QuickLaunch/ViewModels/NameDialogViewModel.cs` — added
- `QuickLaunch/ViewModels/SettingsViewModel.cs` — added
- `QuickLaunch/Views/DialogFactory.cs` — deleted
- `QuickLaunch/Views/DialogFilePicker.cs` — added
- `QuickLaunch/Views/EditItemDialog.Icons.cs` — modified
- `QuickLaunch/Views/EditItemDialog.xaml` — modified
- `QuickLaunch/Views/EditItemDialog.xaml.cs` — modified
- `QuickLaunch/Views/LauncherWindow.Management.cs` — modified
- `QuickLaunch/Views/LauncherWindow.xaml.cs` — modified
- `QuickLaunch/Views/NameDialog.xaml` — modified
- `QuickLaunch/Views/NameDialog.xaml.cs` — modified
- `QuickLaunch/Views/SelectionConverter.cs` — added
- `QuickLaunch/Views/SettingsWindow.xaml` — modified
- `QuickLaunch/Views/SettingsWindow.xaml.cs` — modified
- `docs/validation/dialog-mvvm.md` — added
