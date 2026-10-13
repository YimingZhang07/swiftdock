[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sdkVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$sdkRoot = Join-Path $repoRoot '.tools/dotnet'
$dotnetExe = Join-Path $sdkRoot 'dotnet.exe'
if (Test-Path -LiteralPath (Join-Path $sdkRoot "sdk/$sdkVersion")) {
    & $dotnetExe --version
    exit $LASTEXITCODE
}
$metadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$sdk = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -EQ $sdkVersion | Select-Object -First 1
$file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') } | Select-Object -First 1
if (-not $file) { throw "Official SDK metadata does not contain $sdkVersion win-x64." }
$downloadRoot = Join-Path $repoRoot '.tools/downloads'
New-Item -ItemType Directory -Force -Path $downloadRoot, $sdkRoot | Out-Null
$archive = Join-Path $downloadRoot "dotnet-sdk-$sdkVersion-win-x64.zip"
if (-not (Test-Path -LiteralPath $archive)) {
    & curl.exe --fail --location --retry 2 --output $archive $file.url
    if ($LASTEXITCODE -ne 0) { throw 'SDK download failed.' }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file.hash) {
    throw "SDK checksum mismatch. Remove the incomplete archive at $archive and retry."
}
& tar.exe -xf $archive -C $sdkRoot
if ($LASTEXITCODE -ne 0) { throw 'SDK extraction failed.' }
& $dotnetExe --info
if ($LASTEXITCODE -ne 0) { throw 'Installed SDK failed to run.' }
