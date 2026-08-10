# Looks for the Cockpit's command set inside the IDE's merged command table.
# devenv.CTM is what the shell actually draws its menus from: if the command set guid is
# not in there, the contribution never reached the merge, whatever the registry says.
param([string]$Ctm = "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_f2f12ba4\1033\devenv.CTM")

$bytes = [System.IO.File]::ReadAllBytes($Ctm)
"CTM: {0} ({1:N0} bytes, {2})" -f $Ctm, $bytes.Length, (Get-Item $Ctm).LastWriteTime

function CountBytes([byte[]] $haystack, [byte[]] $needle) {
    $n = 0
    for ($i = 0; $i -le $haystack.Length - $needle.Length; $i++) {
        $hit = $true
        for ($j = 0; $j -lt $needle.Length; $j++) {
            if ($haystack[$i + $j] -ne $needle[$j]) { $hit = $false; break }
        }
        if ($hit) { $n++ }
    }
    return $n
}

foreach ($pair in @(
    @{ name = 'guidCockpitCmdSet'; guid = '8b14bea4-9c47-451d-8143-63d452bc8422' },
    @{ name = 'guidCockpitPackage'; guid = '92c17b2d-a9a9-460d-a1e2-d48f8f21e29f' },
    @{ name = 'guidCockpitImages'; guid = '092f81e8-c52d-446a-a584-57de6f62f2a1' })) {

    $raw = ([Guid]$pair.guid).ToByteArray()
    "{0,-20} {1}" -f $pair.name, (CountBytes $bytes $raw)
}

$text = [System.Text.Encoding]::Unicode.GetString($bytes)
foreach ($term in 'Tootega Cockpit', 'Open Cockpit', 'Tootega.Open') {
    "{0,-20} utf16 hits: {1}" -f $term, ([regex]::Matches($text, [regex]::Escape($term))).Count
}
