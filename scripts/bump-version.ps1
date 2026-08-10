<#
.SYNOPSIS
    Increments the build number and keeps every place that states a version in agreement.

.DESCRIPTION
    The version appears in three places, because each is read by something different: the
    VSIX manifest (what the Extension Manager lists), the assembly attributes (what the
    Cockpit reports to its own UI) and InstalledProductRegistration (what Help > About
    shows). They are all rewritten together — a build where they disagree is a build that
    lies to the user about which one they are running.

    The manifest is the source of truth: it is the only one a human edits when moving to a
    new minor or major, and the others are derived from it here.

.PARAMETER Part
    Which component to increment. Defaults to the build number (the third).

.PARAMETER Set
    An explicit version to write instead of incrementing, for a release.

.EXAMPLE
    ./scripts/bump-version.ps1
    ./scripts/bump-version.ps1 -Part Minor
    ./scripts/bump-version.ps1 -Set 1.2.0
#>
param(
    [ValidateSet('Major', 'Minor', 'Build')]
    [string]$Part = 'Build',

    [string]$Set
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Tootega.Cockpit'

$manifestPath = Join-Path $project 'source.extension.vsixmanifest'
$assemblyPath = Join-Path $project 'Properties\AssemblyInfo.cs'
$idsPath = Join-Path $project 'CockpitIds.cs'

foreach ($path in @($manifestPath, $assemblyPath, $idsPath)) {
    if (-not (Test-Path $path)) { throw "Not found: $path" }
}

# ---- The current version, from the manifest ----

$manifest = [System.IO.File]::ReadAllText($manifestPath)

$identity = [regex]::Match($manifest, '(?<prefix><Identity\b[^>]*?\bVersion=")(?<version>[^"]+)(?<suffix>")')
if (-not $identity.Success) { throw "Could not find the Identity version in $manifestPath" }

$current = $identity.Groups['version'].Value

if ($Set) {
    $next = $Set
}
else {
    $parts = $current.Split('.')
    # Normalized to three components: the manifest carries Major.Minor.Build, and a fourth
    # would be dropped by the Extension Manager anyway.
    while ($parts.Count -lt 3) { $parts += '0' }

    $index = switch ($Part) {
        'Major' { 0 }
        'Minor' { 1 }
        default { 2 }
    }

    $number = 0
    if (-not [int]::TryParse($parts[$index], [ref]$number)) {
        throw "Version component '$($parts[$index])' in '$current' is not a number."
    }

    $parts[$index] = [string]($number + 1)

    # Incrementing a component resets the ones below it: 1.2.7 with -Part Minor is 1.3.0,
    # not 1.3.7.
    for ($i = $index + 1; $i -lt 3; $i++) { $parts[$i] = '0' }

    $next = ($parts[0..2]) -join '.'
}

if ($next -notmatch '^\d+\.\d+\.\d+$') {
    throw "'$next' is not a Major.Minor.Build version."
}

# ---- Write all three ----

function Save([string]$path, [string]$text) {
    # No BOM: these files are tracked, and adding one would show up as a whole-file change.
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

$at = $identity.Groups['version'].Index
$manifest = $manifest.Remove($at, $current.Length).Insert($at, $next)
Save $manifestPath $manifest

# The assembly wants four components; the fourth stays zero so the assembly version and the
# manifest version can always be compared directly.
$assembly = [System.IO.File]::ReadAllText($assemblyPath)
$assembly = [regex]::Replace($assembly, '(?<=AssemblyVersion\(")[^"]+(?=")', "$next.0")
$assembly = [regex]::Replace($assembly, '(?<=AssemblyFileVersion\(")[^"]+(?=")', "$next.0")
Save $assemblyPath $assembly

$ids = [System.IO.File]::ReadAllText($idsPath)
if ($ids -notmatch 'public const string ProductVersion') {
    throw "CockpitIds has no ProductVersion constant to update."
}
$ids = [regex]::Replace($ids, '(?<=public const string ProductVersion = ")[^"]+(?=")', $next)
Save $idsPath $ids

Write-Host "Version: $current -> $next" -ForegroundColor Green
