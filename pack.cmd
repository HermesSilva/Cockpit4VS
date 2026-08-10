@echo off
rem ===========================================================================
rem  Builds the extension and drops the .vsix into Dist\.
rem
rem  It delegates the build to build.ps1 rather than calling MSBuild itself:
rem  locating the VS MSBuild and vstest is already solved there, and two copies
rem  of that logic would drift apart.
rem
rem  Usage:
rem    pack.cmd                 Release, with the tests
rem    pack.cmd /debug          Debug instead of Release
rem    pack.cmd /skiptests      skip the test run
rem ===========================================================================

setlocal EnableDelayedExpansion

set "ROOT=%~dp0"
set "CONFIG=Release"
set "SKIP="

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="/debug"     ( set "CONFIG=Debug"      & shift & goto parse )
if /i "%~1"=="/release"   ( set "CONFIG=Release"    & shift & goto parse )
if /i "%~1"=="/skiptests" ( set "SKIP=-SkipTests"   & shift & goto parse )
if /i "%~1"=="/?"         goto usage
echo Unknown option: %~1
goto usage

:parsed

rem PowerShell 7 when it is installed, Windows PowerShell otherwise: build.ps1
rem runs under both, and requiring pwsh would fail on a clean machine.
set "PS=powershell"
where pwsh >nul 2>nul
if not errorlevel 1 set "PS=pwsh"

echo.
echo === Building %CONFIG% ===
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build.ps1" -Configuration %CONFIG% %SKIP%
if errorlevel 1 (
    echo.
    echo Build failed. Nothing was copied to Dist.
    exit /b 1
)

set "VSIX=%ROOT%src\Tootega.Cockpit\bin\%CONFIG%\Tootega.Cockpit.vsix"
if not exist "%VSIX%" (
    echo.
    echo The build reported success but %VSIX% is not there.
    exit /b 1
)

if not exist "%ROOT%Dist" mkdir "%ROOT%Dist"

rem The version comes from the manifest, so the archived name always matches what
rem the extension actually reports in the Extension Manager.
rem The shell name is not quoted here: inside a back-quoted for /f, quoting the
rem executable makes cmd treat the whole line as one token and it fails to run.
set "MANIFEST=%ROOT%src\Tootega.Cockpit\source.extension.vsixmanifest"
set "VER="
for /f "usebackq delims=" %%v in (`%PS% -NoProfile -Command "([xml](Get-Content -LiteralPath '%MANIFEST%')).PackageManifest.Metadata.Identity.Version"`) do set "VER=%%v"

rem Two copies on purpose: a versioned one to keep, and a stable name to point
rem the installer at without editing the command every release.
copy /y "%VSIX%" "%ROOT%Dist\Tootega.Cockpit.vsix" >nul
if errorlevel 1 (
    echo Could not copy the VSIX into Dist. Is it open in another program?
    exit /b 1
)

if defined VER (
    copy /y "%VSIX%" "%ROOT%Dist\Tootega.Cockpit-%VER%.vsix" >nul
)

echo.
echo === Packed ===
for %%f in ("%ROOT%Dist\Tootega.Cockpit.vsix") do echo   %%~ff  (%%~zf bytes^)
if defined VER echo   %ROOT%Dist\Tootega.Cockpit-%VER%.vsix
echo.
echo Install with Visual Studio CLOSED:
echo   VSIXInstaller.exe "%ROOT%Dist\Tootega.Cockpit.vsix"
exit /b 0

:usage
echo.
echo   pack.cmd [/debug ^| /release] [/skiptests]
echo.
echo   Builds the extension and copies the .vsix into Dist\.
exit /b 1
