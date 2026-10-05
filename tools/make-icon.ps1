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

# Палитра кадра: четыре цвета рисунка, полупрозрачная рамка для сглаженного
# внешнего края и смесь корпуса с акцентом для края окна. 8 цветов — 4 бита на пиксель.
$Palette = @(
    [System.Drawing.Color]::FromArgb(0, $Chrome),
    $Body, $Chrome, $Accent, $Idle,
    [System.Drawing.Color]::FromArgb(85, $Chrome),
    [System.Drawing.Color]::FromArgb(170, $Chrome),
    [System.Drawing.Color]::FromArgb(255, 0x2D, 0x64, 0x8C)
)

$CrcTable = New-Object uint32[] 256
for ($n = 0; $n -lt 256; $n++) {
    [uint32]$c = $n
    for ($k = 0; $k -lt 8; $k++) {
        if ($c -band 1) { $c = [uint32](3988292384 -bxor ($c -shr 1)) } else { $c = $c -shr 1 }
    }
    $CrcTable[$n] = $c
}

function Get-BigEndian([uint32]$v) {
    return , [byte[]]@((($v -shr 24) -band 0xFF), (($v -shr 16) -band 0xFF), (($v -shr 8) -band 0xFF), ($v -band 0xFF))
}

function Write-PngChunk([System.IO.Stream]$stream, [string]$type, [byte[]]$data) {
    $body = [System.Text.Encoding]::ASCII.GetBytes($type) + $data
    [uint32]$crc = [uint32]::MaxValue
    foreach ($byte in $body) { $crc = $CrcTable[($crc -bxor $byte) -band 0xFF] -bxor ($crc -shr 8) }
    $crc = $crc -bxor [uint32]::MaxValue
    $len = Get-BigEndian $data.Length
    $stream.Write($len, 0, 4)
    $stream.Write($body, 0, $body.Length)
    $sum = Get-BigEndian $crc
    $stream.Write($sum, 0, 4)
}

# Индекс ближайшего цвета палитры; сравнение в premultiplied-координатах,
# чтобы прозрачные пиксели не тянулись к цвету по «невидимому» RGB.
function Get-PaletteIndex([System.Drawing.Color]$c) {
    $best = 0; $bestDist = [int]::MaxValue
    for ($i = 0; $i -lt $Palette.Count; $i++) {
        $p = $Palette[$i]
        $dr = $c.R * $c.A - $p.R * $p.A
        $dg = $c.G * $c.A - $p.G * $p.A
        $db = $c.B * $c.A - $p.B * $p.A
        $da = ($c.A - $p.A) * 255
        $dist = [long]$dr * $dr + [long]$dg * $dg + [long]$db * $db + [long]$da * $da
        if ($dist -lt $bestDist) { $bestDist = $dist; $best = $i }
    }
    return $best
}

# Кадр ICO: палитровый PNG (4 бита на пиксель + tRNS). Windows читает PNG-кадры
# с Vista; палитровый вариант проверен на Windows 11.
function Get-FrameBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rowBytes = [int][Math]::Ceiling($w / 2.0)
    $raw = New-Object byte[] (($rowBytes + 1) * $h)   # +1: байт фильтра (0) в начале строки
    for ($y = 0; $y -lt $h; $y++) {
        $row = $y * ($rowBytes + 1) + 1
        for ($x = 0; $x -lt $w; $x++) {
            $index = Get-PaletteIndex ($bmp.GetPixel($x, $y))
            $shift = if ($x % 2 -eq 0) { 4 } else { 0 }
            $at = $row + ($x -shr 1)
            $raw[$at] = $raw[$at] -bor ($index -shl $shift)
        }
    }

    $packed = New-Object System.IO.MemoryStream
    $zlib = New-Object System.IO.Compression.ZLibStream($packed, [System.IO.Compression.CompressionLevel]::SmallestSize)
    $zlib.Write($raw, 0, $raw.Length)
    $zlib.Dispose()

    $header = (Get-BigEndian $w) + (Get-BigEndian $h) + [byte[]]@(4, 3, 0, 0, 0)   # 4 бита, тип 3 (палитра)
    $plte = [byte[]]($Palette | ForEach-Object { $_.R; $_.G; $_.B })
    $trns = [byte[]]($Palette | ForEach-Object { $_.A })

    $png = New-Object System.IO.MemoryStream
    $signature = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    $png.Write($signature, 0, 8)
    Write-PngChunk $png 'IHDR' $header
    Write-PngChunk $png 'PLTE' $plte
    Write-PngChunk $png 'tRNS' $trns
    Write-PngChunk $png 'IDAT' $packed.ToArray()
    Write-PngChunk $png 'IEND' ([byte[]]@())
    return , $png.ToArray()   # запятая: иначе конвейер развернёт массив в Object[]
}

# 64 и 128 px не нужны: Windows уменьшает их из 256
$sizes = @(16, 20, 24, 32, 48, 256)
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
