# Lists the command names the running IDE knows that came from a Tootega extension.
# DTE sees every merged command, loaded package or not, so this separates "the menu
# contribution never merged" from "the package failed to load".
param([string[]]$Match = @('Tootega', 'AGcaRO', 'Cockpit'))

. "$PSScriptRoot\probe-dte.ps1" *> $null
$dte = [Rot]::Find('VisualStudio.DTE')
if (-not $dte) { 'No running Visual Studio found in the ROT.'; return }

$total = 0
$hits = @()
foreach ($command in $dte.Commands) {
    $total++
    $name = $command.Name
    if (-not $name) { continue }
    foreach ($m in $Match) {
        if ($name -like "*$m*") { $hits += ("{0}  guid={1} id={2}" -f $name, $command.Guid, $command.ID); break }
    }
}

"Commands known to the IDE: $total"
if ($hits.Count -eq 0) { 'None of them come from a Tootega extension.' } else { $hits }
