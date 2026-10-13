[CmdletBinding()]
param([switch]$SingleGroup, [switch]$Interactive, [switch]$Prewarm, [ValidateRange(0,300)][int]$IdleSeconds = 0, [string]$Executable)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $repoRoot '.tools/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
if (-not $Executable) { $Executable = Join-Path $repoRoot 'QuickLaunch/bin/Release/net10.0-windows10.0.19041.0/win-x64/QuickLaunch.exe' }
$output = Join-Path $repoRoot ('artifacts/benchmarks/' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$profile = Join-Path $output 'profile'
New-Item -ItemType Directory -Force -Path $profile | Out-Null
Set-Content -LiteralPath (Join-Path $profile 'fixture.txt') -Value 'QuickLaunch benchmark fixture'
$shellLinkFactory = New-Object -ComObject WScript.Shell
$shortcut = $shellLinkFactory.CreateShortcut((Join-Path $profile 'fixture.lnk'))
$shortcut.TargetPath = Join-Path $env:WINDIR 'System32/notepad.exe'
$shortcut.Save()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shellLinkFactory)
$appArguments = '--benchmark --config-dir "' + $profile + '" --diagnostics-dir "' + $output + '" --idle-seconds ' + $IdleSeconds
if ($SingleGroup) { $appArguments += ' --single-group' }
if ($Interactive) { $appArguments += ' --benchmark-interactive' }
if ($Prewarm) { $appArguments += ' --prewarm' }
$windowStyle = if ($Interactive) { 'Normal' } else { 'Hidden' }
$appProcess = Start-Process -FilePath $Executable -ArgumentList $appArguments -WindowStyle $windowStyle -PassThru -Wait
if ($appProcess.ExitCode -ne 0) { throw "Benchmark failed; inspect $output" }
Get-Content -LiteralPath (Join-Path $output 'benchmark.json') -Raw
Write-Output "Benchmark output: $output"
