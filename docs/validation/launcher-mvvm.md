Task 16b — launcher panel MVVM, 2026-10-14, branch `ui/round3`.

> Historical report: the in-process smoke suite and its commands were retired in Task 19. Use the three tiers and coverage mapping in [test-consolidation.md](test-consolidation.md) for current validation.

`MainViewModel` owns persisted configuration, validated mutations, undo records, and debounced saves.
`LauncherViewModel` owns transient panel state, per-group selection, commands, keyboard precedence, toast policy, and import lifetime.

The window binds to `LauncherViewModel`; configuration bindings use its `State` property. Tile and group menus use generated commands with the clicked item/group as their parameter. `ILauncherDialogs` adapts the existing `IDialogService` without exposing window ownership to the model. `ILauncherActions` supplies launch, shell location, icon refresh, and drop resolution operations. The DI container owns the panel model. The existing drag coordinator publishes active-state changes and receives cancellation requests through the window; the model does not reference the coordinator or create windows.

The view retains native hooks, placement/backdrop, focus/navigation geometry, drag/drop hit testing and visuals, DPI-aware icon rendering, header movement, and toast animation/timers. Mouse group changes and panel shows still clear selection; keyboard group changes select the first non-pending item. Launch results wait for drag completion, preserve selection across record replacement, and keep successful launches silent. Toasts retain the existing four/six-second policy and remain pinned during imports.

Validation:

| Command / check | Result |
| --- | --- |
| `.\scripts\Validate.ps1` (TEMP/TMP under `artifacts/test-tmp`) | Locked restore passed; Release solution build passed with 0 warnings / 0 errors; 414 passed, 5 failed, 0 skipped, 419 total (43 s); exit 1 |
| `.\scripts\dotnet.ps1 build QuickLaunch.E2E/QuickLaunch.E2E.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'` | Passed; 0 warnings / 0 errors; exit 0 |
| `.\scripts\dotnet.ps1 test QuickLaunch.Tests/QuickLaunch.Tests.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror' --filter 'FullyQualifiedName~LauncherViewModelTests\|FullyQualifiedName~LauncherInputTests\|FullyQualifiedName~HostingTests' --logger 'trx;LogFileName=launcher-mvvm-focused.trx' --results-directory artifacts/tests` | 22 passed, 0 failed, 0 skipped (9 s); exit 0 |
| New model tests | 20/20 passed without creating an Application or Window; includes Esc/Enter/Delete precedence, command guards, injected operations, selection, group switching/deletion, toast timing policy, import cancellation/errors/duplicates, and late launch completion |
| Existing routed-input and hosting checks | Passed, including new assertions for menu command parameters and the shared panel-model DI registration |
| XAML comparison with starting index | All 25 x:Name values and 5 AutomationProperties.Name values unchanged; neither version has explicit AutomationProperties.AutomationId attributes |
| `git diff --check` | Passed |
| Desktop smoke / FlaUI real-input execution | Not run in this sandbox; reviewer runs these externally; E2E sources unchanged |

The five full-suite failures are the same existing `UnauthorizedAccessException` failures recorded in [task 16a](dialog-mvvm.md):

- `HotkeyAndInstanceTests.SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance`: `NamedPipeClientStream.TryConnect`.
- `ConfigTests.SlowWriteAndQueuedRevisionsKeepNewestSnapshot`: `FileSystem.ReplaceFile`.
- `ConfigTests.LegacyAutoHideSettingIsRetiredWithoutLosingItems`: `FileSystem.ReplaceFile`.
- `ConfigTests.SnapshotRoundtripCreatesBackupAndOmitsVolatileFields`: `FileSystem.ReplaceFile`.
- `ConfigTests.FailureKeepsOldFileAndAllowsSameRevisionRetry`: `FileSystem.ReplaceFile`.

The failing tests and their production code were not changed. The full suite did not pass here. Reports are `artifacts/tests/launcher.trx` and `artifacts/tests/launcher-mvvm-focused.trx`.

Code-behind counts include blank lines and compare against the initial working tree, which matched the starting index:

| File in QuickLaunch/Views | Before | After | Net removed |
| --- | ---: | ---: | ---: |
| LauncherWindow.xaml.cs | 803 | 728 | 75 |
| LauncherWindow.Management.cs | 274 | 185 | 89 |
| Total | 1,077 | 913 | 164 |

No packages, project files, lock files, or E2E sources changed. All task changes are unstaged. The git index SHA-256 before and after is identical:
`9377303FEB24A777ADBB618A6B339609D876DD84844CFA8F4E152C979C9C516E`.

Complete file inventory relative to the starting index:

- `QuickLaunch/ViewModels/LauncherViewModel.cs` — added
- `QuickLaunch/Services/LauncherDependencies.cs` — added
- `QuickLaunch.Tests/LauncherViewModelTests.cs` — added
- `docs/validation/launcher-mvvm.md` — added
- `QuickLaunch/ViewModels/MainViewModel.cs` — ownership documentation
- `QuickLaunch/Services/ApplicationServices.cs` — panel-model DI registration
- `QuickLaunch/Views/LauncherWindow.xaml` — command and state bindings
- `QuickLaunch/Views/LauncherWindow.xaml.cs` — view-specific interaction forwarding
- `QuickLaunch/Views/LauncherWindow.Management.cs` — menu commands and selection binding
- `QuickLaunch/Views/TileListBox.cs` — selection ownership documentation
- `QuickLaunch/DragDrop/DragCoordinator.cs` — active-state notification and command routing
- `QuickLaunch/Diagnostics/DialogSmoke.cs` — shared smoke composition
- `QuickLaunch/Diagnostics/DialogBehaviorSmoke.cs` — group-delete command invocation
- `QuickLaunch/Diagnostics/FaviconSmoke.cs` — composition and refresh command invocation
- `QuickLaunch/Diagnostics/LauncherAppearanceSmoke.cs` — composition, model import state, and launch commands
- `QuickLaunch/Diagnostics/LauncherInputSmoke.cs` — composition, tab command invocation, and menu binding checks
- `QuickLaunch/Diagnostics/PopupSmokeScope.cs` — observe model menu state instead of a private window field
- `QuickLaunch/Diagnostics/WindowPositionSmoke.cs` — composition
- `QuickLaunch/Diagnostics/WindowVisibilitySmoke.cs` — composition and launch commands
- `QuickLaunch.Tests/DialogTests.cs` — composition
- `QuickLaunch.Tests/HostingTests.cs` — verify the shared panel model and DataContext
- `QuickLaunch.Tests/LauncherInputTests.cs` — composition
- `QuickLaunch.Tests/ThemeTests.cs` — composition
