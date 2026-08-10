<#
.SYNOPSIS
    Publishes the packaged extension to the Visual Studio Marketplace.

.DESCRIPTION
    A thin wrapper over VsixPublisher.exe, which ships with the VS SDK. What it adds is the
    checking that a publish deserves and a command line does not do for you: that the payload
    exists, that the manifest and overview are where the manifest says, and that the version
    about to be uploaded is not one the Marketplace already has — publishing overwrites in
    place, and overwriting a released version leaves two different builds calling themselves
    the same thing.

.PARAMETER PersonalAccessToken
    A token scoped to Marketplace (Publish), created under the account that owns the
    publisher. Defaults to $env:VS_MARKETPLACE_PAT. Never commit it.

.PARAMETER Force
    Publish even when the version is already on the Marketplace.

.EXAMPLE
    ./scripts/publish.ps1
    ./scripts/publish.ps1 -Vsix Dist\Tootega.Cockpit-1.0.15.vsix -Force
#>
param(
    [string]$Vsix = "$PSScriptRoot\..\Dist\Tootega.Cockpit.vsix",
    [string]$Manifest = "$PSScriptRoot\..\marketplace\publishManifest.json",
    [string]$PersonalAccessToken = $env:VS_MARKETPLACE_PAT,
    [switch]$Force,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) { Write-Host $message -ForegroundColor Red; exit 1 }

# ---- The payload and the manifest ----

if (-not (Test-Path $Vsix)) { Fail "No .vsix at $Vsix. Run pack.cmd first." }
if (-not (Test-Path $Manifest)) { Fail "No publish manifest at $Manifest." }

$Vsix = (Resolve-Path $Vsix).Path
$Manifest = (Resolve-Path $Manifest).Path

$manifestJson = Get-Content $Manifest -Raw | ConvertFrom-Json
$publisher = $manifestJson.publisher
$internalName = $manifestJson.identity.internalName

if (-not $publisher) { Fail "The publish manifest names no publisher." }

# The overview path in the manifest is relative to the manifest itself, and a missing one
# fails deep inside the publisher with a message that does not say which file it wanted.
$overview = Join-Path (Split-Path -Parent $Manifest) $manifestJson.overview
if (-not (Test-Path $overview)) { Fail "The manifest points at an overview that is not there: $overview" }

# ---- The version, read from the VSIX itself rather than from a second place ----

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($Vsix)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName -eq 'extension.vsixmanifest' }
    if (-not $entry) { Fail "$Vsix carries no extension.vsixmanifest." }

    $reader = New-Object System.IO.StreamReader($entry.Open())
    try { $vsixManifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
}
finally { $archive.Dispose() }

$identity = $vsixManifest.PackageManifest.Metadata.Identity
$version = $identity.Version
$extensionId = $identity.Id

Write-Host "Publishing $extensionId $version as $publisher/$internalName"
Write-Host "  payload  : $Vsix"
Write-Host "  manifest : $Manifest"

# ---- Is this version already out there? ----

$listing = "https://marketplace.visualstudio.com/items?itemName=$publisher.$internalName"
try {
    $page = Invoke-WebRequest -Uri $listing -UseBasicParsing -TimeoutSec 20
    if ($page.Content -match [regex]::Escape("`"version`":`"$version`"")) {
        if (-not $Force) {
            Fail "Version $version is already on the Marketplace. Bump it (pack.cmd does), or pass -Force to overwrite."
        }
        Write-Host "Version $version is already published; overwriting because -Force was given." -ForegroundColor Yellow
    }
}
catch {
    # A listing that does not exist yet is the normal case for a first publish, and a
    # network hiccup is not a reason to refuse to publish.
    Write-Host "Could not read the current listing ($($_.Exception.Message)). Continuing." -ForegroundColor Yellow
}

# ---- The tool ----

$publisherExe = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Recurse -Filter 'VsixPublisher.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

if (-not $publisherExe) {
    $publisherExe = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.vssdk.buildtools" -Recurse -Filter 'VsixPublisher.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $publisherExe) { Fail 'VsixPublisher.exe was not found. Install the Visual Studio SDK component.' }

$arguments = @('publish', '-payload', $Vsix, '-publishManifest', $Manifest)
if ($PersonalAccessToken) { $arguments += @('-personalAccessToken', $PersonalAccessToken) }
else { Write-Host 'No token given; falling back to the publisher logged in on this machine.' -ForegroundColor Yellow }

if ($WhatIf) {
    Write-Host "Would run: $publisherExe publish -payload `"$Vsix`" -publishManifest `"$Manifest`"" -ForegroundColor Cyan
    exit 0
}

& $publisherExe @arguments
if ($LASTEXITCODE -ne 0) { Fail "VsixPublisher failed with exit code $LASTEXITCODE." }

Write-Host "Published. $listing" -ForegroundColor Green
