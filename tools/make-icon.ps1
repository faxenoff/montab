# Собирает assets/montab.ico из геометрии assets/tray.svg.
# В системе нет rsvg/inkscape/magick, поэтому те же примитивы рисуются через
# System.Drawing: экран с большим окном слева и лентой из четырёх табов справа.
#
# Запуск:  pwsh -File tools/make-icon.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'assets\montab.ico'

$Body   = [System.Drawing.Color]::FromArgb(255, 0x1B, 0x1F, 0x24)  # корпус
$Chrome = [System.Drawing.Color]::FromArgb(255, 0x9A, 0xA0, 0xA6)  # рамка и подставка
$Accent = [System.Drawing.Color]::FromArgb(255, 0x3F, 0xA9, 0xF5)  # активное окно и таб
$Idle   = [System.Drawing.Color]::FromArgb(255, 0x6B, 0x71, 0x76)  # прочие табы

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0.01) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h)))
    } else {
        $d = $r * 2
        $path.AddArc($x, $y, $d, $d, 180, 90)
        $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
        $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $path.CloseFigure()
    }
    return $path
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $size / 32.0
    $fill = { param($color, $x, $y, $w, $h, $r)
        $path = New-RoundedPath ($x * $k) ($y * $k) ($w * $k) ($h * $k) ($r * $k)
        $brush = New-Object System.Drawing.SolidBrush($color)
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }

    # Корпус монитора: заливка + рамка
    $screen = New-RoundedPath (2 * $k) (3.5 * $k) (28 * $k) (21 * $k) (2.6 * $k)
    $brush = New-Object System.Drawing.SolidBrush($Body)
    $g.FillPath($brush, $screen)
    $pen = New-Object System.Drawing.Pen($Chrome, [single](1.6 * $k))
    $g.DrawPath($pen, $screen)
    $brush.Dispose(); $pen.Dispose(); $screen.Dispose()

    # Подставка
    & $fill $Chrome 13.4 24.5 5.2 2.6 0
    & $fill $Chrome 9.4 26.6 13.2 2.0 1.0

    # Активное окно и лента превью (второй таб — активный)
    & $fill $Accent 5.0 6.4 13.4 15.2 1.2
    & $fill $Idle   20.2 6.4  6.6 3.0 0.9
    & $fill $Accent 20.2 10.4 6.6 3.0 0.9
    & $fill $Idle   20.2 14.4 6.6 3.0 0.9
    & $fill $Idle   20.2 18.4 6.6 3.0 0.9

    $g.Dispose()
    return $bmp
}

# Кадр ICO: до 48 px — классический DIB (32bpp + пустая AND-маска), крупнее — PNG
function Get-FrameBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    if ($w -gt 48) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $ms.ToArray()   # запятая: иначе конвейер развернёт массив в Object[]
    }

    $stride = $w * 4
    $maskStride = [int][Math]::Floor(($w + 31) / 32) * 4
    $bytes = New-Object byte[] ([int](40 + $stride * $h + $maskStride * $h))
    $bw = New-Object System.IO.BinaryWriter (New-Object System.IO.MemoryStream($bytes, $true))
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($stride * $h))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)

    # XOR-плоскость идёт снизу вверх
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    # AND-маска нулевая: прозрачность несёт альфа-канал
    $bw.Flush(); $bw.Dispose()
    return , $bytes
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $frames += , @{ Size = $size; Bytes = (Get-FrameBytes $bmp) }
    $bmp.Dispose()
}

$stream = [System.IO.File]::Create($out)
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([int16]1); $writer.Write([int16]32)
    $writer.Write([int]$frame.Bytes.Length)
    $writer.Write([int]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
$writer.Dispose(); $stream.Dispose()

"$out — $($frames.Count) frames, $((Get-Item $out).Length) bytes"
