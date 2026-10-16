[CmdletBinding()]
param([ValidateRange(1, 600)][int]$TimeoutSeconds = 90)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repoRoot 'artifacts/validation'
$manifest = Join-Path $output 'snapshots.json'
$process = $null
$timedOut = $false
$previousRuntime = $env:DOTNET_ROOT_X64
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Start a fresh manifest even if restore, build or process startup fails.
$initial = [pscustomobject]@{
    expected = @(); rendered = @(); failed = @(); current = $null
    completed = $false; timedOut = $false; generatedAt = [DateTimeOffset]::Now.ToString('o')
}
$initial | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8

function Stop-Renderer {
    if ($null -ne $process -and -not $process.HasExited) {
        # The coordinator owns a kill-on-close Windows job inherited by every
        # image worker. Stopping this exact PID also removes its workers/HWNDs.
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        if (-not $process.WaitForExit(5000)) { throw "Could not stop snapshot renderer PID $($process.Id)." }
    }
}

function Record-Failure([string]$reason) {
    try { $report = Get-Content -LiteralPath $manifest -Raw -Encoding utf8 | ConvertFrom-Json }
    catch { $report = $initial }
    $current = $report.current
    $failed = @($report.failed)
    $accounted = @($report.rendered) + @($failed | ForEach-Object { $_.image })
    foreach ($file in @($report.expected)) {
        if ($file -notin $accounted) {
            $detail = if ($file -eq $current) { $reason } else { "Not rendered: $reason" }
            $failed += [pscustomobject]@{ image = $file; error = $detail }
        }
    }
    if ($failed.Count -eq 0) { $failed += [pscustomobject]@{ image = $current; error = $reason } }
    $report.failed = $failed
    $report.completed = $true
    $report.timedOut = $timedOut
    $report | Add-Member -NotePropertyName interruptedImage -NotePropertyValue $current -Force
    $report | Add-Member -NotePropertyName updatedAt -NotePropertyValue ([DateTimeOffset]::Now.ToString('o')) -Force
    $report.current = $null
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8
    return $current
}

Push-Location $repoRoot
try {
    & "$PSScriptRoot/dotnet.ps1" restore QuickLaunch.Snapshots/QuickLaunch.Snapshots.csproj --locked-mode --disable-parallel '-m:1' '-nr:false'
    if ($LASTEXITCODE -ne 0) { throw 'Locked snapshot-tool restore failed.' }
    & "$PSScriptRoot/dotnet.ps1" build QuickLaunch.Snapshots/QuickLaunch.Snapshots.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'
    if ($LASTEXITCODE -ne 0) { throw 'Snapshot-tool build failed.' }
    $env:DOTNET_ROOT_X64 = Join-Path $repoRoot '.tools/dotnet'
    $exe = Join-Path $repoRoot 'QuickLaunch.Snapshots/bin/Release/net10.0-windows10.0.19041.0/QuickLaunch.Snapshots.exe'
    $process = Start-Process -FilePath $exe -ArgumentList ('"' + $output + '"') -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $output 'snapshots.stdout.log') -RedirectStandardError (Join-Path $output 'snapshots.stderr.log')
    $null = $process.Handle # Keep the handle so Windows PowerShell can read ExitCode after waiting.
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        Stop-Renderer
        throw "Snapshot renderer exceeded $TimeoutSeconds seconds."
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "Snapshot renderer exited with code $($process.ExitCode); see snapshots.json and snapshots.stderr.log." }
    $report = Get-Content -LiteralPath $manifest -Raw -Encoding utf8 | ConvertFrom-Json
    $expected = @($report.expected)
    $rendered = @($report.rendered)
    $missing = @($expected | Where-Object { $_ -notin $rendered })
    if (-not $report.completed -or $expected.Count -eq 0 -or $rendered.Count -ne $expected.Count -or $missing.Count -ne 0 -or @($report.failed).Count -ne 0) {
        throw "Snapshot renderer did not finish the $($expected.Count)-image matrix; see snapshots.json."
    }
    Write-Output "Rendered $($rendered.Count) snapshots: $output"
}
catch {
    $reason = $_.Exception.Message
    try { Stop-Renderer } catch { $reason += ' ' + $_.Exception.Message }
    $current = Record-Failure $reason
    if ($current) { $reason += " Current image: $current." }
    throw "$reason Partial results: $manifest"
}
finally {
    if ($null -ne $process) { $process.Dispose() }
    $env:DOTNET_ROOT_X64 = $previousRuntime
    Pop-Location
}
