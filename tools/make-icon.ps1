# Generates Assets/ember.png and Assets/ember.ico (multi-resolution, PNG-compressed entries),
# plus the Linux hicolor icon theme PNGs (packaging/linux/icons/<size>x<size>/ember-monitor.png).
param([string]$OutDir = "$PSScriptRoot\..\src\Ember\Assets",
      [string]$LinuxDir = "$PSScriptRoot\..\packaging\linux\icons")
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Path([string]$d, [float]$s) {
    # Minimal SVG-path subset parser (M, C, Z with absolute coords) scaled by $s/256
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tokens = [regex]::Matches($d, '[MCZ]|-?\d+(\.\d+)?') | ForEach-Object { $_.Value }
    $i = 0; $cur = $null; $start = $null; $k = $s / 256.0
    while ($i -lt $tokens.Count) {
        $t = $tokens[$i]
        if ($t -eq 'M') { $cur = [System.Drawing.PointF]::new([float]$tokens[$i+1]*$k, [float]$tokens[$i+2]*$k); $start = $cur; $p.StartFigure(); $i += 3 }
        elseif ($t -eq 'C') {
            $i++
            while ($i -lt $tokens.Count -and $tokens[$i] -notmatch '[MCZ]') {
                $c1 = [System.Drawing.PointF]::new([float]$tokens[$i]*$k, [float]$tokens[$i+1]*$k)
                $c2 = [System.Drawing.PointF]::new([float]$tokens[$i+2]*$k, [float]$tokens[$i+3]*$k)
                $e  = [System.Drawing.PointF]::new([float]$tokens[$i+4]*$k, [float]$tokens[$i+5]*$k)
                $p.AddBezier($cur, $c1, $c2, $e); $cur = $e; $i += 6
            }
        }
        elseif ($t -eq 'Z') { $p.CloseFigure(); $i++ }
        else { $i++ }
    }
    return $p
}

$outer = 'M128,30 C150,72 194,100 194,154 C194,198 164,226 128,226 C92,226 62,198 62,154 C62,122 79,101 93,83 C95,105 105,117 117,121 C113,91 117,60 128,30 Z'
$inner = 'M128,122 C141,141 160,154 160,180 C160,201 146,213 128,213 C110,213 96,201 96,181 C96,163 109,151 117,139 C119,151 124,157 130,159 C128,147 126,135 128,122 Z'

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square tile
    $r = [float]($size * 0.22); $pad = [float]($size * 0.02); $w = [float]($size - 2*$pad)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($pad, $pad, 2*$r, 2*$r, 180, 90)
    $tile.AddArc($pad + $w - 2*$r, $pad, 2*$r, 2*$r, 270, 90)
    $tile.AddArc($pad + $w - 2*$r, $pad + $w - 2*$r, 2*$r, 2*$r, 0, 90)
    $tile.AddArc($pad, $pad + $w - 2*$r, 2*$r, 2*$r, 90, 90)
    $tile.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0,0)), ([System.Drawing.PointF]::new(0,$size)), ([System.Drawing.Color]::FromArgb(255,0x2A,0x2C,0x38)), ([System.Drawing.Color]::FromArgb(255,0x12,0x13,0x19))
    $g.FillPath($bg, $tile)
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40,255,255,255)), ([float][Math]::Max(1, $size/128))
        $g.DrawPath($pen, $tile)
    }

    # Glow
    if ($size -ge 48) {
        $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glow.AddEllipse([float]($size*0.22), [float]($size*0.42), [float]($size*0.56), [float]($size*0.52))
        $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
        $pgb.CenterColor = [System.Drawing.Color]::FromArgb(95, 0xB0, 0x7C, 0xFF)
        $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0xB0, 0x7C, 0xFF))
        $g.FillPath($pgb, $glow)
    }

    # Flame — scaled into the tile with some margin
    $scale = 0.80; $off = $size * (1 - $scale) / 2
    $g.TranslateTransform([float]$off, [float]($off + $size*0.01))
    $o = New-Path $outer ($size * $scale)
    $ob = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, [float]($size*$scale*0.1))), ([System.Drawing.PointF]::new(0, [float]($size*$scale*0.9))), ([System.Drawing.Color]::FromArgb(255,0xE6,0xD2,0xFF)), ([System.Drawing.Color]::FromArgb(255,0x70,0x3E,0xE6))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.Color]::FromArgb(255,0xE6,0xD2,0xFF), [System.Drawing.Color]::FromArgb(255,0xB0,0x7C,0xFF), [System.Drawing.Color]::FromArgb(255,0x70,0x3E,0xE6))
    $blend.Positions = @([float]0, [float]0.5, [float]1)
    $ob.InterpolationColors = $blend
    $g.FillPath($ob, $o)
    $in = New-Path $inner ($size * $scale)
    $ib = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, [float]($size*$scale*0.45))), ([System.Drawing.PointF]::new(0, [float]($size*$scale*0.85))), ([System.Drawing.Color]::FromArgb(255,0xFB,0xF7,0xFF)), ([System.Drawing.Color]::FromArgb(255,0xD3,0xB4,0xFF))
    $g.FillPath($ib, $in)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $b = Render $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    if ($s -eq 256) { $b.Save((Join-Path $OutDir 'ember.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
}

# ICO container
$fs = [System.IO.File]::Create((Join-Path $OutDir 'ember.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$s].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Close()
Write-Output "Icon written to $OutDir"

# Linux: hicolor theme sizes
foreach ($s in 16, 22, 24, 32, 48, 64, 128, 256, 512) {
    $dir = Join-Path $LinuxDir "${s}x${s}"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $b = Render $s
    $b.Save((Join-Path $dir 'ember-monitor.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}
Write-Output "Linux icons written to $LinuxDir"
