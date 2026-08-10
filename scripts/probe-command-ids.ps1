# Asks the running IDE for the Cockpit commands by guid and id.
# Canonical names come from where a command is placed, not from <CommandName>, so a lookup
# by name proves nothing on its own; the guid/id pair is what the command table stores.
. "$PSScriptRoot\probe-dte.ps1" *> $null
$dte = [Rot]::Find('VisualStudio.DTE')
if (-not $dte) { 'No running Visual Studio found in the ROT.'; return }

$cmdSet = '{8b14bea4-9c47-451d-8143-63d452bc8422}'

foreach ($id in 0x100, 0x101, 0x102, 0x10F) {
    try {
        $c = $dte.Commands.Item($cmdSet, $id)
        "[ ok ] id=0x{0:X}  name='{1}'  enabled={2}" -f $id, $c.Name, $c.IsAvailable
    }
    catch {
        "[fail] id=0x{0:X} — {1}" -f $id, $_.Exception.Message
    }
}
