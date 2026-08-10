# Renders the extension icon (Extension Manager / Marketplace tile) from the very drawing
# the 16px command icon uses, so the identity is one file at two sizes and the tile cannot
# drift away from the mark shown inside the IDE.
#
# The source is the product mark carried over from the VS Code extension, so the listing in
# both stores shows the same logo.
param(
    [int]$Size = 128,
    [string]$Source = "$PSScriptRoot\..\src\Tootega.Cockpit\Resources\Icons\Cockpit.xaml",
    [string]$OutFile = "$PSScriptRoot\..\src\Tootega.Cockpit\Resources\CockpitExtension.png"
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$element = [Windows.Markup.XamlReader]::Parse((Get-Content -LiteralPath $Source -Raw))

# The XAML is authored on the 16px grid; the Viewbox does the scaling, so the only thing
# that changes with $Size is how much resolution the tile gets.
$element.Width = $Size
$element.Height = $Size
$element.Measure([Windows.Size]::new($Size, $Size))
$element.Arrange([Windows.Rect]::new(0, 0, $Size, $Size))
$element.UpdateLayout()

$bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new(
    $Size, $Size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($element)

$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$stream = [IO.File]::Create($OutFile)
try { $encoder.Save($stream) } finally { $stream.Dispose() }

Write-Output "wrote $OutFile ($Size x $Size) from $(Split-Path -Leaf $Source)"
