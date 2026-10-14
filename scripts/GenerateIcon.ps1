[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    & powershell.exe -NoProfile -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
    return
}
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
[Windows.Media.RenderOptions]::ProcessRenderMode = [Windows.Interop.RenderMode]::SoftwareOnly
$repoRoot = Split-Path $PSScriptRoot -Parent
$iconPath = Join-Path $repoRoot 'QuickLaunch/Assets/QuickLaunch.ico'
$previewPath = Join-Path $repoRoot 'artifacts/icon/preview.png'
New-Item -ItemType Directory -Force -Path (Split-Path $iconPath), (Split-Path $previewPath) | Out-Null

function Brush([string]$color) {
    return [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($color))
}

function Render-Icon([int]$size) {
    # inset, background radius, first/second cell origin, tile edge/radius, circle radius
    $hint = switch ($size) {
        16 { @(0, 3, 3, 9, 4, 0, 2) }
        20 { @(0, 4, 4, 11, 5, 0, 2.5) }
        24 { @(1, 5, 5, 13, 6, 0, 3) }
        32 { @(1, 7, 7, 17, 8, 2, 4.2) }
        40 { @(1, 9, 9, 21, 10, 2.5, 5.2) }
        48 { @(2, 10, 11, 25, 12, 3, 6.2) }
        64 { @(2, 14, 15, 34, 15, 4, 8) }
        256 { @(8, 56, 60, 136, 60, 16, 32) }
    }
    $inset, $radius, $first, $second, $edge, $tileRadius, $circleRadius = $hint
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $background = if ($size -le 20) { Brush '#2A73E3' } else {
            [Windows.Media.LinearGradientBrush]::new(
                [Windows.Media.ColorConverter]::ConvertFromString('#3A8BEF'),
                [Windows.Media.ColorConverter]::ConvertFromString('#1A5DC8'), 90)
        }
        $drawing.DrawRoundedRectangle($background, $null,
            [Windows.Rect]::new($inset, $inset, $size - 2 * $inset, $size - 2 * $inset), $radius, $radius)
        $white = Brush '#FFFFFF'
        $muted = Brush $(switch ($size) {
            16 { '#B8FFFFFF' }
            20 { '#B3FFFFFF' }
            { $_ -eq 24 -or $_ -eq 32 } { '#A6FFFFFF' }
            default { '#8CFFFFFF' }
        })
        $drawing.DrawRoundedRectangle($white, $null, [Windows.Rect]::new($first, $first, $edge, $edge), $tileRadius, $tileRadius)
        $drawing.DrawRoundedRectangle($muted, $null, [Windows.Rect]::new($second, $first, $edge, $edge), $tileRadius, $tileRadius)
        $drawing.DrawRoundedRectangle($muted, $null, [Windows.Rect]::new($first, $second, $edge, $edge), $tileRadius, $tileRadius)
        $center = $second + $edge / 2.0
        if ($size -eq 16) {
            $disc = Brush '#A9DCFF'
            $corner = Brush '#73A9DCFF'
            $drawing.DrawRectangle($disc, $null, [Windows.Rect]::new(9, 10, 4, 2))
            $drawing.DrawRectangle($disc, $null, [Windows.Rect]::new(10, 9, 2, 4))
            foreach ($x in @(9, 12)) {
                foreach ($y in @(9, 12)) { $drawing.DrawRectangle($corner, $null, [Windows.Rect]::new($x, $y, 1, 1)) }
            }
        }
        else {
            $disc = Brush $(if ($size -le 24) { '#A9DCFF' } else { '#9BD4FF' })
            $drawing.DrawEllipse($disc, $null, [Windows.Point]::new($center, $center), $circleRadius, $circleRadius)
        }
    }
    finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    return $bitmap
}

function Encode-Png($bitmap) {
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() }
    finally { $stream.Dispose() }
}

function Encode-Dib($bitmap) {
    $size = $bitmap.PixelWidth
    $stride = $size * 4
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $pixels = [byte[]]::new($stride * $size)
    $straight = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $straight.CopyPixels($pixels, $stride, 0)
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint32]40)
        $writer.Write([int]$size)
        $writer.Write([int]($size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)
        $writer.Write([uint32](($stride + $maskStride) * $size))
        1..4 | ForEach-Object { $writer.Write([uint32]0) }
        for ($y = $size - 1; $y -ge 0; $y--) { $writer.Write($pixels, $y * $stride, $stride) }
        for ($y = $size - 1; $y -ge 0; $y--) {
            $mask = [byte[]]::new($maskStride)
            for ($x = 0; $x -lt $size; $x++) {
                if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) {
                    $index = [int][Math]::Floor($x / 8.0)
                    $mask[$index] = $mask[$index] -bor (0x80 -shr ($x % 8))
                }
            }
            $writer.Write($mask)
        }
        return ,$stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$bitmaps = @{}
$payloads = @{}
foreach ($size in $sizes) {
    $bitmaps[$size] = Render-Icon $size
    $payloads[$size] = if ($size -eq 256) { Encode-Png $bitmaps[$size] } else { Encode-Dib $bitmaps[$size] }
}
$output = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($size in $sizes) {
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$payloads[$size].Length)
        $writer.Write([uint32]$offset)
        $offset += $payloads[$size].Length
    }
    foreach ($size in $sizes) { $writer.Write([byte[]]$payloads[$size]) }
}
finally { $writer.Dispose(); $output.Dispose() }

function Draw-Label($drawing, [string]$label, [double]$x, [double]$y, $foreground) {
    $text = [Windows.Media.FormattedText]::new($label, [Globalization.CultureInfo]::InvariantCulture,
        [Windows.FlowDirection]::LeftToRight, [Windows.Media.Typeface]::new('Segoe UI'), 13, $foreground, 1.0)
    $drawing.DrawText($text, [Windows.Point]::new($x, $y))
}

function Zoom-Nearest($frame) {
    $size = $frame.PixelWidth
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $source = [Windows.Media.Imaging.FormatConvertedBitmap]::new($frame, [Windows.Media.PixelFormats]::Pbgra32, $null, 0)
    $source.CopyPixels($pixels, $stride, 0)
    $zoomStride = $stride * 4
    $zoom = [byte[]]::new($zoomStride * $size * 4)
    for ($y = 0; $y -lt $size * 4; $y++) {
        for ($x = 0; $x -lt $size * 4; $x++) {
            $sourceOffset = [int][Math]::Floor($y / 4.0) * $stride + [int][Math]::Floor($x / 4.0) * 4
            [Array]::Copy($pixels, $sourceOffset, $zoom, $y * $zoomStride + $x * 4, 4)
        }
    }
    return [Windows.Media.Imaging.BitmapSource]::Create($size * 4, $size * 4, 96, 96, [Windows.Media.PixelFormats]::Pbgra32, $null, $zoom, $zoomStride)
}

# Preview the packed ICO after decoding, including its alpha and AND masks.
$stream = [IO.File]::OpenRead($iconPath)
try { $decoded = [Windows.Media.Imaging.IconBitmapDecoder]::new($stream, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad) }
finally { $stream.Dispose() }
$visual = [Windows.Media.DrawingVisual]::new()
[Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [Windows.Media.BitmapScalingMode]::NearestNeighbor)
$drawing = $visual.RenderOpen()
try {
    for ($strip = 0; $strip -lt 2; $strip++) {
        $top = $strip * 320
        $foreground = if ($strip -eq 0) { Brush '#202020' } else { Brush '#F3F3F3' }
        $background = if ($strip -eq 0) { Brush '#F3F3F3' } else { Brush '#202020' }
        $drawing.DrawRectangle($background, $null, [Windows.Rect]::new(0, $top, 760, 320))
        Draw-Label $drawing 'Native pixels / 1:1' 20 ($top + 12) $foreground
        $x = 20
        foreach ($frame in $decoded.Frames | Sort-Object PixelWidth) {
            $size = $frame.PixelWidth
            Draw-Label $drawing "$size" $x ($top + 38) $foreground
            $drawing.DrawImage($frame, [Windows.Rect]::new($x, $top + 60, $size, $size))
            $x += $size + 28
        }
    }
    for ($strip = 0; $strip -lt 2; $strip++) {
        $top = 640 + $strip * 196
        $foreground = if ($strip -eq 0) { Brush '#202020' } else { Brush '#F3F3F3' }
        $background = if ($strip -eq 0) { Brush '#F3F3F3' } else { Brush '#202020' }
        $drawing.DrawRectangle($background, $null, [Windows.Rect]::new(0, $top, 760, 196))
        Draw-Label $drawing '4x / nearest neighbour' 20 ($top + 12) $foreground
        $x = 20
        foreach ($frame in $decoded.Frames | Where-Object PixelWidth -LE 32 | Sort-Object PixelWidth) {
            $size = $frame.PixelWidth
            Draw-Label $drawing "$size px" $x ($top + 38) $foreground
            $drawing.DrawImage((Zoom-Nearest $frame), [Windows.Rect]::new($x, $top + 60, $size * 4, $size * 4))
            $x += 180
        }
    }
}
finally { $drawing.Close() }
$preview = [Windows.Media.Imaging.RenderTargetBitmap]::new(760, 1032, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$preview.Render($visual)
[IO.File]::WriteAllBytes($previewPath, (Encode-Png $preview))
Write-Host "Generated $iconPath (16, 20, 24, 32, 40, 48, 64 DIB; 256 PNG)"
Write-Host "Preview: $previewPath"
