# Genera src/PassDesk.App/Assets/PassDesk.ico (candado blanco sobre fondo azul) sin recursos externos.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-icon.ps1
param([string]$Output = (Join-Path $PSScriptRoot "..\src\PassDesk.App\Assets\PassDesk.ico"))

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size
    $margin = [single]([Math]::Max(1, $s * 0.04))
    $bg = New-RoundedPath $margin $margin ($s - 2 * $margin) ($s - 2 * $margin) ([single]($s * 0.22))
    $rect = New-Object System.Drawing.RectangleF($margin, $margin, ($s - 2 * $margin), ($s - 2 * $margin))
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 54, 148, 230), [System.Drawing.Color]::FromArgb(255, 10, 84, 176), [single]90)
    $g.FillPath($brush, $bg)

    # Arco del candado
    $white = [System.Drawing.Color]::White
    $penWidth = [single]([Math]::Max(1.5, $s * 0.085))
    $pen = New-Object System.Drawing.Pen($white, $penWidth)
    $shackleW = $s * 0.34
    $shackleX = ($s - $shackleW) / 2
    $shackleY = $s * 0.20
    $g.DrawArc($pen, [single]$shackleX, [single]$shackleY, [single]$shackleW, [single]($s * 0.36), [single]180, [single]180)
    $g.DrawLine($pen, [single]$shackleX, [single]($shackleY + $s * 0.18), [single]$shackleX, [single]($s * 0.47))
    $g.DrawLine($pen, [single]($shackleX + $shackleW), [single]($shackleY + $s * 0.18), [single]($shackleX + $shackleW), [single]($s * 0.47))

    # Cuerpo del candado
    $bodyW = $s * 0.52
    $bodyH = $s * 0.38
    $bodyX = ($s - $bodyW) / 2
    $bodyY = $s * 0.44
    $body = New-RoundedPath ([single]$bodyX) ([single]$bodyY) ([single]$bodyW) ([single]$bodyH) ([single]($s * 0.07))
    $g.FillPath((New-Object System.Drawing.SolidBrush($white)), $body)

    # Bocallave
    if ($size -ge 24) {
        $holeBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 16, 96, 190))
        $holeR = $s * 0.055
        $cx = $s / 2
        $cy = $bodyY + $bodyH * 0.42
        $g.FillEllipse($holeBrush, [single]($cx - $holeR), [single]($cy - $holeR), [single]($holeR * 2), [single]($holeR * 2))
        $g.FillRectangle($holeBrush, [single]($cx - $holeR * 0.45), [single]$cy, [single]($holeR * 0.9), [single]($bodyH * 0.30))
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 256, 128, 64, 48, 32, 24, 16
$images = @()
foreach ($size in $sizes) { $images += ,(New-IconPng $size) }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([Byte]$dim)
    $writer.Write([Byte]$dim)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length)
    $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $writer.Write($img) }
$writer.Flush()

$dir = Split-Path -Parent $Output
New-Item -ItemType Directory -Force $dir | Out-Null
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
# Vista previa PNG para documentación
[System.IO.File]::WriteAllBytes((Join-Path $dir "PassDesk-256.png"), $images[0])
Write-Output "Icono generado: $Output ($($out.Length) bytes)"
