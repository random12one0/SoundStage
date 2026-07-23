# Generates the Soundstage app icon from the same waveform mark the UI and tray use, so there is one
# source of truth for the brand rather than a binary someone has to redraw by hand.
#
#   pwsh tools/make-icon.ps1
#
# Writes assets/soundstage.ico with the sizes Windows actually asks for (16..256). Entries are PNG
# compressed, which every Windows since Vista understands and which keeps the file small.

param(
    [string]$OutPath = "$PSScriptRoot\..\assets\soundstage.ico"
)

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded dark tile, so the mark reads on any taskbar colour.
    $pad = [Math]::Max(1, [int]($size * 0.06))
    $radius = [Math]::Max(2, [int]($size * 0.22))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 24, 34, 48),
        [System.Drawing.Color]::FromArgb(255, 14, 22, 32),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($bg, $path)

    # The waveform: one cycle, in the product's teal.
    $penWidth = [Math]::Max(1.2, $size * 0.10)
    $pen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(255, 55, 224, 207), $penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $points = @()
    $steps = [Math]::Max(16, $size)
    for ($i = 0; $i -lt $steps; $i++) {
        $t = $i / ($steps - 1)
        $x = ($size * 0.20) + ($t * $size * 0.60)
        $y = ($size * 0.50) - ([Math]::Sin($t * [Math]::PI * 2) * $size * 0.20)
        $points += New-Object System.Drawing.PointF($x, $y)
    }
    if ($points.Count -gt 2) { $g.DrawCurve($pen, [System.Drawing.PointF[]]$points) }

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
}

# Assemble the .ico container by hand: a 6-byte header, then one 16-byte directory entry per image,
# then the PNG payloads.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0)               # reserved
$w.Write([UInt16]1)               # type: icon
$w.Write([UInt16]$pngs.Count)

$offset = 6 + (16 * $pngs.Count)
for ($i = 0; $i -lt $pngs.Count; $i++) {
    $size = $sizes[$i]
    # A dimension of 0 in the directory means 256 — the field is only one byte wide.
    $dim = [Byte]0
    if ($size -lt 256) { $dim = [Byte]$size }
    $w.Write($dim)
    $w.Write($dim)
    $w.Write([Byte]0)             # palette
    $w.Write([Byte]0)             # reserved
    $w.Write([UInt16]1)           # colour planes
    $w.Write([UInt16]32)          # bits per pixel
    $w.Write([UInt32]$pngs[$i].Length)
    $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
$w.Dispose()
$out.Dispose()

Write-Host "wrote $OutPath ($([Math]::Round((Get-Item $OutPath).Length / 1KB, 1)) KB, $($pngs.Count) sizes)"
