# Forward arguments to the repository-local SDK without modifying machine settings.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $repoRoot '.tools/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/packages'
$dotnetExe = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { throw 'Run scripts/Setup.ps1 first.' }
& $dotnetExe @args
exit $LASTEXITCODE
