# -OutIco: plik .ico (wszystkie rozmiary); -OutPng: osobny PNG (ikona pakietu NuGet, domyslnie 128 px).
param([string]$OutIco, [string]$PreviewPng, [string]$OutPng, [int]$PngSize = 128)
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tlo: zaokraglony kwadrat, gradient AccentColor -> Accent2Color z App.xaml.
    $r = [Math]::Max(2.0, $s * 0.22); $d = $r * 2; $w = $s - 1
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(0, 0, $d, $d, 180, 90); $bg.AddArc($w - $d, 0, $d, $d, 270, 90)
    $bg.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $bg.AddArc(0, $w - $d, $d, $d, 90, 90); $bg.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.ColorTranslator]::FromHtml('#4F8CFF')), ([System.Drawing.ColorTranslator]::FromHtml('#22D3EE'))
    $g.FillPath($grad, $bg)

    # Tarcza (biala).
    function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([float]($x * $s)), ([float]($y * $s)) }
    $sh = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sh.AddLine((P 0.50 0.15), (P 0.80 0.26))
    $sh.AddBezier((P 0.80 0.26), (P 0.80 0.56), (P 0.70 0.74), (P 0.50 0.86))
    $sh.AddBezier((P 0.50 0.86), (P 0.30 0.74), (P 0.20 0.56), (P 0.20 0.26))
    $sh.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $sh)

    # Ptaszek (ciemny niebieski) - grubszy na malych rozmiarach, zeby byl czytelny.
    $pw = [float]([Math]::Max(1.6, $s * ($(if ($s -le 24) { 0.11 } else { 0.08 }))))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#1D4ED8')), $pw
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((P 0.36 0.50), (P 0.46 0.61), (P 0.65 0.40)))

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
# Klatka DIB (BMP w ICO): BITMAPINFOHEADER (wysokosc x2) + BGRA bottom-up + maska AND (zera, liczy sie alfa).
function Get-DibBytes($b) {
    $s = $b.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]($s * $s * 4 + $maskRow * $s)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $b.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return , $ms.ToArray()
}

# PNG tylko dla 256 (standard), mniejsze jako DIB - czytelne dla kazdego dekodera (GDI+, stare narzedzia).
$pngs = foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($PreviewPng) { $b.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png) }
        $bytes = $ms.ToArray()
    } else {
        $bytes = Get-DibBytes $b
    }
    $b.Dispose()
    , $bytes
}

if ($OutPng) {
    $b = New-IconBitmap $PngSize
    $b.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    "OK $OutPng ($((Get-Item $OutPng).Length) B, ${PngSize}x${PngSize})"
}
if (-not $OutIco) { return }

# ICO: ICONDIR + ICONDIRENTRY[] + ramki.
$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))   # 256 zapisywane jako 0
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
"OK $OutIco ($((Get-Item $OutIco).Length) B, $($sizes.Count) rozmiarow)"
