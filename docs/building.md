# Building, testing and releasing

## Prerequisites

- **Visual Studio 2022 17.x or 2026 18.x** with the **Visual Studio extension development**
  workload. That workload brings the VS SDK, `VsixPublisher.exe` and `CreateExpInstance.exe`.
- **Node.js**, only if you intend to change the webview. The build skips the bundle step when
  Node is missing, so a C#-only contributor needs no JavaScript toolchain.

## Build and test

```powershell
./build.ps1                 # build + tests
./build.ps1 -SkipTests
./build.ps1 -Deploy         # also install into the experimental instance
./build.ps1 -Configuration Release
```

Output: `src\Tootega.Cockpit\bin\Debug\Tootega.Cockpit.vsix`.

**Use the Visual Studio MSBuild, not `dotnet build` or `dotnet test`.** The VSIX project is a
classic (non-SDK) csproj and the .NET SDK cannot resolve its VSSDK references; the tests run
under `vstest.console.exe` for the same reason. `build.ps1` locates both through `vswhere`.

The test project declares `System.Text.Json` itself, because the extension excludes it at
runtime — the IDE provides it — and the tests run outside devenv.

### The build must stay at zero warnings

The VSSDK and vs-threading analyzers catch real in-proc bugs. Suppress one only with a
`#pragma` and a comment saying why, next to the code it covers.

### The webview

The React bundle is built by the `BuildWebView` target (esbuild) before the VSIX is packaged,
so it can never be stale relative to its sources. It fails the build only when there is no
bundle at all.

**The bundle is added to the VSIX from inside that target, not by a glob.** A glob is
evaluated before any target runs, so on a clean build it would match nothing and the VSIX
would silently ship a tool window that loads nothing. If you add generated content, add it
the same way.

### Icons

```powershell
./scripts/preview-icons.ps1        # renders every icon at 16 and 32px, light and dark
./scripts/gen-extension-icon.ps1   # regenerates the 128px Marketplace tile from Cockpit.xaml
```

An icon lives in three places at once — `Resources/Icons/*.xaml`,
`Resources/Cockpit.imagemanifest` and `CockpitMonikers.cs`, plus the `guidCockpitImages`
symbols in the `.vsct`. A mismatch shows up as a blank icon, not a build error, so change all
of them together.

## Debugging

F5 launches the experimental instance (`devenv /rootsuffix Exp`) with the extension deployed.

When that instance gets into a strange state, reset it rather than fighting it:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\VSSDK\VisualStudioIntegration\Tools\Bin\CreateExpInstance.exe" /Reset /VSInstance=<id> /RootSuffix=Exp
```

`vswhere -all -format json` gives the instance id.

For problems in the real instance, start it with `devenv /log` and read
`%APPDATA%\Microsoft\VisualStudio\<hive>\ActivityLog.xml`. The extension's own log is in
**View > Output**, pane *Tootega Cockpit*; turn on **Advanced > Debug logging** first.

Diagnostic helpers live in `scripts/`:

| Script | What it answers |
|---|---|
| `check-install.ps1` | Is it installed, is the command table in the assembly, was the pkgdef applied? |
| `probe-menus.ps1` | What is actually on the Extensions / View menus of the running IDE? |
| `probe-commands.ps1` | Which of our commands did the IDE merge? |
| `probe-titlebar.ps1` | Did the title bar button get placed? |
| `reinstall.ps1` | Uninstall then install, with the IDE closed |
| `close-vs.ps1` | Ask a running IDE to quit, and wait for it |

## Versioning

```powershell
./scripts/bump-version.ps1 -Part Build     # or Minor, Major
```

The version is stated in three files — `source.extension.vsixmanifest`, `AssemblyInfo.cs` and
`CockpitIds.ProductVersion` — and the script rewrites all three. They are three statements of
one fact and must never disagree.

## Packaging

```powershell
pack.cmd                    # bump the build number, build Release, run tests, copy to Dist\
pack.cmd /skiptests
pack.cmd /nobump
pack.cmd /minor
```

`Dist\` gets two copies: `Tootega.Cockpit.vsix`, a stable name to point the installer at, and
`Tootega.Cockpit-<version>.vsix` to keep.

The bump happens **before** the build on purpose: Visual Studio skips installing a `.vsix`
whose version it believes it already has, and debugging that looks exactly like the build
having done nothing.

## Installing a local build

With Visual Studio closed:

```powershell
./scripts/reinstall.ps1
```

Uninstall-then-install rather than install-over: a full reinstall is what makes Visual Studio
redo its menu merge.

## Publishing

```powershell
$env:VS_MARKETPLACE_PAT = '<token scoped to Marketplace (Publish)>'
./scripts/publish.ps1 -WhatIf     # show the command without running it
./scripts/publish.ps1
```

See [`marketplace/README.md`](../marketplace/README.md) for what the listing is made of and
what has to exist before the first publish.

Release checklist:

1. `pack.cmd` — bumps, builds Release, runs the tests.
2. Install the packaged `.vsix` on a clean instance and confirm the Cockpit opens, a
   conversation streams, and the Hub reports consumption.
3. Add the release to `CHANGELOG.md`.
4. `./scripts/publish.ps1`.
5. Tag the commit with the version.
