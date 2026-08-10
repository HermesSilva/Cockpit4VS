# Counts telling strings inside the IDE's private registry hive, ASCII and UTF-16 alike.
# A hive stores key/value names as ASCII when they fit and string data as UTF-16, so a term
# has to be looked for in both encodings before "absent" means anything.
param([string]$Hive = "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_f2f12ba4\privateregistry.bin")

# Shared read: the hive stays mounted for a while after devenv exits, and an exclusive
# open would fail for a file nothing is actually writing to.
$stream = [System.IO.File]::Open($Hive, 'Open', 'Read', 'ReadWrite')
try {
    $bytes = New-Object byte[] $stream.Length
    $read = 0
    while ($read -lt $bytes.Length) {
        $n = $stream.Read($bytes, $read, $bytes.Length - $read)
        if ($n -le 0) { break }
        $read += $n
    }
}
finally { $stream.Dispose() }
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
$wide = [System.Text.Encoding]::Unicode.GetString($bytes)

function Count([string]$text, [string]$term) {
    $n = 0; $i = 0
    while (($i = $text.IndexOf($term, $i, [StringComparison]::OrdinalIgnoreCase)) -ge 0) { $n++; $i++ }
    return $n
}

foreach ($term in 'CockpitPackage', 'Tootega.Cockpit.CockpitPackage', '92c17b2d-a9a9-460d-a1e2-d48f8f21e29f',
                  'AGcaROPackage', '025797b4-cb36-4d93-81cb-697add028d1c', 'Menus.ctmenu') {
    "{0,-42} ascii={1,-4} utf16={2}" -f $term, (Count $ascii $term), (Count $wide $term)
}
