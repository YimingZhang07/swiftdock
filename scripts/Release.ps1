[CmdletBinding()]
param([switch]$SkipTests, [switch]$E2E)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'QuickLaunch/QuickLaunch.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Expected a three-part release Version in QuickLaunch.csproj, got '$version'." }
$compiler = Join-Path $repoRoot '.tools/innosetup/ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup is missing. Run scripts/Setup.ps1 first.' }
$artifacts = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$staging = Join-Path $artifacts "staging/$version"
$output = Join-Path $artifacts "release/$version"

function Reset-ReleaseDirectory([string]$directory) {
    $target = [IO.Path]::GetFullPath($directory)
    if (-not $target.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a directory outside artifacts: $target"
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

Push-Location $repoRoot
try {
    if (-not $SkipTests) { & "$PSScriptRoot/Validate.ps1" }
    if ($E2E) { & "$PSScriptRoot/E2E.ps1" }
    Reset-ReleaseDirectory $staging
    # The optional single-file analyzer adds an ILLink NuGet dependency. Keep the
    # existing lock file and package set; deployment paths are audited in source.
    & "$PSScriptRoot/dotnet.ps1" publish QuickLaunch/QuickLaunch.csproj -c Release -r win-x64 --self-contained true `
        --disable-build-servers '-m:1' '-warnaserror' '-p:RestoreLockedMode=true' `
        '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableSingleFileAnalyzer=false' `
        '-p:EnableCompressionInSingleFile=false' '-p:PublishReadyToRun=false' '-p:DebugType=None' '-p:DebugSymbols=false' -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
    $exe = Join-Path $staging 'QuickLaunch.exe'
    $published = @(Get-ChildItem -LiteralPath $staging -Force)
    if ($published.Count -ne 1 -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Publish must produce only QuickLaunch.exe. Inspect $staging"
    }

    Reset-ReleaseDirectory $output
    & $compiler "/DAppVersion=$version" "/DPublishDir=$staging" "/DReleaseDir=$output" (Join-Path $repoRoot 'installer/QuickLaunch.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    $setup = Join-Path $output "QuickLaunch-$version-Setup.exe"
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Installer was not created: $setup" }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = Join-Path $output "QuickLaunch-$version-Portable.zip"
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $exe, 'QuickLaunch/QuickLaunch.exe', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $zip.CreateEntry('QuickLaunch/Data/') | Out-Null
    }
    finally { $zip.Dispose() }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $data = $zip.GetEntry('QuickLaunch/Data/')
        $binary = $zip.GetEntry('QuickLaunch/QuickLaunch.exe')
        if ($zip.Entries.Count -ne 2 -or $null -eq $data -or $data.Length -ne 0 -or
            $null -eq $binary -or $binary.Length -ne (Get-Item -LiteralPath $exe).Length) {
            throw 'Portable ZIP verification failed: expected QuickLaunch.exe and an empty Data/ entry.'
        }
    }
    finally { $zip.Dispose() }
    $sums = Join-Path $output 'SHA256SUMS.txt'
    @($setup, $archive) | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
        $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_)
    } | Set-Content -LiteralPath $sums -Encoding ASCII
    Get-Item -LiteralPath $setup, $archive, $sums | Select-Object FullName, Length | Format-Table -AutoSize | Out-String -Width 240 | Write-Output
    Write-Output "Published executable for Benchmark.ps1 / MeasureStartup.ps1: $exe"
}
finally { Pop-Location }
