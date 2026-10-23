[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$conceptRoot = Join-Path $projectRoot 'docs/icon-concepts/2026-09-29'
$outputPath = Join-Path $projectRoot 'src/SCPSL.ModLaunch/Assets/modlaunch.ico'
$previewRoot = Join-Path $projectRoot 'artifacts/icon-review'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
[IO.Directory]::CreateDirectory($previewRoot) | Out-Null

# The approved smaller power symbol is readable at 32 px and above. Use the
# original larger glyph at the smallest Windows scales to preserve its shape.
$sizes = @(16,20,24,32,40,48,64,96,128,256)
$small = [Drawing.Bitmap]::new((Join-Path $conceptRoot '05-containment-teal-power.png'))
$large = [Drawing.Bitmap]::new((Join-Path $conceptRoot '07-containment-large-power.png'))
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $source = if ($size -lt 32) { $large } else { $small }
        $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($source,[Drawing.Rectangle]::new(0,0,$size,$size),0,0,$source.Width,$source.Height,[Drawing.GraphicsUnit]::Pixel,$attributes)
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            $frame = $stream.ToArray()
            $frames.Add($frame)
            [IO.File]::WriteAllBytes((Join-Path $previewRoot "modlaunch-$size.png"),$frame)
        } finally {
            $stream.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
        }
    }
} finally { $small.Dispose(); $large.Dispose() }

# ICO directory entries point to lossless PNG frames with 32-bit RGBA. A zero
# width/height byte represents 256 pixels in the Windows icon file format.
$output = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $writer.Flush()
    [IO.File]::WriteAllBytes($outputPath,$output.ToArray())
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output "Icon: $outputPath"
Write-Output "Sizes: $($sizes -join ', ')"
