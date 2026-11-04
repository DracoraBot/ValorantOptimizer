param(
    [string]$OutputDirectory = 'assets'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $root $OutputDirectory
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$pngPath = Join-Path $outputRoot 'app-icon.png'
$icoPath = Join-Path $outputRoot 'app.ico'

$size = 256
$bitmap = New-Object System.Drawing.Bitmap($size, $size)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

# Rounded-square background.
$radius = 54
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$diameter = $radius * 2
$path.AddArc(8, 8, $diameter, $diameter, 180, 90)
$path.AddArc($size - 8 - $diameter, 8, $diameter, $diameter, 270, 90)
$path.AddArc($size - 8 - $diameter, $size - 8 - $diameter, $diameter, $diameter, 0, 90)
$path.AddArc(8, $size - 8 - $diameter, $diameter, $diameter, 90, 90)
$path.CloseFigure()

$background = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 15, 17, 21))
$graphics.FillPath($background, $path)
$border = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 38, 46, 61), 3)
$graphics.DrawPath($border, $path)

# Crosshair mark.
$accent = [System.Drawing.Color]::FromArgb(255, 255, 70, 85)
$pen = New-Object System.Drawing.Pen($accent, 9)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$center = $size / 2
$graphics.DrawEllipse($pen, 66, 66, 124, 124)
$graphics.DrawLine($pen, $center, 30, $center, 58)
$graphics.DrawLine($pen, $center, 198, $center, 226)
$graphics.DrawLine($pen, 30, $center, 58, $center)
$graphics.DrawLine($pen, 198, $center, 226, $center)
$dot = New-Object System.Drawing.SolidBrush($accent)
$graphics.FillEllipse($dot, $center - 9, $center - 9, 18, 18)

$graphics.Dispose()
$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

# ICO container with a single 256x256 PNG payload (Windows Vista+).
$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)          # reserved
$writer.Write([UInt16]1)          # type: icon
$writer.Write([UInt16]1)          # image count
$writer.Write([Byte]0)            # width 256
$writer.Write([Byte]0)            # height 256
$writer.Write([Byte]0)            # palette colors
$writer.Write([Byte]0)            # reserved
$writer.Write([UInt16]1)          # planes
$writer.Write([UInt16]32)         # bits per pixel
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)         # payload offset
$writer.Write($pngBytes)
$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Output "Wrote $pngPath"
Write-Output "Wrote $icoPath"
