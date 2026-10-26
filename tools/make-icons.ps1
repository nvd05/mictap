# Generates the app icons from one vector design: SVG sources and multi-size .ico files.
# Usage: powershell -NoProfile -File tools/make-icons.ps1 [-OutDir src/MicTap.App/Assets]
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\MicTap.App\Assets'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

# The design on a 256x256 canvas; the SVG and the WPF drawing below use the same numbers.
$Gradient = @('#7F67BE', '#533D8C')
$MicStroke = 'M66,118 A62,62 0 0 0 190,118 M128,180 V212 M92,212 H164'
$RecordRed = '#FF5252'

function Get-Svg([bool]$recording) {
    $badge = if ($recording) { "`n  <circle cx=`"204`" cy=`"52`" r=`"40`" fill=`"$RecordRed`" stroke=`"#FFFFFF`" stroke-width=`"12`"/>" } else { '' }
    @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="$($Gradient[0])"/>
      <stop offset="1" stop-color="$($Gradient[1])"/>
    </linearGradient>
  </defs>
  <rect width="256" height="256" rx="56" fill="url(#bg)"/>
  <rect x="98" y="40" width="60" height="112" rx="30" fill="#FFFFFF"/>
  <path d="$MicStroke" fill="none" stroke="#FFFFFF" stroke-width="16" stroke-linecap="round"/>$badge
</svg>
"@
}

function Get-Drawing([bool]$recording) {
    $color = { param($hex) [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
    $white = [System.Windows.Media.Brushes]::White
    $group = New-Object System.Windows.Media.DrawingGroup

    $background = New-Object System.Windows.Media.LinearGradientBrush((& $color $Gradient[0]), (& $color $Gradient[1]), (New-Object System.Windows.Point 0, 0), (New-Object System.Windows.Point 1, 1))
    $square = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect 0, 0, 256, 256), 56, 56)
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing($background, $null, $square)))

    $capsule = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect 98, 40, 60, 112), 30, 30)
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing($white, $null, $capsule)))

    $pen = New-Object System.Windows.Media.Pen($white, 16)
    $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing($null, $pen, [System.Windows.Media.Geometry]::Parse($MicStroke))))

    if ($recording) {
        $dot = New-Object System.Windows.Media.EllipseGeometry((New-Object System.Windows.Point 204, 52), 40, 40)
        $red = New-Object System.Windows.Media.SolidColorBrush(& $color $RecordRed)
        $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing($red, (New-Object System.Windows.Media.Pen($white, 12)), $dot)))
    }

    $group
}

function Get-Bitmap([System.Windows.Media.Drawing]$drawing, [int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform((New-Object System.Windows.Media.ScaleTransform(($size / 256.0), ($size / 256.0))))
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap
}

function Get-PngBytes($bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    $stream.ToArray()
}

# Classic DIB icon image: 32-bit BGRA bottom-up rows with straight alpha, then an empty AND mask.
function Get-DibBytes($bitmap, [int]$size) {
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $bitmap.CopyPixels($pixels, $stride, 0)
    $maskStride = [int][Math]::Ceiling($size / 32.0) * 4
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    # The comma binds tighter than arithmetic in PowerShell, hence the parentheses.
    foreach ($value in @(40, $size, ($size * 2))) { $writer.Write([int]$value) }
    $writer.Write([int16]1); $writer.Write([int16]32)
    foreach ($value in @(0, (($stride + $maskStride) * $size), 0, 0, 0, 0)) { $writer.Write([int]$value) }
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $i = $y * $stride + $x * 4
            $alpha = $pixels[$i + 3]
            for ($c = 0; $c -lt 3; $c++) {
                $value = if ($alpha -eq 0) { 0 } else { [Math]::Min(255, [int][Math]::Round($pixels[$i + $c] * 255.0 / $alpha)) }
                $writer.Write([byte]$value)
            }
            $writer.Write([byte]$alpha)
        }
    }
    $writer.Write((New-Object byte[] ($maskStride * $size)))
    $writer.Flush()
    $stream.ToArray()
}

function Write-Icon([System.Windows.Media.Drawing]$drawing, [string]$path) {
    # Small sizes as DIB for every icon API, 256 as PNG as Windows expects.
    $images = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
        $bitmap = Get-Bitmap $drawing $size
        $bytes = if ($size -eq 256) { Get-PngBytes $bitmap } else { Get-DibBytes $bitmap $size }
        [pscustomobject]@{ Size = $size; Bytes = $bytes }
    }

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $edge = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$edge); $writer.Write([byte]$edge); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([int16]1); $writer.Write([int16]32)
        $writer.Write([int]$image.Bytes.Length); $writer.Write([int]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($path, $stream.ToArray())
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
foreach ($variant in @(@{ Name = 'mictap'; Recording = $false }, @{ Name = 'mictap-recording'; Recording = $true })) {
    $svg = Join-Path $OutDir "$($variant.Name).svg"
    [System.IO.File]::WriteAllText($svg, (Get-Svg $variant.Recording) + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Icon (Get-Drawing $variant.Recording) (Join-Path $OutDir "$($variant.Name).ico")
    "written $($variant.Name).svg, $($variant.Name).ico"
}
