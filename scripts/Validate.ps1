[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    & "$PSScriptRoot/dotnet.ps1" restore QuickLaunch.Tests/QuickLaunch.Tests.csproj --locked-mode --disable-parallel '-m:1' '-nr:false'
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & "$PSScriptRoot/dotnet.ps1" build QuickLaunch.Tests/QuickLaunch.Tests.csproj -c Release --no-restore --disable-build-servers '-m:1' '-warnaserror'
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & "$PSScriptRoot/dotnet.ps1" test QuickLaunch.Tests/QuickLaunch.Tests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=launcher.trx' --results-directory artifacts/tests
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally { Pop-Location }
