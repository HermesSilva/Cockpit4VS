# Asks the running Visual Studio to quit through DTE, so it saves and unlocks its hive.
. "$PSScriptRoot\probe-dte.ps1" *> $null
$dte = [Rot]::Find('VisualStudio.DTE')
if ($dte) { try { $dte.Quit() } catch { } }

for ($i = 0; $i -lt 60; $i++) {
    if (-not (Get-Process devenv -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 1
}

if (Get-Process devenv -ErrorAction SilentlyContinue) { 'devenv is still running.' } else { 'devenv closed.' }
