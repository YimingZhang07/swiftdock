[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$previousOptIn = $env:QUICKLAUNCH_E2E
Push-Location $repoRoot
try {
    & "$PSScriptRoot/dotnet.ps1" restore QuickLaunch.E2E/QuickLaunch.E2E.csproj --locked-mode --disable-parallel '-m:1' '-nr:false'
    if ($LASTEXITCODE -ne 0) { throw 'Locked E2E dependency restore failed.' }
    & "$PSScriptRoot/dotnet.ps1" build QuickLaunch.E2E/QuickLaunch.E2E.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'
    if ($LASTEXITCODE -ne 0) { throw 'Release E2E build failed.' }
    # Real input is deliberate; the tests also check for an unlocked interactive input desktop.
    $env:QUICKLAUNCH_E2E = '1'
    & "$PSScriptRoot/dotnet.ps1" test QuickLaunch.E2E/QuickLaunch.E2E.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=e2e.trx' --results-directory artifacts/e2e
    if ($LASTEXITCODE -ne 0) { throw 'E2E failed; see artifacts/e2e and the retained temp profile path in TRX.' }
}
finally {
    $env:QUICKLAUNCH_E2E = $previousOptIn
    Pop-Location
}
