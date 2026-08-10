# Renders every command icon onto one sheet, at 16px (actual size) and 64px (to inspect
# the geometry), over light and dark bands. Design review aid — not part of the build.
param(
    [string]$IconDir = "$PSScriptRoot\..\src\Tootega.Cockpit\Resources\Icons",
    [string]$OutFile = "$PSScriptRoot\icon-preview.png"
)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$icons = Get-ChildItem -Path $IconDir -Filter *.xaml | Sort-Object Name
$cell = 96
$cols = $icons.Count
$width = $cell * $cols
$height = $cell * 2

$visual = [Windows.Media.DrawingVisual]::new()
$dc = $visual.RenderOpen()

$light = [Windows.Media.Brushes]::WhiteSmoke
$dark = [Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(31, 31, 31))
$dc.DrawRectangle($light, $null, [Windows.Rect]::new(0, 0, $width, $cell))
$dc.DrawRectangle($dark, $null, [Windows.Rect]::new(0, $cell, $width, $cell))

for ($i = 0; $i -lt $icons.Count; $i++) {
    $xamlText = Get-Content $icons[$i].FullName -Raw
    $x = $i * $cell

    foreach ($row in 0, 1) {
        # Row 1 stands in for the dark theme. At runtime the VS ImageService inverts
        # luminosity while preserving hue, so the neutral stroke flips to near-white and
        # the orange accent stays orange. Reproducing that here keeps the preview honest:
        # drawing the unmodified source on a dark band would suggest a bug that isn't one.
        $rowXaml = if ($row -eq 1) { $xamlText -replace '#FF2B2B2B', '#FFE4E4E4' } else { $xamlText }
        $el = [Windows.Markup.XamlReader]::Parse($rowXaml)

        $big = 64
        $el.Measure([Windows.Size]::new($big, $big))
        $el.Arrange([Windows.Rect]::new(0, 0, $big, $big))
        $el.UpdateLayout()

        $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new($big, $big, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bmp.Render($el)

        $src = $bmp
        $ox = $x + ($cell - $big) / 2
        $oy = $row * $cell + 6
        $dc.DrawImage($src, [Windows.Rect]::new($ox, $oy, $big, $big))

        # 16px actual size, beside the large one
        $small = 16
        $el2 = [Windows.Markup.XamlReader]::Parse($rowXaml)
        $el2.Measure([Windows.Size]::new($small, $small))
        $el2.Arrange([Windows.Rect]::new(0, 0, $small, $small))
        $el2.UpdateLayout()
        $bmp2 = [Windows.Media.Imaging.RenderTargetBitmap]::new($small, $small, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bmp2.Render($el2)
        $dc.DrawImage($bmp2, [Windows.Rect]::new($x + $cell / 2 - 8, $row * $cell + $big + 10, $small, $small))
    }
}

$dc.Close()

$out = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$out.Render($visual)
$enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($out))
$fs = [IO.File]::Create($OutFile)
try { $enc.Save($fs) } finally { $fs.Dispose() }

Write-Output ("wrote {0} ({1} icons)" -f $OutFile, $icons.Count)
Write-Output ($icons.Name -join ', ')
