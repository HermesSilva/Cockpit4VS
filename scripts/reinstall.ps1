<#
.SYNOPSIS
    Uninstalls the installed Cockpit and installs the one in Dist, with the IDE closed.

.DESCRIPTION
    A full uninstall/install is the only sequence that makes Visual Studio redo the menu
    merge from scratch; updating in place, or re-running the configuration, can leave the
    previous result standing. Both steps run silently and the IDE is closed first, because
    the installer defers everything it cannot do while devenv holds the hive.

    The configuration update afterwards is not belt and braces. The installer finishes by
    leaving an `extensions.configurationchanged` marker for the shell to act on at its next
    start, and on some Visual Studio 2026 installations that never happens: the extension is
    on disk, enabled in the caches, and absent from both the IDE and its own list of
    installed extensions. Applying it here makes the install mean what it says.
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

$devenv = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\18' -Recurse -Filter 'devenv.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -like '*\Common7\IDE' } |
    Select-Object -First 1 -ExpandProperty FullName

if ($devenv) {
    Write-Host 'Applying the pending extension configuration…'
    $u = Start-Process $devenv -ArgumentList '/updateconfiguration' -PassThru -Wait
    Write-Host "  exit $($u.ExitCode)"
}
else {
    Write-Host 'devenv.exe was not found; run "devenv /updateconfiguration" before starting the IDE.' -ForegroundColor Yellow
}
