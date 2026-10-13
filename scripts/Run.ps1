[CmdletBinding()]
param([switch]$Smoke, [switch]$Silent, [string]$ConfigDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $repoRoot '.tools/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$exe = Join-Path $repoRoot 'QuickLaunch/bin/Release/net10.0-windows10.0.19041.0/win-x64/QuickLaunch.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Release with scripts/dotnet.ps1 first.' }
$diagnostics = Join-Path $repoRoot 'artifacts/validation'
$appArguments = '--diagnostics-dir "' + $diagnostics + '"'
if ($Smoke) {
    if ($ConfigDirectory) { throw 'Smoke tests always use a fresh isolated profile; omit ConfigDirectory.' }
    $ConfigDirectory = Join-Path $diagnostics ('profiles/smoke-' + [Guid]::NewGuid().ToString('N'))
    $appArguments += ' --smoke-test'
}
if ($ConfigDirectory) { $appArguments += ' --config-dir "' + [System.IO.Path]::GetFullPath($ConfigDirectory) + '"' }
if ($Silent) { $appArguments += ' --silent' }
# Launch the apphost so asInvoker and PerMonitorV2 in its manifest take effect.
$windowStyle = if ($Smoke -or $Silent) { 'Hidden' } else { 'Normal' }
$appProcess = Start-Process -FilePath $exe -ArgumentList $appArguments -WindowStyle $windowStyle -PassThru -Wait
if ($Smoke -and $appProcess.ExitCode -eq 0) {
    $report = Get-Content -LiteralPath (Join-Path $diagnostics 'smoke-result.json') -Raw | ConvertFrom-Json
    if (-not $report.passed -or [System.IO.Path]::GetFullPath($report.profileDirectory) -ne [System.IO.Path]::GetFullPath($ConfigDirectory)) {
        throw 'Smoke did not produce a passing report for this isolated profile.'
    }
}
exit $appProcess.ExitCode
