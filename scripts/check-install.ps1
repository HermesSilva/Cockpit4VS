<#
.SYNOPSIS
    Validates an installed Cockpit against the reasons its menus can fail to appear.

.DESCRIPTION
    A VSIX can install cleanly and still contribute no menu, and the failure is silent:
    Visual Studio reads the command table from a resource inside the assembly, and if the
    resource is missing, the pkgdef was not applied, or the menu cache is stale, the result
    looks identical — nothing in the menus.

    This checks each of those separately, so the answer is a specific cause rather than
    "it doesn't work". Run it with Visual Studio CLOSED for the registry check to be
    conclusive; the rest works either way.

.EXAMPLE
    ./scripts/check-install.ps1
#>
param(
    # Defaults to the newest VS 18 user hive.
    [string]$Hive
)

$ErrorActionPreference = 'Stop'

$packageGuid = '92c17b2d-a9a9-460d-a1e2-d48f8f21e29f'
$identity = 'Tootega.Cockpit'

function Say([string]$state, [string]$text) {
    $colour = switch ($state) {
        'ok'   { 'Green' }
        'bad'  { 'Red' }
        default { 'Yellow' }
    }
    $mark = switch ($state) {
        'ok'   { '[ ok ]' }
        'bad'  { '[fail]' }
        default { '[ ?? ]' }
    }
    Write-Host "$mark $text" -ForegroundColor $colour
}

# ---- 1. Is it installed, and where? ----

if (-not $Hive) {
    $Hive = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^18\.\d+_[0-9a-f]+$' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Hive -or -not (Test-Path $Hive)) {
    Say bad "No Visual Studio 18 user hive found."
    return
}

Write-Host "Hive: $Hive"

$folder = Get-ChildItem (Join-Path $Hive 'Extensions') -Directory -ErrorAction SilentlyContinue |
    Where-Object {
        $manifest = Join-Path $_.FullName 'extension.vsixmanifest'
        (Test-Path $manifest) -and ((Get-Content $manifest -Raw) -match [regex]::Escape($identity))
    } |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $folder) {
    Say bad "The extension is not installed in this hive. Install the VSIX with Visual Studio closed."
    return
}

Say ok "Installed at $folder"

# ---- 2. Is the assembly there, with the command table inside it? ----

$dll = Join-Path $folder 'Tootega.Cockpit.dll'
if (-not (Test-Path $dll)) {
    Say bad "Tootega.Cockpit.dll is missing — the install did not finish."
    return
}

$assembly = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($dll))

$cto = $null
foreach ($name in $assembly.GetManifestResourceNames()) {
    if ($name -notlike '*.resources') { continue }

    $reader = New-Object System.Resources.ResourceReader($assembly.GetManifestResourceStream($name))
    try {
        foreach ($entry in $reader) {
            if ($entry.Key -eq 'Menus.ctmenu') { $cto = $entry.Value }
        }
    }
    finally {
        $reader.Dispose()
    }
}

if ($null -eq $cto) {
    Say bad "The assembly carries no 'Menus.ctmenu' resource — the .vsct was not merged at build time."
    return
}

Say ok ("The command table is embedded ({0:N0} bytes)." -f $cto.Length)

# The table's own header says which format it is in. 'CFCT' is the compressed form vsct.exe
# emits, so its contents are not searchable as raw bytes — the header and a plausible size
# are what can be checked, and an empty table would be neither.
$header = [System.Text.Encoding]::ASCII.GetString($cto, 0, 4)

if ($header -eq 'CFCT') { Say ok "The table is a compiled, compressed command table (CFCT)." }
elseif ($header -eq 'CTMU') { Say ok "The table is a compiled command table (CTMU)." }
else { Say bad "The 'Menus.ctmenu' resource is not a command table (header '$header')." }

# ---- 3. Does the pkgdef register the menus? ----

$pkgdef = Join-Path $folder 'Tootega.Cockpit.pkgdef'
if (-not (Test-Path $pkgdef)) {
    Say bad "Tootega.Cockpit.pkgdef is missing — nothing registers the package."
    return
}

$pkgdefText = Get-Content $pkgdef -Raw

if ($pkgdefText -match '\[\$RootKey\$\\Menus\]') { Say ok "The pkgdef registers a menu resource." }
else { Say bad "The pkgdef has no [`$RootKey`$\Menus] section — ProvideMenuResource is missing." }

if ($pkgdefText -match [regex]::Escape($packageGuid)) { Say ok "The pkgdef carries the package guid." }
else { Say bad "The pkgdef does not mention the expected package guid." }

# ---- 4. Was the pkgdef actually applied to the IDE's private registry? ----

$registry = Join-Path $Hive 'privateregistry.bin'
if (-not (Test-Path $registry)) {
    Say warn "No privateregistry.bin in this hive yet."
    return
}

try {
    $stream = [System.IO.File]::Open($registry, [System.IO.FileMode]::Open,
                                     [System.IO.FileAccess]::Read,
                                     [System.IO.FileShare]::ReadWrite)
}
catch {
    Say warn "privateregistry.bin is locked — close Visual Studio and run this again to check whether the pkgdef was applied."
    return
}

try {
    $buffer = New-Object byte[] $stream.Length
    $read = 0
    while ($read -lt $buffer.Length) {
        $n = $stream.Read($buffer, $read, $buffer.Length - $read)
        if ($n -le 0) { break }
        $read += $n
    }
}
finally {
    $stream.Dispose()
}

# Registry hives store names as UTF-16; the value data we look for is ASCII.
$wide = [System.Text.Encoding]::Unicode.GetString($buffer, 0, $read)
$narrow = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)

$registered = $wide.IndexOf($packageGuid, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
              $narrow.IndexOf($packageGuid, [StringComparison]::OrdinalIgnoreCase) -ge 0

if ($registered) {
    Say ok "The package is registered in the IDE's private registry."
    Write-Host ""
    Write-Host "Everything checks out. If the menus are still missing, the menu cache is stale:" -ForegroundColor Cyan
    Write-Host '  & "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" /updateconfiguration'
}
else {
    Say bad "The pkgdef was never applied: the package guid is absent from the private registry."
    Write-Host ""
    Write-Host "That is why no menu appears. With Visual Studio closed, run:" -ForegroundColor Cyan
    Write-Host '  & "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" /updateconfiguration'
}
