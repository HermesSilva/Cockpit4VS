# Tootega Cockpit for Claude Code — Visual Studio

> **Unofficial.** Not affiliated with, endorsed by, or sponsored by Anthropic. "Claude",
> "Claude Code" and "Anthropic" are trademarks of Anthropic, PBC, used here only to
> describe interoperability. This extension talks to the official Claude Code CLI; it does
> not bundle or redistribute it.

A rich GUI for **Claude Code**, packaged as a native **Visual Studio** extension.

The interface is **only a presentation and control layer over the Claude Code CLI**. All
orchestration — the agent loop, tools, subagents, todos, context, cache, compaction,
permissions, MCP, hooks and skills — **lives in the CLI**. The extension renders the event
stream the CLI emits and implements the client side of its interactive protocols. It never
reimplements orchestration.

| | |
|---|---|
| **Author** | Tootega Pesquisa e Inovação |
| **License** | MIT |
| **Type** | Visual Studio extension (VSIX, `AsyncPackage`, WebView2 + React) |
| **Runs on** | Visual Studio 2022 (17.x) and Visual Studio 2026 (18.x), amd64 |
| **Host framework** | .NET Framework 4.7.2 |
| **Channel to the engine** | `claude` in headless/streaming mode (`stream-json`) |
| **Engine tested against** | Claude Code CLI 2.1.x |
| **Language** | International English (no localisation layer, by design) |

This is a conversion of the [VS Code extension](https://marketplace.visualstudio.com/items?itemName=HermesSilva.tootega-cockpit)
of the same name. The React webview is *the same bundle*; what changed is the host around
it. See [`docs/architecture.md`](docs/architecture.md) for how that works.

---

## Requirements

- **Visual Studio 2022 17.0 or later**, 64-bit. Community, Professional or Enterprise.
- **Claude Code CLI** on the `PATH`, or its full path set in the options.
  Install it from Anthropic and sign in once (`claude` → `/login`), or use the
  **Sign in to Claude (CLI)** command.
- **WebView2 runtime** — already present on any machine with Visual Studio.
- **ffmpeg**, only if you want voice dictation.

The extension spawns nothing until you open a conversation. Installing it costs you a
package load and nothing else.

## Install

From the Marketplace: **Extensions > Manage Extensions**, search for *Tootega Cockpit*.

From a `.vsix`, with Visual Studio closed:

```powershell
VSIXInstaller.exe Tootega.Cockpit.vsix
```

`scripts/reinstall.ps1` does uninstall-then-install in one step, which is what you want
while iterating — Visual Studio only redoes its menu merge on a full reinstall.

## Where it lives

| Surface | How to reach it |
|---|---|
| **Cockpit** (a conversation) | Title bar button · `Extensions > Tootega Cockpit > Open Cockpit` · `Ctrl+Alt+C` |
| **Cockpit Hub** (contexts, account, consumption) | Title bar button · `Extensions > Tootega Cockpit > Open Cockpit Hub` |
| Both windows | `View > Other Windows`, and the Solution Explorer context menu on the solution and project nodes |
| Settings | `Tools > Options > Tootega Cockpit` |
| Log | `View > Output`, pane **Tootega Cockpit** |

One window per conversation: each Cockpit window has its own folder scope and its own CLI
process, so two conversations never share state.

## What it does

**Conversation.** Token-by-token streaming, thinking blocks, a tool-call timeline with a
card per tool, markdown with syntax highlighting and a line-number gutter, find in
conversation, export to Markdown, a scroll-marker rail with one mark per prompt, and a
verbosity filter that changes what you see without changing what the agent does.

**Human control.** Permission approval with a per-tool preview (Allow / Always / Deny),
permission modes including plan mode with an editable plan, composed questions
(`AskUserQuestion`) with tabs and multi-select, side-by-side diffs in the webview or in the
Visual Studio diff viewer, `@`-mentions with fuzzy file completion, and sharing the current
editor selection as a chip on the composer.

**Transparency.** Live context window and cache life, token and cost accounting per session,
turn and compaction counts, and — opt-in — a local OpenTelemetry receiver that aggregates
what the CLI reports.

**Writing aid.** An inline spell-checker (Hunspell, pt-BR + EN, flags only what is wrong in
both) that marks and never auto-corrects, a suggestions dropdown, voice dictation with live
partials, and optional AI correction of dictated text.

**Credentials.** API keys live in the Windows Credential Manager, not in a file — a store
you can inspect and revoke through a normal Windows UI. Enrolment secrets are rendered as
inline SVG QR codes, so they never touch disk.

## Settings

`Tools > Options > Tootega Cockpit`, grouped as:

- **Engine** — CLI path, engine selection, and the optional local Tootega engine.
- **Session** — model, effort, permission mode, agents, auto-resume, auto-save before
  read/write.
- **Interface** — notifications, expansion defaults, timeline verbosity, spell check, your
  display name, and the title bar button.
- **Voice** — ffmpeg path, dictation language, post-dictation correction.
- **Advanced** — internal model, custom system prompt, OTEL receiver, debug logging.

Defaults match the VS Code extension, so moving between the two editors changes nothing
about how the agent behaves.

## Building from source

```powershell
./build.ps1                 # build + tests
./build.ps1 -Deploy         # also install into the experimental instance
./build.ps1 -SkipTests
./pack.cmd                  # bump, build Release, drop the .vsix in Dist\
```

Use the Visual Studio MSBuild, not `dotnet build` — the VSIX project is a classic
(non-SDK) csproj and the .NET SDK cannot resolve its VSSDK references. `build.ps1` finds
the right MSBuild and `vstest.console.exe` through `vswhere`.

The React bundle is built by the `BuildWebView` MSBuild target, so it can never be stale
relative to its sources; it is skipped when Node is missing, so a C#-only contributor needs
no JavaScript toolchain.

More in [`docs/building.md`](docs/building.md).

## Documentation

| Document | What is in it |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | The two bridges, threading rules, what the port substituted and why |
| [`docs/building.md`](docs/building.md) | Build, test, debug, package and publish |
| [`docs/troubleshooting.md`](docs/troubleshooting.md) | Menus missing, package not loading, CLI not found, logs |
| [`CHANGELOG.md`](CHANGELOG.md) | Release history |
| [`CLAUDE.md`](CLAUDE.md) | Directives for anyone — human or agent — working in this repository |

## Privacy

The extension talks to the Claude Code CLI on your machine and to Anthropic's endpoints
through it. It has no telemetry of its own and phones home to nobody. The optional OTEL
receiver listens on localhost and aggregates only what the CLI itself reports.

## Licence

MIT — see [`LICENSE`](LICENSE). Third-party components and their licences are listed in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
