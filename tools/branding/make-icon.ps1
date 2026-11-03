# Generates the original Zipark icon (Assets/Zipark.ico + logo.png) with System.Drawing.
# Design: teal rounded tile, a white "X" made of two archive "bands", an amber clasp in the centre.
param([string]$OutDir = "$PSScriptRoot\..\..\src\Zipark.Desktop\Assets")
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Tile([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $r = [Math]::Max(2, [int]($s * 0.22)); $m = [Math]::Max(0, [int]($s * 0.04)); $w = $s - 2 * $m
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($m, $m, $r, $r, 180, 90); $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
  $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90); $path.CloseFigure()
  $rect = New-Object System.Drawing.Rectangle $m, $m, $w, $w
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 20, 150, 160)), ([System.Drawing.Color]::FromArgb(255, 9, 78, 92)), 45
  $g.FillPath($brush, $path)
  $c = $s / 2.0; $len = $s * 0.62; $th = [Math]::Max(2.0, $s * 0.14)
  foreach ($angle in 45, -45) {
    $state = $g.Save(); $g.TranslateTransform($c, $c); $g.RotateTransform($angle)
    $band = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = -$len / 2; $by = -$th / 2; $br = $th
    $band.AddArc($bx, $by, $br, $th, 90, 180); $band.AddArc($bx + $len - $br, $by, $br, $th, 270, 180); $band.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $band)
    $g.Restore($state)
  }
  $k = [Math]::Max(3.0, $s * 0.2)
  $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 166, 35))
  $g.FillEllipse($amber, $c - $k / 2, $c - $k / 2, $k, $k)
  if ($s -ge 32) { $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 9, 78, 92)), ([Math]::Max(1.0, $s * 0.025)); $g.DrawEllipse($pen, $c - $k / 2, $c - $k / 2, $k, $k) }
  $g.Dispose(); return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) { $b = New-Tile $s; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += , $ms.ToArray(); if ($s -eq 256) { $b.Save("$OutDir\logo.png", [System.Drawing.Imaging.ImageFormat]::Png) }; $b.Dispose() }
$out = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset); $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes("$OutDir\Zipark.ico", $out.ToArray())
"icon written to $OutDir"
