# Renders the extension icon (Extension Manager / Marketplace tile) from the same
# gauge geometry as the 16px command icon, so the identity is one drawing at two sizes.
#
# The stroke is mid-grey rather than near-black: this PNG is static, and the Extension
# Manager shows it over both light and dark chrome.
param(
    [int]$Size = 128,
    [string]$OutFile = "$PSScriptRoot\..\src\Tootega.Cockpit\Resources\CockpitExtension.png"
)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$xaml = @"
<Viewbox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
         Width="$Size" Height="$Size">
  <Canvas Width="16" Height="16">
    <Ellipse Canvas.Left="0" Canvas.Top="0" Width="16" Height="16" Fill="#00000000" />
    <Path Stroke="#FF8A8A8A" StrokeThickness="1.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
          Data="M 3.757 13.243 A 6 6 0 1 1 12.243 13.243" />
    <Path Stroke="#FFE8792B" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
          Data="M 8 9 L 10.9 5.4" />
    <Ellipse Canvas.Left="6.85" Canvas.Top="7.85" Width="2.3" Height="2.3" Fill="#FF8A8A8A" />
  </Canvas>
</Viewbox>
"@

$element = [Windows.Markup.XamlReader]::Parse($xaml)
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

Write-Output "wrote $OutFile ($Size x $Size)"
