[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Executable, [ValidateRange(1,30)][int]$Count = 10, [switch]$AllowUnavailableTray)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ReadDiagnostics.ps1"
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $repoRoot '.tools/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$output = Join-Path $repoRoot ('artifacts/startup/' + [Guid]::NewGuid().ToString('N'))
$values = @()
for ($index = 0; $index -lt $Count; $index++) {
    $profile = Join-Path $output ('profile-' + $index)
    $logs = Join-Path $output ('logs-' + $index)
    $arguments = '--startup-probe --silent --config-dir "' + $profile + '" --diagnostics-dir "' + $logs + '"'
    if ($AllowUnavailableTray) { $arguments += ' --allow-unavailable-tray' }
    $startedAfter = [DateTime]::UtcNow
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Startup probe failed: $logs" }
    $events = @(Read-QuickLaunchSession -Directory $logs -ProcessId $process.Id -StartedAfter $startedAfter)
    $entry = $events | Where-Object { $_.EventId.Name -eq 'startup' } | Select-Object -First 1
    if ($null -eq $entry -or $null -eq $entry.processToReadyMs) { throw "Startup CLEF event missing: $logs" }
    $values += [double]$entry.processToReadyMs
}
$sorted = @($values | Sort-Object)
$report = [pscustomobject]@{ Executable=$Executable; Samples=$values; P50=$sorted[[Math]::Ceiling($Count*.5)-1]; P95=$sorted[[Math]::Ceiling($Count*.95)-1]; Max=($sorted | Select-Object -Last 1); ColdBootMeasured=$false; AllowUnavailableTray=[bool]$AllowUnavailableTray; Note='Process launches; filesystem cache is not cleared. Process start to tray/registration handling.' }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'startup.json') -Encoding utf8
$report | ConvertTo-Json -Depth 5
Write-Output "Startup report: $output"
