<#
.SYNOPSIS
    Builds the extension and runs the unit tests.

.DESCRIPTION
    Uses the VS MSBuild rather than `dotnet build`: the VSIX project is a classic
    (non-SDK) csproj, and the .NET SDK cannot resolve its VSSDK references. Likewise the
    tests run under vstest.console, not `dotnet test`, for the same reason.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Configuration Release
    ./build.ps1 -SkipTests
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipTests,

    # Installs the VSIX into the experimental instance as part of the build.
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere not found — is Visual Studio installed?" }

$vsPath = & $vswhere -prerelease -latest -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsPath) { throw "No Visual Studio installation with MSBuild was found." }

$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
$vstest = Join-Path $vsPath 'Common7\IDE\Extensions\TestPlatform\vstest.console.exe'

Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
& $msbuild (Join-Path $root 'Cockpit4VS.sln') `
    /t:Restore,Build `
    /p:Configuration=$Configuration `
    /p:DeployExtension=$(if ($Deploy) { 'true' } else { 'false' }) `
    /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$vsix = Join-Path $root "src\Tootega.Cockpit\bin\$Configuration\Tootega.Cockpit.vsix"
if (Test-Path $vsix) {
    $kb = [math]::Round((Get-Item $vsix).Length / 1KB)
    Write-Host "VSIX: $vsix ($kb KB)" -ForegroundColor Green
}

if ($SkipTests) { return }

$testDll = Join-Path $root "tests\Tootega.Cockpit.Tests\bin\$Configuration\net472\Tootega.Cockpit.Tests.dll"
if (-not (Test-Path $testDll)) { throw "Test assembly not found at $testDll" }

Write-Host "Running tests..." -ForegroundColor Cyan
& $vstest $testDll /Logger:"console;verbosity=minimal"
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
