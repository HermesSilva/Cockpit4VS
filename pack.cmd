@echo off
rem ===========================================================================
rem  Builds the extension and drops the .vsix into Dist\.
rem
rem  It delegates the build to build.ps1 rather than calling MSBuild itself:
rem  locating the VS MSBuild and vstest is already solved there, and two copies
rem  of that logic would drift apart.
rem
rem  The build number is incremented BEFORE compiling, so the .vsix that lands in
rem  Dist is never the same version as the one already installed - Visual Studio
rem  skips an install whose version it believes it already has.
rem
rem  Usage:
rem    pack.cmd                 bump, build Release, run the tests
rem    pack.cmd /debug          Debug instead of Release
rem    pack.cmd /skiptests      skip the test run
rem    pack.cmd /nobump         keep the current version
rem    pack.cmd /minor          increment the minor instead of the build
rem    pack.cmd /install        install/update the packed VSIX at the end
rem    pack.cmd /noinstall      skip the install step (default)
rem ===========================================================================

setlocal EnableDelayedExpansion

set "ROOT=%~dp0"
set "CONFIG=Release"
set "SKIP="
set "BUMP=1"
set "PART=Build"
set "INSTALL="

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="/debug"     ( set "CONFIG=Debug"      & shift & goto parse )
if /i "%~1"=="/release"   ( set "CONFIG=Release"    & shift & goto parse )
if /i "%~1"=="/skiptests" ( set "SKIP=-SkipTests"   & shift & goto parse )
if /i "%~1"=="/nobump"    ( set "BUMP="             & shift & goto parse )
if /i "%~1"=="/minor"     ( set "PART=Minor"        & shift & goto parse )
if /i "%~1"=="/major"     ( set "PART=Major"        & shift & goto parse )
if /i "%~1"=="/install"   ( set "INSTALL=1"         & shift & goto parse )
if /i "%~1"=="/noinstall" ( set "INSTALL="          & shift & goto parse )
if /i "%~1"=="/?"         goto usage
echo Unknown option: %~1
goto usage

:parsed

rem PowerShell 7 when it is installed, Windows PowerShell otherwise: build.ps1
rem runs under both, and requiring pwsh would fail on a clean machine.
set "PS=powershell"
where pwsh >nul 2>nul
if not errorlevel 1 set "PS=pwsh"

if defined BUMP (
    echo.
    echo === Bumping the version ===
    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\bump-version.ps1" -Part %PART%
    if errorlevel 1 (
        echo.
        echo Could not bump the version. Nothing was built.
        exit /b 1
    )
)

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

if not defined INSTALL (
    echo.
    echo Install with Visual Studio CLOSED:
    echo   VSIXInstaller.exe "%ROOT%Dist\Tootega.Cockpit.vsix"
    exit /b 0
)

rem VSIXInstaller lives beside devenv; vswhere locates the same instance build.ps1
rem builds against, so the install lands where the extension was compiled for.
set "VSIXINSTALLER="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo.
    echo vswhere not found - is Visual Studio installed? The VSIX was packed but not installed.
    exit /b 1
)
for /f "usebackq delims=" %%p in (`"%VSWHERE%" -prerelease -latest -property installationPath`) do set "VSPATH=%%p"
if defined VSPATH set "VSIXINSTALLER=%VSPATH%\Common7\IDE\VSIXInstaller.exe"

if not defined VSIXINSTALLER (
    echo.
    echo Could not find a Visual Studio installation. The VSIX was packed but not installed.
    exit /b 1
)
if not exist "%VSIXINSTALLER%" (
    echo.
    echo VSIXInstaller.exe not found at "%VSIXINSTALLER%". The VSIX was packed but not installed.
    exit /b 1
)

rem devenv holds a lock on installed extensions; installing while it runs fails with
rem a file-in-use error, so refuse rather than leave a half-applied install.
tasklist /fi "imagename eq devenv.exe" 2>nul | find /i "devenv.exe" >nul
if not errorlevel 1 (
    echo.
    echo Visual Studio is running. Close every instance before installing, then rerun with /install.
    exit /b 1
)

echo.
echo === Installing ===
rem /q is silent; the installer updates in place when the same extension id is already
rem present, so this both installs on a clean machine and updates on a repeat run.
"%VSIXINSTALLER%" /q "%ROOT%Dist\Tootega.Cockpit.vsix"
set "RC=%errorlevel%"

rem 1001 = this exact version is already installed; that is a success for a repeat run.
if "%RC%"=="1001" (
    echo Already installed at this version - nothing to update.
    exit /b 0
)
if not "%RC%"=="0" (
    echo.
    echo Install failed with code %RC%.
    exit /b %RC%
)

echo Installed. Restart Visual Studio to load the new version.
exit /b 0

:usage
echo.
echo   pack.cmd [/debug ^| /release] [/skiptests] [/nobump] [/minor ^| /major] [/install]
echo.
echo   Increments the build number, builds the extension and copies the .vsix
echo   into Dist\ under both a versioned and a stable name. With /install it also
echo   installs or updates the packed VSIX (Visual Studio must be closed).
exit /b 1
