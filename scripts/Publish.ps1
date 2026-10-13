[CmdletBinding()]
param([switch]$FrameworkDependent, [switch]$ReadyToRun)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$variant = if ($FrameworkDependent) { 'framework-dependent' } else { 'standalone' }
if ($ReadyToRun) { $variant += '-r2r' }
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'QuickLaunch/QuickLaunch.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$releaseName = 'QuickLaunch-' + $version + '-win-x64-' + $variant + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$destination = Join-Path $repoRoot ('artifacts/releases/' + $releaseName)
$selfContained = (-not $FrameworkDependent).ToString().ToLowerInvariant()
$r2r = $ReadyToRun.IsPresent.ToString().ToLowerInvariant()
Push-Location $repoRoot
try {
    & "$PSScriptRoot/dotnet.ps1" publish QuickLaunch/QuickLaunch.csproj -c Release -r win-x64 --self-contained $selfContained --disable-build-servers '-m:1' '-warnaserror' "-p:PublishReadyToRun=$r2r" '-p:RestoreLockedMode=true' -o $destination
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if ($ReadyToRun) { Set-Content -LiteralPath (Join-Path $destination 'r2r.flag') -Value 'PublishReadyToRun=true' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/QUICKSTART.zh-CN.md') -Destination (Join-Path $destination 'README.md')
    $archive = $destination + '.zip'
    Compress-Archive -LiteralPath $destination -DestinationPath $archive -CompressionLevel Optimal
    $checksum = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    Set-Content -LiteralPath ($archive + '.sha256') -Value ($checksum.Hash + '  ' + [IO.Path]::GetFileName($archive))
    $manifest = [pscustomobject]@{ Directory=$destination; Executable=(Join-Path $destination 'QuickLaunch.exe'); Archive=$archive; Sha256=$checksum.Hash }
    if (-not $FrameworkDependent -and -not $ReadyToRun) { $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/releases/latest.json') -Encoding utf8 }
    $manifest | ConvertTo-Json
}
finally { Pop-Location }
