# CLAUDE.md — Cockpit for Visual Studio

> Directives for anyone (human or agent) working in this repository.
> This project is a **conversion** of `D:\Tootega\Source\Cockpit` (the VS Code extension)
> to a native **Visual Studio 2026** extension. Read that repo's `CLAUDE.md` for the
> product rationale; this file covers only what the conversion changes.

---

## 1. What carries over unchanged

The founding principle is the same: **this is a presentation and control layer over the
Claude Code CLI.** The agent loop, tools, subagents, todos, context, compaction,
permissions, MCP, hooks and skills all live in the CLI. We render its event stream and
implement the client side of its interactive protocols. We never reimplement orchestration.

The wire contract is also unchanged — the same `stream-json` command line, the same
NDJSON events, the same host↔webview message shapes.

## 2. What the conversion changes

| Layer | VS Code | Visual Studio |
|---|---|---|
| Extension model | Node extension host | **VSIX (VSSDK classic), `AsyncPackage`, in-proc** |
| Target framework | Node 20 | **.NET Framework 4.7.2** — devenv 18.x still runs on it |
| Host language | TypeScript (`src/`) | **C#** (`src/Tootega.Cockpit/`) |
| UI shell | `WebviewPanel` / `WebviewView` | **`ToolWindowPane` hosting WebView2** |
| UI content | React + Vite | **the same React bundle**, served to WebView2 |
| Settings | `contributes.configuration` | **`DialogPage`** (Tools > Options > Tootega Cockpit) |
| Commands | `contributes.commands` | **`.vsct`** command table |
| Icons | Codicons (`$(sparkle)`) | **`.imagemanifest` + ImageMonikers** (vector XAML) |
| i18n | pt-BR + EN, runtime switch | **English only, no i18n layer** (deliberate) |

### Language rule — differs from the base repo

The VS Code Cockpit is bilingual by mandate. **This port is English-only.** There is no
i18n layer, no catalogs, no locale switching. Strings are written in place, in
international English. Do not reintroduce an i18n abstraction "for later".

Source code, comments and commit messages are English, as in the base repo.

## 3. The two bridges

Almost all of the conversion's leverage comes from two adapters that let the *unmodified*
React webview run inside Visual Studio.

**`CockpitWebView.BuildShim()`** — injects a script before page load that defines
`acquireVsCodeApi()` with the three members the webview uses (`postMessage`, `getState`,
`setState`) on top of `chrome.webview`, and republishes host messages onto `window.message`
where the React code listens. State goes to `localStorage`, preserving the draft-survives-
reload guarantee. The webview cannot tell which editor it is in.

**`VsThemeBridge.BuildCss()`** — publishes the ~59 `--vscode-*` CSS custom properties the
webview's stylesheets read, filled from VS theme colors (`EnvironmentColors`,
`CommonControlsColors`, `TreeViewColors`). Re-applied on `VSColorTheme.ThemeChanged`.
Tokens VS has no equivalent for (chart palette, diff bands, error red) are derived from
background luminosity, never hardcoded for one theme.

**Consequence:** do not fork the webview's CSS or its `vscodeApi.ts` for Visual Studio. If
something looks wrong, fix the *bridge*. A divergence there is a divergence forever.

## 4. Icons

Vector XAML in `Resources/Icons/`, registered in `Resources/Cockpit.imagemanifest`, exposed
to C# through `CockpitMonikers` and to the command table through `guidCockpitImages`. The
three must stay in sync — a mismatch shows up as a blank icon, not a build error.

Rules: neutral stroke `#2B2B2B` (the ImageService inverts luminosity for dark themes),
orange `#E8792B` as the only chromatic element, 16px grid, ~1.3–1.5px strokes. Preview with
`scripts/preview-icons.ps1` before committing art.

## 5. Threading

In-proc VSIX code runs inside devenv. Two rules that are not negotiable:

- Anything touching VS services runs on the UI thread — `SwitchToMainThreadAsync()`, or
  `ThreadHelper.ThrowIfNotOnUIThread()` to assert it.
- No `async void`. Event handlers hand work to
  `JoinableTaskFactory.RunAsync(...).FileAndForget(...)`. An unobserved exception in an
  `async void` takes the IDE down with it.

`Log` is the exception by design: the pane is created on the UI thread once, and writes go
through `OutputStringThreadSafe` so CLI reader threads can log freely.

## 6. Build and test

```powershell
./build.ps1                 # build + tests
./build.ps1 -Deploy         # also install into the experimental instance
./build.ps1 -SkipTests
```

Output: `src\Tootega.Cockpit\bin\Debug\Tootega.Cockpit.vsix`.
F5 launches the experimental instance (`devenv /rootsuffix Exp`).

**Use the VS MSBuild, not `dotnet build`/`dotnet test`.** The VSIX project is a classic
(non-SDK) csproj and the .NET SDK cannot resolve its VSSDK references; the tests run under
`vstest.console.exe` for the same reason. `build.ps1` locates both via `vswhere`.

The test project declares `System.Text.Json` itself, because the extension excludes it at
runtime (the IDE provides it) and tests run outside devenv.

The build must stay at **zero warnings**. The VSSDK and vs-threading analyzers catch real
in-proc bugs; suppress one only with a `#pragma` and a comment saying why.

Do not package assemblies `devenv.exe.config` already binds (System.Text.Json and friends)
— use `ExcludeAssets="runtime"`.

## 7. Non-goals

Same as the base repo — plus: do not add an i18n layer, and do not port the VS Code-only
affordances that have no Visual Studio counterpart (activity-bar container, editor-title
menu contributions) by inventing equivalents. Map them to VS idioms or drop them.
