# Tootega Cockpit for Claude Code

A rich GUI for **Claude Code**, native to Visual Studio.

> **Unofficial.** Not affiliated with, endorsed by, or sponsored by Anthropic. "Claude",
> "Claude Code" and "Anthropic" are trademarks of Anthropic, PBC, used here only to
> describe interoperability. This extension talks to the official Claude Code CLI; it does
> not bundle or redistribute it.

The Cockpit is **a presentation and control layer over the Claude Code CLI**. Everything the
agent does — the loop, tools, subagents, context, cache, compaction, permissions, MCP,
hooks, skills — happens in the CLI you already have. The extension renders what it emits and
gives you the controls, inside the IDE you already work in.

## What you get

**A conversation that reads like one.** Token-by-token streaming, thinking blocks you can
expand, a timeline with a card per tool call, markdown with syntax highlighting and a
line-number gutter, find-in-conversation, export to Markdown, and a scroll rail with one
mark per prompt so a long session stays navigable. A verbosity filter changes what you see
without changing what the agent does.

**Control where it matters.** Approve or refuse each tool call with a preview of what it
would do. Switch permission modes, including plan mode — where you can *edit* the plan before
approving it. Answer composed questions with tabs and multi-select. Read diffs side by side,
in the panel or in the Visual Studio diff viewer. Mention files with `@`, and share the
current editor selection as a chip on the composer.

**Know what it costs.** A live context window and cache life, tokens and cost per session,
turns and compactions, and an optional local OpenTelemetry receiver that aggregates what the
CLI reports. No guessing about how much of the window is left.

**Write better prompts.** An inline spell-checker for English and Brazilian Portuguese that
marks and never auto-corrects — it flags only what is wrong in *both* languages. Voice
dictation with live partials, and optional AI clean-up of the dictated text.

**Keys where keys belong.** API keys live in the Windows Credential Manager, which you can
inspect and revoke through a normal Windows UI. Enrolment secrets are rendered as inline SVG
QR codes, so they never touch disk.

## Getting started

1. Install the [Claude Code CLI](https://www.anthropic.com/claude-code) and sign in once —
   or use **Extensions > Tootega Cockpit > Sign in to Claude (CLI)**.
2. Open the Cockpit from the title bar button, from
   **Extensions > Tootega Cockpit > Open Cockpit**, or with `Ctrl+Alt+C`.
3. Point the conversation at a folder using the folder button on the window's toolbar.

Each conversation gets its own window, its own folder and its own CLI process, so two
conversations never share state. The **Cockpit Hub** shows every saved context, the account
and the consumption in one place.

Settings live in **Tools > Options > Tootega Cockpit**.

## Requirements

- Visual Studio 2022 (17.x) or Visual Studio 2026 (18.x), 64-bit.
- The Claude Code CLI on your `PATH`, or its path set in the options.
- ffmpeg, only if you want voice dictation.

Nothing is spawned until you open a conversation.

## Privacy

The extension talks to the Claude Code CLI on your machine, and through it to Anthropic's
endpoints. It has no telemetry of its own and phones home to nobody. The optional OTEL
receiver listens on localhost and aggregates only what the CLI itself reports.

## Links

- [Source, issues and changelog](https://github.com/HermesSilva/Cockpit4VS)
- The same interface for VS Code:
  [Tootega Cockpit](https://marketplace.visualstudio.com/items?itemName=HermesSilva.tootega-cockpit)

MIT licensed.
