[CmdletBinding()]
param([ValidateRange(30, 1800)][int]$TimeoutSeconds = 300, [string]$Filter = '')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$previousOptIn = $env:SWIFTDOCK_E2E
$previousRunId = $env:SWIFTDOCK_E2E_RUN_ID
$runId = [Guid]::NewGuid().ToString('N')
$e2eRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'Swiftdock.E2E')).TrimEnd('\') + '\'
$output = Join-Path $repoRoot 'artifacts/e2e'
$runner = $null
$failure = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Stop-TestHostTree {
    if ($null -eq $runner) { return }
    # Stop only descendants of this run, excluding product processes: those get
    # their normal --exit handshake in Stop-E2EInstances below.
    $all = @(Get-CimInstance Win32_Process -OperationTimeoutSec 3)
    $ids = [Collections.Generic.HashSet[int]]::new()
    $null = $ids.Add($runner.Id)
    do {
        $added = $false
        foreach ($entry in $all) {
            if ($ids.Contains([int]$entry.ParentProcessId) -and $ids.Add([int]$entry.ProcessId)) { $added = $true }
        }
    } while ($added)
    foreach ($entry in @($all | Where-Object { $ids.Contains([int]$_.ProcessId) -and $_.Name -ne 'Swiftdock.exe' })) {
        $owned = Get-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $owned) { continue }
        try {
            $null = $owned.Handle
            if ([Math]::Abs(($owned.StartTime.ToUniversalTime() - $entry.CreationDate.ToUniversalTime()).TotalSeconds) -gt 1) { continue }
            if (-not $owned.HasExited) { $owned.Kill() }
            if (-not $owned.WaitForExit(2000)) { $cleanupErrors.Add("Test host PID $($owned.Id) did not stop within 2 seconds.") }
        }
        catch { if (-not $owned.HasExited) { $cleanupErrors.Add("Test host PID $($entry.ProcessId): $($_.Exception.Message)") } }
        finally { $owned.Dispose() }
    }
}

function Stop-RecordedHosts {
    # Independent fallback if CIM enumeration failed or a runner died before its
    # children. Exact start-time checks prevent touching a recycled PID.
    if (-not (Test-Path -LiteralPath $e2eRoot)) { return }
    foreach ($directory in @(Get-ChildItem -LiteralPath $e2eRoot -Directory)) {
        $marker = Join-Path $directory.FullName 'run.id'
        if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw) -ne $runId) { continue }
        try { Set-Content -LiteralPath (Join-Path $directory.FullName 'abort') -Value 'Overall runner cleanup' } catch { }
        $identities = @()
        $supervisor = Join-Path $directory.FullName 'supervisor.json'
        if (Test-Path -LiteralPath $supervisor) { $identities += Get-Content -LiteralPath $supervisor -Raw | ConvertFrom-Json }
        $workerPid = Join-Path $directory.FullName 'worker.pid'
        $workerStarted = Join-Path $directory.FullName 'worker.started'
        if ((Test-Path -LiteralPath $workerPid) -and (Test-Path -LiteralPath $workerStarted)) {
            $identities += [pscustomobject]@{ Pid = [int](Get-Content -LiteralPath $workerPid -Raw); Started = [long](Get-Content -LiteralPath $workerStarted -Raw) }
        }
        foreach ($identity in $identities) {
            $owned = Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
            if ($null -eq $owned) { continue }
            try {
                $null = $owned.Handle
                if ($owned.StartTime.ToUniversalTime().Ticks -ne $identity.Started) { continue }
                if (-not $owned.HasExited) { $owned.Kill() }
                if (-not $owned.WaitForExit(2000)) { $cleanupErrors.Add("Recorded test host PID $($identity.Pid) did not exit.") }
            }
            catch { if (-not $owned.HasExited) { $cleanupErrors.Add("Recorded host cleanup: $($_.Exception.Message)") } }
            finally { $owned.Dispose() }
        }
    }
}

function Get-E2EInstances {
    foreach ($entry in @(Get-CimInstance Win32_Process -Filter "Name='Swiftdock.exe'" -OperationTimeoutSec 3)) {
        if ($entry.CommandLine -notmatch '(?:^|\s)--config-dir(?:=|\s+)(?:"(?<quoted>[^"]+)"|(?<plain>\S+))') { continue }
        $configDirectory = if ($Matches.quoted) { $Matches.quoted } else { $Matches.plain }
        try { $configDirectory = [IO.Path]::GetFullPath($configDirectory).TrimEnd('\') } catch { continue }
        if (-not $configDirectory.StartsWith($e2eRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }
        [pscustomobject]@{ Entry = $entry; Profile = $configDirectory }
    }
}

function Release-RecordedInput {
    if (-not (Test-Path -LiteralPath $e2eRoot)) { return }
    # Only release inputs journaled by this run, after its hosts have stopped.
    if (-not ('SwiftdockE2EInputCleanup' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class SwiftdockE2EInputCleanup {
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, System.UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, System.UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
'@
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $e2eRoot -Directory)) {
        $marker = Join-Path $directory.FullName 'run.id'
        $journal = Join-Path $directory.FullName 'input.jsonl'
        if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw) -ne $runId -or -not (Test-Path -LiteralPath $journal)) { continue }
        $records = [IO.File]::ReadAllText($journal).Split([char]10)
        if ($records.Length -lt 2) { continue }
        $state = $records[$records.Length - 2] | ConvertFrom-Json # Last complete record; ignore a torn final write.
        foreach ($key in $state.Keys) { [SwiftdockE2EInputCleanup]::keybd_event([byte]$key, 0, 2, [UIntPtr]::Zero) }
        foreach ($button in $state.Buttons) {
            switch ($button) {
                'Left' { [SwiftdockE2EInputCleanup]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
                'Right' { [SwiftdockE2EInputCleanup]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero) }
                'Middle' { [SwiftdockE2EInputCleanup]::mouse_event(64, 0, 0, 0, [UIntPtr]::Zero) }
                'XButton1' { [SwiftdockE2EInputCleanup]::mouse_event(256, 0, 0, 1, [UIntPtr]::Zero) }
                'XButton2' { [SwiftdockE2EInputCleanup]::mouse_event(256, 0, 0, 2, [UIntPtr]::Zero) }
            }
        }
        if ($null -ne $state.Cursor) { $null = [SwiftdockE2EInputCleanup]::SetCursorPos($state.Cursor.X, $state.Cursor.Y) }
    }
}

function Stop-E2EInstances {
    # Also finds an orphan whose scenario host was already killed or crashed.
    foreach ($group in @(Get-E2EInstances | Group-Object Profile)) {
        $configDirectory = $group.Name
        $exe = Join-Path $repoRoot 'Swiftdock/bin/Release/net10.0-windows10.0.19041.0/win-x64/Swiftdock.exe'
        $request = $null
        try {
            try { Add-Content -LiteralPath (Join-Path $output 'cleanup.log') -Value "$(Get-Date -Format o) --exit --config-dir $configDirectory" }
            catch { $cleanupErrors.Add("Could not write cleanup log: $($_.Exception.Message)") }
            $request = Start-Process -FilePath $exe -ArgumentList @('--exit', '--config-dir', ('"' + $configDirectory + '"')) -WindowStyle Hidden -PassThru
            $null = $request.Handle
            if (-not $request.WaitForExit(4000)) {
                $cleanupErrors.Add("--exit client timed out for $configDirectory; last-resort client kill.")
                $request.Kill(); $null = $request.WaitForExit(2000)
            }
            # A worker can finish shutdown after CIM enumeration: --exit then
            # returns 2 (no instance). The tracked-process checks below decide
            # whether cleanup failed; a timeout above always remains a failure.
        }
        catch { $cleanupErrors.Add("--exit failed for ${configDirectory}: $($_.Exception.Message)") }
        finally { if ($null -ne $request) { $request.Dispose() } }
        foreach ($item in $group.Group) {
            $owned = Get-Process -Id $item.Entry.ProcessId -ErrorAction SilentlyContinue
            if ($null -eq $owned) { continue }
            try {
                $null = $owned.Handle
                if ([Math]::Abs(($owned.StartTime.ToUniversalTime() - $item.Entry.CreationDate.ToUniversalTime()).TotalSeconds) -gt 1) { continue }
                if (-not $owned.WaitForExit(4000)) {
                    $cleanupErrors.Add("LAST-RESORT KILL: Swiftdock PID $($owned.Id), profile $configDirectory did not respond to --exit.")
                    $owned.Kill()
                    if (-not $owned.WaitForExit(2000)) { $cleanupErrors.Add("Swiftdock PID $($owned.Id) still running after kill.") }
                }
            }
            catch { $cleanupErrors.Add("Cleanup PID $($item.Entry.ProcessId): $($_.Exception.Message)") }
            finally { $owned.Dispose() }
        }
    }
}

Push-Location $repoRoot
try {
    & "$PSScriptRoot/dotnet.ps1" restore Swiftdock.E2E/Swiftdock.E2E.csproj --locked-mode --disable-parallel '-m:1' '-nr:false'
    if ($LASTEXITCODE -ne 0) { throw 'Locked E2E dependency restore failed.' }
    & "$PSScriptRoot/dotnet.ps1" build Swiftdock.E2E/Swiftdock.E2E.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'
    if ($LASTEXITCODE -ne 0) { throw 'Release E2E build failed.' }
    $env:SWIFTDOCK_E2E = '1'
    $env:SWIFTDOCK_E2E_RUN_ID = $runId
    $dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    # Discover compiled tests without running any scenario or touching UIA.
    $discovered = & $dotnet test Swiftdock.E2E/Swiftdock.E2E.csproj -c Release --no-build --no-restore --list-tests
    if ($LASTEXITCODE -ne 0) { throw 'E2E scenario discovery failed.' }
    $names = @($discovered | ForEach-Object {
        if ($_ -match '^\s+(Swiftdock\.E2E\.\w+\.\w+)(?:\(|\s*$)') { $Matches[1] }
    } | Sort-Object -Unique)
    $terms = @($Filter.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
    $selected = @($names | Where-Object {
        $name = $_
        $terms.Count -eq 0 -or @($terms | Where-Object { $name.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
    })
    if ($selected.Count -eq 0) { throw "No E2E scenarios match '$Filter'." }
    Write-Output 'Selected E2E scenarios (all data rows of each selected scenario):'
    $selected | ForEach-Object { Write-Output "  $_" }
    $testArguments = @('test', 'Swiftdock.E2E/Swiftdock.E2E.csproj', '-c', 'Release',
        '--no-build', '--no-restore', '--logger', '"trx;LogFileName=e2e.trx"', '--results-directory', 'artifacts/e2e')
    if ($terms.Count -gt 0) {
        $expression = ($selected | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
        $testArguments += @('--filter', ('"' + $expression + '"'))
    }
    $runner = Start-Process -FilePath $dotnet -ArgumentList $testArguments `
        -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $output 'e2e.stdout.log') -RedirectStandardError (Join-Path $output 'e2e.stderr.log')
    $null = $runner.Handle
    if (-not $runner.WaitForExit($TimeoutSeconds * 1000)) { throw "E2E exceeded the $TimeoutSeconds-second overall deadline." }
    if ($runner.ExitCode -ne 0) { throw "E2E exited with code $($runner.ExitCode); see artifacts/e2e." }
}
catch { $failure = $_.Exception.Message }
finally {
    # Independent of the test thread and UIA; run on success, failure, and timeout.
    try { Stop-TestHostTree } catch { $cleanupErrors.Add("Test-host cleanup: $($_.Exception.Message)") }
    try { Stop-RecordedHosts } catch { $cleanupErrors.Add("Recorded host cleanup: $($_.Exception.Message)") }
    try { if ($null -ne $runner -and -not $runner.HasExited) { $runner.Kill(); $null = $runner.WaitForExit(2000) } }
    catch { $cleanupErrors.Add("Runner cleanup: $($_.Exception.Message)") }
    try { Stop-E2EInstances } catch { $cleanupErrors.Add("Instance cleanup: $($_.Exception.Message)") }
    try { Release-RecordedInput } catch { $cleanupErrors.Add("Input cleanup: $($_.Exception.Message)") }
    if ($null -ne $runner) { $runner.Dispose() }
    $env:SWIFTDOCK_E2E = $previousOptIn
    $env:SWIFTDOCK_E2E_RUN_ID = $previousRunId
    Pop-Location
}
if ($cleanupErrors.Count -gt 0) {
    $cleanupErrors | Add-Content -LiteralPath (Join-Path $output 'cleanup.log')
    $failure = (@($failure) + @($cleanupErrors)) -join [Environment]::NewLine
}
if ($failure) { throw $failure }
Write-Output "E2E passed; all test-owned instances cleaned up. Results: $output"
