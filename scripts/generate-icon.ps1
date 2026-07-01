$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot "src\CodexQuotaWidget.App\Assets"
$icoPath = Join-Path $assetsDir "app.ico"
$previewPath = Join-Path $assetsDir "app-256.png"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

Add-Type -AssemblyName System.Drawing

function New-IconPngBytes {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = $Size / 256.0
    $rect = New-Object System.Drawing.RectangleF (18 * $scale), (18 * $scale), (220 * $scale), (220 * $scale)
    $radius = 54 * $scale
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 18, 26, 35)), ([System.Drawing.Color]::FromArgb(255, 8, 83, 92)), 135
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 103, 232, 214)), (8 * $scale)
    $g.DrawPath($pen, $path)

    $fontSize = [Math]::Max(10, 118 * $scale)
    $font = New-Object System.Drawing.Font "Segoe UI Variable Display", $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 248, 252, 255))
    $textRect = New-Object System.Drawing.RectangleF (0), (-10 * $scale), $Size, ($Size * 0.88)
    $g.DrawString("C", $font, $textBrush, $textRect, $format)

    $barBack = New-Object System.Drawing.RectangleF (62 * $scale), (189 * $scale), (132 * $scale), (16 * $scale)
    $barPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $barRadius = 8 * $scale
    $barDiameter = $barRadius * 2
    $barPath.AddArc($barBack.X, $barBack.Y, $barDiameter, $barDiameter, 180, 90)
    $barPath.AddArc($barBack.Right - $barDiameter, $barBack.Y, $barDiameter, $barDiameter, 270, 90)
    $barPath.AddArc($barBack.Right - $barDiameter, $barBack.Bottom - $barDiameter, $barDiameter, $barDiameter, 0, 90)
    $barPath.AddArc($barBack.X, $barBack.Bottom - $barDiameter, $barDiameter, $barDiameter, 90, 90)
    $barPath.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(80, 255, 255, 255))), $barPath)

    $barFill = New-Object System.Drawing.RectangleF (62 * $scale), (189 * $scale), (88 * $scale), (16 * $scale)
    $fillPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fillPath.AddArc($barFill.X, $barFill.Y, $barDiameter, $barDiameter, 180, 90)
    $fillPath.AddArc($barFill.Right - $barDiameter, $barFill.Y, $barDiameter, $barDiameter, 270, 90)
    $fillPath.AddArc($barFill.Right - $barDiameter, $barFill.Bottom - $barDiameter, $barDiameter, $barDiameter, 0, 90)
    $fillPath.AddArc($barFill.X, $barFill.Bottom - $barDiameter, $barDiameter, $barDiameter, 90, 90)
    $fillPath.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 92, 245, 213))), $fillPath)

    $memory = New-Object System.IO.MemoryStream
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)

    if ($Size -eq 256) {
        $bitmap.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $g.Dispose()
    $bitmap.Dispose()
    return $memory.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Bytes = [byte[]]@(New-IconPngBytes -Size $size)
    }
}

$stream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$images.Count)
$offset = 6 + (16 * $images.Count)

foreach ($image in $images) {
    $entrySize = $image.Size
    if ($entrySize -eq 256) { $entrySize = 0 }
    $writer.Write([byte]$entrySize)
    $writer.Write([byte]$entrySize)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$image.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $writer.Write([byte[]]$image.Bytes)
}

$writer.Dispose()
$stream.Dispose()
Get-Item $icoPath, $previewPath | Select-Object FullName, Length, LastWriteTime
