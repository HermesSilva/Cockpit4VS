<#
.SYNOPSIS
    Uninstalls the installed Cockpit and installs the one in Dist, with the IDE closed.

.DESCRIPTION
    A full uninstall/install is the only sequence that makes Visual Studio redo the menu
    merge from scratch; updating in place, or re-running the configuration, can leave the
    previous result standing. Both steps run silently and the IDE is closed first, because
    the installer defers everything it cannot do while devenv holds the hive.
#>
param(
    [string]$Vsix = "$PSScriptRoot\..\Dist\Tootega.Cockpit.vsix",
    [string]$Identity = 'Tootega.Cockpit.a0f4d7c2-6b1e-4f8a-9c3d-2e5b7a1f0d64'
)

$ErrorActionPreference = 'Stop'

$installer = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\18' -Recurse -Filter 'VSIXInstaller.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $installer) { throw 'VSIXInstaller.exe was not found under the Visual Studio 18 installation.' }

& "$PSScriptRoot\close-vs.ps1" | Write-Host
Start-Sleep -Seconds 2

Write-Host "Uninstalling $Identity…"
$u = Start-Process $installer -ArgumentList "/quiet", "/uninstall:$Identity" -PassThru -Wait
Write-Host "  exit $($u.ExitCode)"

Write-Host "Installing $Vsix…"
$i = Start-Process $installer -ArgumentList "/quiet", "`"$Vsix`"" -PassThru -Wait
Write-Host "  exit $($i.ExitCode)"
