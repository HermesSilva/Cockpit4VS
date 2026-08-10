# Changelog

All notable changes to Tootega Cockpit for Visual Studio.

Versions before 1.0.18 were internal builds produced while porting the extension from VS
Code, and are not listed individually — nothing shipped from them. `pack.cmd` bumps the
build number on every run, so the number here is the one that was published, not the count
of builds it took to get there.

## 1.0.18 — first public release

The Cockpit, native to Visual Studio. Same interface as the VS Code extension, same
`stream-json` contract with the Claude Code CLI, a new host around it.

### Conversation

- Token-by-token streaming, thinking blocks, a tool-call timeline with a card per call.
- Markdown with syntax highlighting and a line-number gutter.
- Find in conversation, export to Markdown, a scroll rail with one mark per prompt.
- A verbosity filter that changes what is displayed without changing what the agent does.
- One window per conversation, each with its own folder scope and its own CLI process.

### Control

- Permission approval with a per-tool preview; permission modes including an editable plan
  mode.
- Composed questions (`AskUserQuestion`) with tabs and multi-select.
- Side-by-side diffs in the panel or in the Visual Studio diff viewer.
- `@`-mentions with fuzzy file completion, and the current editor selection as a composer
  chip.
- A folder control on the conversation toolbar: it shows where the conversation runs and
  changes it.

### Transparency

- Live context window and cache life, tokens and cost per session, turn and compaction
  counts.
- Optional local OpenTelemetry receiver that aggregates what the CLI reports. Off by
  default; conversation text is pinned out of telemetry.

### Writing

- Inline spell-checker for English and Brazilian Portuguese (Hunspell), flagging only what
  is wrong in both, marking and never auto-correcting.
- Voice dictation with live partials, and opt-in AI correction of dictated text.

### Visual Studio integration

- Tool windows hosting the React interface through WebView2, with a shim that implements
  `acquireVsCodeApi()` so the webview runs unmodified.
- A theme bridge that fills the webview's `--vscode-*` tokens from VS theme colours,
  re-applied on theme change — including `color-scheme` and scrollbars, so the panel matches
  the IDE in every theme.
- A button in the Visual Studio title bar that opens the Cockpit Hub. Unsupported territory
  by nature, so it is optional, degrades to nothing, and is documented as such.
- Commands under **Extensions > Tootega Cockpit**, in **View > Other Windows** and on the
  Solution Explorer context menu; `Ctrl+Alt+C`, `Ctrl+Alt+N` and `Ctrl+Alt+.` carry over
  from VS Code.
- Settings in **Tools > Options > Tootega Cockpit**, with the same defaults as the VS Code
  extension.
- Vector icons through the VS ImageService, so they follow the theme.
- API keys in the Windows Credential Manager; enrolment secrets rendered as inline SVG.

### Deliberately different from the VS Code extension

- **English only.** No localisation layer, no catalogs, no locale switching.
- Substitutions where .NET had no equivalent: `WeCantSpell.Hunspell` for `hunspell-asm`,
  the Windows Credential Manager for `SecretStorage`, `QRCoder` for `qrcode`, and a raw
  `TcpListener` for the OTEL receiver. See [`docs/architecture.md`](docs/architecture.md).

### Known issues

- On some Visual Studio 2026 (18.x) installations, per-user extensions do not get their
  main-menu contributions merged, while context menus from the same extensions do. It
  affects extensions generally rather than this one. The title bar button, the Solution
  Explorer context menu and the keyboard shortcuts all still work. See
  [`docs/troubleshooting.md`](docs/troubleshooting.md).
