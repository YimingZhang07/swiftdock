[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Executable, [ValidateRange(1,30)][int]$Count = 10)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $repoRoot '.tools/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$output = Join-Path $repoRoot ('artifacts/startup/' + [Guid]::NewGuid().ToString('N'))
$values = @()
for ($index = 0; $index -lt $Count; $index++) {
    $profile = Join-Path $output ('profile-' + $index)
    $logs = Join-Path $output ('logs-' + $index)
    $arguments = '--startup-probe --silent --config-dir "' + $profile + '" --diagnostics-dir "' + $logs + '"'
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Startup probe failed: $logs" }
    $entry = Get-ChildItem -LiteralPath $logs -Filter '*.jsonl' | Get-Content | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object name -EQ startup | Select-Object -First 1
    $values += [double]$entry.data.processToReadyMs
}
$sorted = @($values | Sort-Object)
$report = [pscustomobject]@{ Executable=$Executable; Samples=$values; P50=$sorted[[Math]::Ceiling($Count*.5)-1]; P95=$sorted[[Math]::Ceiling($Count*.95)-1]; Max=($sorted | Select-Object -Last 1); ColdBootMeasured=$false; Note='Process launches; filesystem cache is not cleared. Process start to tray/registration handling.' }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'startup.json') -Encoding utf8
$report | ConvertTo-Json -Depth 5
Write-Output "Startup report: $output"
