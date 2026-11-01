# Tootega Cockpit for Claude Code

**Claude Code, with a full interface, inside Visual Studio.**

Everything the agent does already happens in the Claude Code CLI you have installed: the
loop, the tools, the subagents, the context, the cache, the permissions, MCP, hooks and
skills. What it does not have is a place to *see* any of it. That is what the Cockpit is —
a presentation and control layer over the CLI, native to the IDE you already work in.

![A real turn in the Cockpit: thinking, Grep and Read cards, a task checklist, a permission diff, the test run and the answer](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Hero.gif)

*A real turn, recorded from the Cockpit's own interface: the agent finds a bug, plans the fix, asks before editing, runs the tests and reports.*

> **Unofficial.** Not affiliated with, endorsed by, or sponsored by Anthropic. "Claude",
> "Claude Code" and "Anthropic" are trademarks of Anthropic, PBC, used here only to describe
> interoperability. This extension talks to the official Claude Code CLI; it does not bundle
> or redistribute it.

---

## A conversation you can actually follow

Streaming token by token, with thinking blocks you can open when you want to know why, and
a timeline that gives every tool call its own card — what it ran, on what, and what came
back. Markdown is rendered with syntax highlighting and a line-number gutter, so a code
answer reads like code.

Long sessions stay navigable: a scroll rail marks each of your prompts, `Ctrl+F` searches
the conversation, and a verbosity filter quiets the timeline without changing anything the
agent does.

![Timeline with tool cards, a completed task checklist and a markdown answer](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Chat%20Panel.png)

### Take the conversation with you

Every turn is saved to `.cockpit\TimeLine\` as **one self-contained HTML file** that opens in any
browser: tool cards still open, long prompts still expand, and the copy buttons copy the
source. Or have the CLI rewrite the conversation as a polished Markdown document.

![The exported HTML timeline in a browser: Show more, an opened Read card, copying the answer](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Exported%20Timeline.gif)

## Control that arrives before the damage

Every tool call can be approved or refused with a preview of what it would do — allow once,
allow always, or deny. Permission modes are a dropdown, including **plan mode**, where the
plan is *editable*: correct it, send your notes back, and approve the version you actually
want.

Diffs are shown side by side, in the panel or in the Visual Studio diff viewer. Composed
questions from the agent come as real controls — tabs, multi-select, a free-text option.
Mention files with `@` and fuzzy completion, and share the current editor selection as a
chip on the composer, so the agent reads what you are looking at.

![Permission request with a side-by-side diff of the proposed edit](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Permission.png)

| Composed question | The recorded answer |
|---|---|
| ![Composed question with tabs and multi-select](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Question%20View.png) | ![The answered question in the timeline](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Question%20Response.png) |

## Know what it is spending

A live context window with the tokens left in it, cache life with a warning before it
expires, cost per session, turns, compactions, and cache hit rates. No guessing how much
room is left before compaction, and no surprise at the end of the month.

When the window passes **80%**, the **context guard** steps in — even in the middle of a turn. It
asks Claude to save what it needs to resume, following your repository's directives, and to
confirm with a line whose format and value the Cockpit dictates. Only that exact confirmation
unlocks the compaction, and the paused task picks up where it left off. If anything is off, it
stops without compacting and tells you why.

Optionally, a local OpenTelemetry receiver aggregates what the CLI itself reports. It
listens on localhost only, and conversation text is pinned out of telemetry.

| Live session meters | Account and limits |
|---|---|
| ![Cockpit Hub with context window, cache, cost and saved contexts](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Main%20Panel.png) | ![Account and usage: session and weekly limits, per-model breakdown](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Session%20Statistics%20View.png) |

## Written for people who write

An inline spell-checker for **English and Brazilian Portuguese**, running on Hunspell in the
host. It flags only what is wrong in *both* languages, so code identifiers and mixed-language
prompts stay quiet — and it **marks, never auto-corrects**. Click an underlined word for
suggestions grouped by language.

Prefer to talk? **Voice dictation** with live partials, and an opt-in pass that cleans up the
dictated text before it reaches the composer.

## One conversation, one window

Each conversation gets its own tool window, its own folder and its own CLI process, so two
of them never share state. The folder is on the window's toolbar: it says where the
conversation runs, and clicking it moves the conversation somewhere else.

The **Cockpit Hub** is the other half — every saved context, the account, the plugins, MCP,
skills and the consumption, in one place. It is one click from the title bar.

![The live task checklist and the activity gauge while the tests run](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Tasks%20View.png)

## Keys where keys belong

API keys go into the **Windows Credential Manager**, not into a file next to the extension:
a store you can inspect, audit and revoke through a normal Windows UI. Enrolment secrets for
the credential vault are rendered as inline SVG QR codes, so they never touch disk.

---

## Getting started

1. Install the [Claude Code CLI](https://www.anthropic.com/claude-code).
2. Sign in once — `claude` then `/login`, or
   **Extensions ▸ Tootega Cockpit ▸ Sign in to Claude (CLI)**.
3. Open the Cockpit: the button in the title bar, `Ctrl+Alt+C`, or
   **Extensions ▸ Tootega Cockpit ▸ Open Cockpit**.
4. Point it at a folder with the folder button on the window's toolbar, and start typing.

Settings live in **Tools ▸ Options ▸ Tootega Cockpit** — engine, session defaults,
interface, voice and the advanced switches. They mirror the VS Code extension, so nothing
about the agent's behaviour changes when you move between editors.

### Shortcuts

| | |
|---|---|
| `Ctrl+Alt+C` | Open the Cockpit |
| `Ctrl+Alt+N` | New session |
| `Ctrl+Alt+.` | Interrupt the agent |

## Requirements

- **Visual Studio 2022 (17.x) or Visual Studio 2026 (18.x)**, 64-bit — Community,
  Professional or Enterprise.
- **Claude Code CLI** on your `PATH`, or its full path set in the options.
- **ffmpeg**, only if you want voice dictation.

Nothing is spawned until you open a conversation. Installing the extension costs a package
load and nothing else.

## Privacy

The extension talks to the Claude Code CLI on your machine, and through it to Anthropic's
endpoints. It has **no telemetry of its own** and phones home to nobody. The optional OTEL
receiver listens on localhost and aggregates only what the CLI reports.

## Also for VS Code

The same interface, from the same codebase:
[Tootega Cockpit for VS Code](https://marketplace.visualstudio.com/items?itemName=HermesSilva.tootega-cockpit).

## Links

- [Source, issues and changelog](https://github.com/HermesSilva/Cockpit4VS)
- [Report a problem](https://github.com/HermesSilva/Cockpit4VS/issues)

MIT licensed. Made by Tootega Pesquisa e Inovação.
