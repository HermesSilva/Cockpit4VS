# Finds which file under the user hive holds the shell's merged extension menus, by looking
# for a button caption that is known to be on a menu that works (AGcaRO's), then reporting
# whether one of ours is in the same file.
param(
    [string]$Root = "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_f2f12ba4",
    [string]$Known = 'Unlock for This Session',
    [string]$Ours = 'Open Cockpit'
)

$needles = @{ known = $Known; ours = $Ours }

Get-ChildItem $Root -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Length -gt 80MB) { return }
    try {
        $s = [System.IO.File]::Open($_.FullName, 'Open', 'Read', 'ReadWrite')
        $b = New-Object byte[] $s.Length
        $read = 0
        while ($read -lt $b.Length) { $n = $s.Read($b, $read, $b.Length - $read); if ($n -le 0) { break }; $read += $n }
        $s.Dispose()
    }
    catch { return }

    $wide = [System.Text.Encoding]::Unicode.GetString($b)
    $narrow = [System.Text.Encoding]::ASCII.GetString($b)

    $found = @()
    foreach ($key in $needles.Keys) {
        $term = $needles[$key]
        if ($wide.Contains($term) -or $narrow.Contains($term)) { $found += $key }
    }

    if ($found.Count) { "{0,-8} {1}" -f ($found -join '+'), $_.FullName }
}
