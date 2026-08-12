# Architecture

This is a conversion of the VS Code extension `D:\Tootega\Source\Cockpit` to a native
Visual Studio extension. The founding principle is unchanged: **the Cockpit is a
presentation and control layer over the Claude Code CLI.** The agent loop, tools,
subagents, todos, context, compaction, permissions, MCP, hooks and skills all live in the
CLI. We render its event stream and implement the client side of its interactive
protocols. We never reimplement orchestration.

The wire contract is unchanged too — the same `stream-json` command line, the same NDJSON
events, the same host↔webview message shapes.

## What the conversion changed

| Layer | VS Code | Visual Studio |
|---|---|---|
| Extension model | Node extension host | VSIX (VSSDK classic), `AsyncPackage`, in-proc |
| Target framework | Node 20 | .NET Framework 4.7.2 — devenv 18.x still runs on it |
| Host language | TypeScript (`src/`) | C# (`src/Tootega.Cockpit/`) |
| UI shell | `WebviewPanel` / `WebviewView` | `ToolWindowPane` hosting WebView2 |
| UI content | React + Vite | **the same React bundle**, served to WebView2 |
| Settings | `contributes.configuration` | `DialogPage` (Tools > Options > Tootega Cockpit) |
| Commands | `contributes.commands` | `.vsct` command table |
| Icons | Codicons (`$(sparkle)`) | `.imagemanifest` + ImageMonikers (vector XAML) |
| Localisation | pt-BR + EN, runtime switch | English only, no i18n layer (deliberate) |

**English-only is a rule, not an omission.** There is no i18n layer, no catalogs, no locale
switching. Strings are written in place, in international English. Do not reintroduce an
i18n abstraction "for later".

## The two bridges

Almost all of the conversion's leverage comes from two adapters that let the *unmodified*
React webview run inside Visual Studio.

### `CockpitWebView.BuildShim()`

Injects a script before page load that defines `acquireVsCodeApi()` with the three members
the webview uses — `postMessage`, `getState`, `setState` — on top of `chrome.webview`, and
republishes host messages onto `window.message` where the React code listens. State goes to
`localStorage`, which preserves the draft-survives-reload guarantee. The webview cannot
tell which editor it is in.

The bundle is served from a virtual `https://cockpit.invalid/` origin rather than `file://`,
so the page gets a normal security context and `localStorage`, modules and `fetch` behave
as they do in VS Code.

### `VsThemeBridge.BuildCss()`

Publishes the `--vscode-*` CSS custom properties the webview's stylesheets read, filled
from VS theme colours (`EnvironmentColors`, `CommonControlsColors`, `TreeViewColors`), and
re-applied on `VSColorTheme.ThemeChanged`. Tokens VS has no equivalent for — chart palette,
diff bands, error red — are derived from background luminosity, never hardcoded for one
theme.

It also publishes `color-scheme` and explicit `::-webkit-scrollbar` rules. VS Code's
webview host styles scrollbars itself; WebView2 does not, so without this the browser
paints its light-scheme scrollbars over a dark tool window.

**Consequence: do not fork the webview's CSS or its `vscodeApi.ts` for Visual Studio.** If
something looks wrong, fix the *bridge*. A divergence there is a divergence forever.

## Substitutions

Four things had no .NET equivalent and were replaced rather than translated. Each is a
decision, not an accident:

| Original | Here | Why |
|---|---|---|
| `hunspell-asm` (WASM) | `WeCantSpell.Hunspell` | Fully managed, reads the same `.aff`/`.dic`, no native binary per architecture. Covered by live tests against the real dictionaries. |
| VS Code `SecretStorage` | Windows Credential Manager (P/Invoke) | VS has no equivalent. Not DPAPI-over-a-file: the credential manager is inspectable and revocable through a normal Windows UI. |
| `qrcode` (npm) | `QRCoder` | Inline SVG, so the enrolment secret never touches disk. |
| `HttpListener` for OTEL | raw `TcpListener` | `HttpListener` needs a URL reservation on Windows; asking for an elevated command before a convenience feature works is the worse trade. |

Two more are shape changes rather than library swaps: `shell: true` became `cmd.exe /s /c`,
and clipboard file paths come from the WPF clipboard instead of a PowerShell round-trip,
which also removed the original's code-page workaround.

## Windows

`ChatToolWindow` is registered with `MultiInstances = true`: one window per conversation,
each with its own folder scope and its own CLI process. Instance ids are the shell's handle
on a multi-instance window and must be stable for the window's life; the tab id is ours,
and the two are matched by walking the open instances rather than by keeping a map that
could drift.

`HubToolWindow` is single-instance and always reflects the active conversation.

## Icons

Vector XAML in `Resources/Icons/`, registered in `Resources/Cockpit.imagemanifest`, exposed
to C# through `CockpitMonikers` and to the command table through `guidCockpitImages`. The
three must stay in sync — a mismatch shows up as a blank icon, not a build error.

Rules: neutral stroke `#2B2B2B` (the ImageService inverts luminosity for dark themes),
orange `#E8792B` as the only chromatic element, 16px grid, ~1.3–1.5px strokes. Preview with
`scripts/preview-icons.ps1` before committing art.

The product mark (`Cockpit.xaml`, moniker 1000) is the one exception: it is the logo
carried over from the VS Code extension, keeps its own colours, and has
`AllowColorInversion="false"` so the theme cannot repaint a brand.

## The title bar button

`UI/TitleBarButton.cs` grafts a button onto the shell's own title bar, beside Copilot's.
There is no supported way to do this — the title bar has no group in `vsshlids.h` and no
extension point — so the code is written for the day it stops working: the search is
geometric rather than by control name, every step can come back empty, and coming back
empty means no button, never an exception and never a broken title bar. When it fails it
writes the top of the visual tree to `%TEMP%\tootega-cockpit-titlebar.txt` and says so in
the output pane.

It is a user option (`Interface > Title bar button`), on by default, and applying the
option adds or removes the button without a restart.

## The browser control

`UI/VsWebView2.cs` is the WebView2 the tool windows host, and it differs from the stock one
in two ways that the IDE forces.

**Composition rather than a child window.** The ordinary WPF `WebView2` is an `HwndHost`,
and a child window always paints above WPF whatever the z-order says. In an IDE whose
panels slide over the editor that is not cosmetic: an unpinned Output or Error List came out
*underneath* the conversation. `WebView2CompositionControl` renders the page into a WPF
visual, so the shell's own surfaces overlap it like anything else.

**Keyboard.** A hosted browser is otherwise a keyboard black hole — the content takes every
key, so F5 did not start the debugger and Ctrl+Shift+B did not build. Every keystroke is
offered to the shell first through `IVsFilterKeys2`, which resolves it against the user's
own bindings and the active context, and only what the shell does not claim continues into
the page. Two hooks, because which one a key takes is not ours to decide: the browser's
`AcceleratorKeyPressed` (where F5 surfaces) and `PreviewKeyDown` (where the control feeds
keys back as WPF input).

The routing policy is deliberately narrow. A bare key is only offered when it cannot be
text — the function keys. Ctrl and Alt combinations are offered except the ones the composer
implements, and the caret keys (Home, End, PageUp, PageDown) are never offered at all: the
IDE has nothing worth binding to them inside a text box, and a caret that stops moving is
felt on every line. Modifiers are read from the keyboard with `GetKeyState` rather than from
`Keyboard.Modifiers`, because WPF's view of a keyboard being typed into another process can
be stale — and a stale Alt turns every keystroke into a candidate command.

## Threading

In-proc VSIX code runs inside devenv. Two rules that are not negotiable:

- Anything touching VS services runs on the UI thread — `SwitchToMainThreadAsync()`, or
  `ThreadHelper.ThrowIfNotOnUIThread()` to assert it.
- No `async void`. Event handlers hand work to
  `JoinableTaskFactory.RunAsync(...).FileAndForget(...)`. An unobserved exception in an
  `async void` takes the IDE down with it.

Two deliberate exceptions, both documented where they live:

- `Log` writes through `OutputStringThreadSafe`, so CLI reader threads can log freely. The
  pane is created once, on the UI thread, during package initialization.
- `CockpitWebView.PostMessage` is callable from any thread — the CLI reader threads produce
  most host→webview messages, and a WPF `WebView2` only accepts its own thread. It switches
  unconditionally through the `JoinableTaskFactory`; when the caller is already on the main
  thread the await completes inline, so a session's stream stays in order. The private
  `Send` it lands in deliberately does *not* assert the thread, because that assertion
  would give every caller a main-thread contract `PostMessage` does not have.

## Layout

```
src/Tootega.Cockpit/
  CockpitPackage.cs        AsyncPackage: registration, tool windows, options
  CockpitPackage.vsct      command table (menus, groups, key bindings)
  CockpitCommands.cs       binds every .vsct entry to its handler
  CockpitIds.cs            guids, command ids, product version
  Cli/                     spawning and talking to the Claude Code CLI
  Host/                    orchestration: message router, broadcaster, host service
  Protocol/                the host↔webview message shapes
  Session/                 per-conversation state machine
  Settings/ Options/       Tools > Options and persisted settings
  Secrets/                 Windows Credential Manager, credential vault
  Spell/ Voice/ Stats/     spell-checker, dictation, consumption accounting
  UI/                      tool windows, WebView2 host, theme bridge, title bar button
  Util/                    logging, file and state stores
  Resources/               image manifest and vector icons
  WebView/                 index.html (source) + the built bundle
webview/                   React sources and the esbuild script
shared/                    protocol and event types shared with the VS Code extension
tests/                     unit tests (vstest)
```
