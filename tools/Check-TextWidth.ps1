# Sprawdza, czy przetlumaczone teksty mieszcza sie w kontrolkach o stalej szerokosci.
# Mierzy tekst czcionka aplikacji (WPF FormattedText) we wszystkich jezykach i wypisuje
# kontrolki, w ktorych tekst jest szerszy niz miejsce (Width - Padding - ramka).
# Sprawdza przyciski (Content=) i naglowki kolumn DataGrid (Header=) z Width="N".
#   powershell -File tools\Check-TextWidth.ps1            # tylko przepelnienia
#   powershell -File tools\Check-TextWidth.ps1 -All       # wszystkie pomiary
param([switch]$All)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$loc = Join-Path $root 'src\Certify.Core\Localization'
$wpf = Join-Path $root 'src\Certify.WPF'

# Teksty: pl/en z UIStrings.cs, reszta z Translations\*.json (brak klucza -> en)
$texts = @{ pl = @{}; en = @{} }
$src = [IO.File]::ReadAllText((Join-Path $loc 'UIStrings.cs'), [Text.Encoding]::UTF8)
$rx = [regex]'\["([^"]+)"\]\s*=\s*\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)'
foreach ($m in $rx.Matches($src)) {
    $texts.pl[$m.Groups[1].Value] = [regex]::Unescape($m.Groups[2].Value)
    $texts.en[$m.Groups[1].Value] = [regex]::Unescape($m.Groups[3].Value)
}
foreach ($f in Get-ChildItem (Join-Path $loc 'Translations\*.json')) {
    $json = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $d = @{}
    foreach ($p in $json.PSObject.Properties) { $d[$p.Name] = $p.Value }
    $texts[$f.BaseName] = $d
}

$typeface = New-Object Windows.Media.Typeface (New-Object Windows.Media.FontFamily 'Segoe UI Variable, Segoe UI'), `
    ([Windows.FontStyles]::Normal), ([Windows.FontWeights]::SemiBold), ([Windows.FontStretches]::Normal)
function Measure-Text([string]$s, [double]$size) {
    $ft = New-Object Windows.Media.FormattedText $s, ([Globalization.CultureInfo]::InvariantCulture), `
        ([Windows.FlowDirection]::LeftToRight), $typeface, $size, ([Windows.Media.Brushes]::Black), 1.0
    $ft.WidthIncludingTrailingWhitespace
}

# Kontrolki: przycisk z Content+Width albo kolumna DataGrid z Header+Width
$items = @()
foreach ($f in Get-ChildItem (Join-Path $wpf '*.xaml')) {
    $n = 0
    foreach ($line in [IO.File]::ReadAllLines($f.FullName, [Text.Encoding]::UTF8)) {
        $n++
        if ($line -notmatch '\sWidth="(\d+)"') { continue }
        $width = [double]$Matches[1]
        if ($line -match '<Button\b[^>]*Content="\{DynamicResource (\w+)\}"') {
            $key = $Matches[1]
            # Padding domyslny ze stylu Button (14,7), ramka 1 px z kazdej strony
            $padH = 14
            if ($line -match 'Padding="(\d+)') { $padH = [double]$Matches[1] }
            $size = 12; if ($line -match 'FontSize="(\d+)"') { $size = [double]$Matches[1] }
            $items += [pscustomobject]@{ File = $f.Name; Line = $n; Key = $key; Avail = $width - 2 * $padH - 2; Size = $size }
        }
        elseif ($line -match '<DataGrid\w*Column\b[^>]*Header="\{DynamicResource (\w+)\}"') {
            # Naglowek DataGrid: ok. 10 px marginesu + miejsce na strzalke sortowania
            $items += [pscustomobject]@{ File = $f.Name; Line = $n; Key = $Matches[1]; Avail = $width - 20; Size = 12 }
        }
    }
}

$problems = 0
foreach ($it in $items) {
    foreach ($lang in ($texts.Keys | Sort-Object)) {
        $t = $texts[$lang][$it.Key]; if (-not $t) { $t = $texts.en[$it.Key] }
        $w = [math]::Round((Measure-Text $t $it.Size), 1)
        $over = $w -gt $it.Avail
        if ($over) { $problems++ }
        if ($over -or $All) {
            '{0}:{1}  {2,-22} {3}  {4,6} / {5,-5} {6}{7}' -f $it.File, $it.Line, $it.Key, $lang, $w, $it.Avail, $t, $(if ($over) { '  <-- za dlugi' } else { '' })
        }
    }
}
"Sprawdzono {0} kontrolek x {1} jezykow, przepelnien: {2}" -f $items.Count, $texts.Count, $problems
if ($problems -gt 0) { exit 1 }
