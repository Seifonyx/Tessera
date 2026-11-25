# Draws Tessera.ico: four glassy rounded tiles in a 2x2 mosaic, rendered separately at every icon size
# so each one is crisp (no scaling). Run once; build.cmd embeds the result in Tessera.exe.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
# sky, indigo, coral, mint
$colors = @(
    [System.Drawing.Color]::FromArgb(255, 76, 194, 255),
    [System.Drawing.Color]::FromArgb(255, 124, 118, 255),
    [System.Drawing.Color]::FromArgb(255, 255, 132, 92),
    [System.Drawing.Color]::FromArgb(255, 70, 214, 150))

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Lighter($c, [float]$t) {
    [System.Drawing.Color]::FromArgb($c.A, [int]($c.R + (255 - $c.R) * $t), [int]($c.G + (255 - $c.G) * $t), [int]($c.B + (255 - $c.B) * $t))
}

$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [Math]::Max(1, [Math]::Round($s * 0.06)); $gap = [Math]::Max(1, [Math]::Round($s * 0.07))
    $tile = ($s - 2 * $pad - $gap) / 2.0
    $r = [Math]::Max(1.5, $tile * 0.24)
    for ($i = 0; $i -lt 4; $i++) {
        $x = $pad + ($i % 2) * ($tile + $gap); $y = $pad + [Math]::Floor($i / 2) * ($tile + $gap)
        $path = RoundRect $x $y $tile $tile $r
        $c = $colors[$i]
        if ($s -ge 24) {
            # glass: lighter at the top-left, full colour at the bottom-right
            $rect = New-Object System.Drawing.RectangleF $x, $y, $tile, $tile
            $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (Lighter $c 0.35), $c, 45.0
        } else { $br = New-Object System.Drawing.SolidBrush $c }
        $g.FillPath($br, $path); $br.Dispose()
        if ($s -ge 32) {
            # faint white rim, like the menu's frosted tiles
            $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), ([float][Math]::Max(1, $s / 64))
            $g.DrawPath($pen, $path); $pen.Dispose()
        }
        $path.Dispose()
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

# .ico container: header, one directory entry per size, then the PNG images
$out = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush(); [IO.File]::WriteAllBytes((Join-Path $here 'Tessera.ico'), $out.ToArray())
# preview of the large size, for checking the design
[IO.File]::WriteAllBytes((Join-Path $here 'Tessera-256.png'), $pngs[$sizes.Count - 1])
"Tessera.ico written ($($sizes -join ', ') px)"
