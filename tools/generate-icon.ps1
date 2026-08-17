<#
.SYNOPSIS
  Regenerates dotnet/vx-connector.ico — the application icon embedded in the exe.

.DESCRIPTION
  The icon is drawn in code rather than kept as hand-authored artwork so the small
  sizes can differ from the large ones: at 16px the twelve dimples of the full
  drawing collapse into grey mush, so those sizes get a reduced set of larger ones
  and a tighter inset. Same golf ball MainForm used to draw at runtime.

  The .ico container is written by hand because System.Drawing can only save a
  single-image icon. Frames up to 128px are uncompressed 32-bit DIBs (the format
  every consumer has understood since Windows 95); 256px is a PNG frame, which is
  the conventional encoding for that size and keeps the file ~15x smaller than the
  DIB equivalent.

  Run after changing the artwork, then commit the .ico:
    pwsh -File tools/generate-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\dotnet\vx-connector.ico')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Sizes Windows actually asks for: 16 (title bar / tray), 24 and 32 (alt-tab, small
# Explorer icons), 48 (medium icons), 64/128/256 (large and extra-large views).
$Sizes = @(16, 24, 32, 48, 64, 128, 256)

# Dimples as fractions of the ball's bounding box, derived from the 48px drawing
# MainForm carried (ball at 4,4,40x40) so the large sizes look identical to it.
# x, y, diameter.
$DimplesFull = @(
    @(0.300, 0.200, 0.125), @(0.600, 0.250, 0.125), @(0.200, 0.450, 0.125),
    @(0.500, 0.500, 0.150), @(0.350, 0.700, 0.125), @(0.700, 0.600, 0.100),
    @(0.150, 0.700, 0.100), @(0.600, 0.800, 0.100), @(0.400, 0.400, 0.100),
    @(0.750, 0.400, 0.100), @(0.250, 0.300, 0.075), @(0.450, 0.150, 0.075)
)

# 16px and 24px: fewer, larger dimples. Anything finer just reads as noise.
$DimplesSmall = @(
    @(0.300, 0.200, 0.150), @(0.625, 0.275, 0.150), @(0.200, 0.500, 0.150),
    @(0.525, 0.550, 0.175), @(0.375, 0.775, 0.150)
)

function New-BallBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # Small sizes give up the margin so the ball fills more of the cell; large
        # ones keep the breathing room Windows expects around an app icon.
        $insetFraction = if ($Size -le 24) { 0.06 } else { 0.0833 }
        $inset = $Size * $insetFraction
        $diameter = $Size - (2 * $inset)
        $ball = New-Object System.Drawing.RectangleF($inset, $inset, $diameter, $diameter)

        # Slightly wider gradient than the runtime drawing used: at 256px a
        # 250->200 ramp reads as flat grey.
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $ball,
            [System.Drawing.Color]::FromArgb(255, 253, 253, 253),
            [System.Drawing.Color]::FromArgb(255, 193, 196, 200),
            45.0)
        $g.FillEllipse($grad, $ball)
        $grad.Dispose()

        $dimples = if ($Size -le 24) { $DimplesSmall } else { $DimplesFull }
        $dimpleBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(60, 110, 115, 120))
        foreach ($d in $dimples) {
            $dx = $ball.X + ($ball.Width * $d[0])
            $dy = $ball.Y + ($ball.Height * $d[1])
            $dd = $ball.Width * $d[2]
            $g.FillEllipse($dimpleBrush, $dx, $dy, $dd, $dd)
        }
        $dimpleBrush.Dispose()

        # Darker and thicker than the runtime drawing's hairline. An app icon has to
        # survive a white Explorer background, where a near-white ball with a faint
        # outline disappears entirely.
        $penWidth = [Math]::Max(1.0, $Size / 32.0)
        $pen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(190, 96, 100, 105), $penWidth)
        $half = $penWidth / 2.0
        $g.DrawEllipse($pen,
            $ball.X + $half, $ball.Y + $half,
            $ball.Width - $penWidth, $ball.Height - $penWidth)
        $pen.Dispose()
    }
    finally {
        $g.Dispose()
    }

    return $bmp
}

# 32-bit BGRA DIB as an ICO frame: BITMAPINFOHEADER with a doubled height, bottom-up
# pixel rows, then a 1bpp AND mask. The mask is all zeros — 32-bit frames key off the
# alpha channel — but it still has to be present and DWORD-row-aligned.
function ConvertTo-IconDib {
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $locked = $Bitmap.LockBits($rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $locked.Stride
        $pixels = New-Object byte[] ($stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($locked)
    }

    $rowBytes = $size * 4
    $maskRowBytes = [int]([Math]::Floor(($size + 31) / 32)) * 4
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    try {
        $w.Write([uint32]40)            # biSize
        $w.Write([int32]$size)          # biWidth
        $w.Write([int32]($size * 2))    # biHeight — XOR image plus AND mask
        $w.Write([uint16]1)             # biPlanes
        $w.Write([uint16]32)            # biBitCount
        $w.Write([uint32]0)             # biCompression = BI_RGB
        $w.Write([uint32](($rowBytes * $size) + ($maskRowBytes * $size)))  # biSizeImage
        $w.Write([int32]0)              # biXPelsPerMeter
        $w.Write([int32]0)              # biYPelsPerMeter
        $w.Write([uint32]0)             # biClrUsed
        $w.Write([uint32]0)             # biClrImportant

        for ($y = $size - 1; $y -ge 0; $y--) {
            $w.Write($pixels, $y * $stride, $rowBytes)
        }

        $maskRow = New-Object byte[] $maskRowBytes
        for ($y = 0; $y -lt $size; $y++) {
            $w.Write($maskRow, 0, $maskRowBytes)
        }

        $w.Flush()
        return $ms.ToArray()
    }
    finally {
        $w.Dispose()
        $ms.Dispose()
    }
}

function ConvertTo-IconPng {
    param([System.Drawing.Bitmap]$Bitmap)

    $ms = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return $ms.ToArray()
    }
    finally {
        $ms.Dispose()
    }
}

$frames = @()
foreach ($size in $Sizes) {
    $bmp = New-BallBitmap -Size $size
    try {
        # PNG for 256 only: it is the conventional encoding at that size and saves
        # ~250 KB. Everything smaller stays a plain DIB for maximum compatibility.
        $data = if ($size -ge 256) { ConvertTo-IconPng $bmp } else { ConvertTo-IconDib $bmp }
        $frames += [pscustomobject]@{ Size = $size; Data = $data }
    }
    finally {
        $bmp.Dispose()
    }
}

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$out = [System.IO.File]::Create($OutputPath)
$w = New-Object System.IO.BinaryWriter($out)
try {
    $w.Write([uint16]0)                 # idReserved
    $w.Write([uint16]1)                 # idType = icon
    $w.Write([uint16]$frames.Count)     # idCount

    # Image data starts after the directory.
    $offset = 6 + (16 * $frames.Count)
    foreach ($f in $frames) {
        # 256 is encoded as 0 in the single-byte width/height fields.
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $w.Write([byte]$dim)            # bWidth
        $w.Write([byte]$dim)            # bHeight
        $w.Write([byte]0)               # bColorCount (0 = truecolour)
        $w.Write([byte]0)               # bReserved
        $w.Write([uint16]1)             # wPlanes
        $w.Write([uint16]32)            # wBitCount
        $w.Write([uint32]$f.Data.Length)
        $w.Write([uint32]$offset)
        $offset += $f.Data.Length
    }

    foreach ($f in $frames) {
        $w.Write($f.Data, 0, $f.Data.Length)
    }
    $w.Flush()
}
finally {
    $w.Dispose()
    $out.Dispose()
}

$written = Get-Item $OutputPath
'{0} — {1} frames ({2}), {3:N1} KB' -f `
    $written.Name, $frames.Count, ($Sizes -join '/'), ($written.Length / 1KB)
