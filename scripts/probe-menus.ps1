# Lists what the IDE actually shows on the menus the Cockpit contributes to.
# The command table can merge and still leave nothing visible if a placement points at a
# group the shell no longer draws, so the menus themselves are what has to be looked at.
param([string[]]$Bars = @('Extensions', 'View', 'Other Windows'))

. "$PSScriptRoot\probe-dte.ps1" *> $null
$dte = [Rot]::Find('VisualStudio.DTE')
if (-not $dte) { 'No running Visual Studio found in the ROT.'; return }

foreach ($barName in $Bars) {
    try { $bar = $dte.CommandBars.Item($barName) } catch { "== $barName — not found"; continue }
    "== $barName ($($bar.Controls.Count) items)"
    foreach ($control in $bar.Controls) { "   " + $control.Caption }
}
