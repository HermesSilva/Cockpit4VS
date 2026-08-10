# Third-Party Notices

Tootega Cockpit for Claude Code is distributed under the MIT licence (see `LICENSE`). It
ships, or builds against, the components below. Each remains under its own licence, and
nothing here changes those terms.

## Shipped in the VSIX

| Component | Version | Licence | Why it is here |
|---|---|---|---|
| [WeCantSpell.Hunspell](https://github.com/aarondandy/WeCantSpell.Hunspell) | 7.0.1 | MIT | The spell-checker. Fully managed, reads the same `.aff`/`.dic` files as Hunspell, so no native binary per architecture. |
| [QRCoder](https://github.com/codebude/QRCoder) | 1.8.0 | MIT | Renders the credential-vault enrolment secret as inline SVG. |
| [Microsoft.Web.WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) | 1.0.4129.50 | [Microsoft WebView2 SDK licence](https://www.nuget.org/packages/Microsoft.Web.WebView2/license) | Hosts the React interface inside the tool windows. |
| [React](https://react.dev/) and [React DOM](https://react.dev/) | 18.3.x | MIT | The interface itself, bundled into `WebView/main.js`. |
| [highlight.js](https://highlightjs.org/) | 11.9.x | BSD-3-Clause | Syntax highlighting in the conversation. |

## Dictionaries

| Dictionary | Licence |
|---|---|
| English (`Dictionaries/en.*`), derived from [SCOWL](http://wordlist.sourceforge.net) | See `Dictionaries/en.LICENSE.txt` — SCOWL/Hunspell terms |
| Brazilian Portuguese (`Dictionaries/pt-br.*`), by Raimundo Moura and team | LGPL v3 and MPL — see `Dictionaries/pt-br.LICENSE.txt` |

The dictionaries are loaded from disk on first use rather than embedded: the pt-BR pair
alone is over 5 MB.

## Build-time only, not shipped

| Component | Version | Licence |
|---|---|---|
| [Microsoft.VisualStudio.SDK](https://www.nuget.org/packages/Microsoft.VisualStudio.SDK) | 17.14.40265 | Microsoft VS SDK licence |
| [Microsoft.VSSDK.BuildTools](https://www.nuget.org/packages/Microsoft.VSSDK.BuildTools) | 18.8.739 | Microsoft VS SDK licence |
| [System.Text.Json](https://www.nuget.org/packages/System.Text.Json), [System.Text.Encodings.Web](https://www.nuget.org/packages/System.Text.Encodings.Web) | 9.0.0 | MIT |
| [esbuild](https://esbuild.github.io/), [TypeScript](https://www.typescriptlang.org/) | see `webview/package.json` | MIT / Apache-2.0 |

`System.Text.Json` and its dependency are referenced with `ExcludeAssets="runtime"`: the
IDE already binds a copy through `devenv.exe.config`, and shipping another would be dead
weight at best and a split-assembly conflict at worst.

## Claude Code CLI

The extension **talks to** the Claude Code CLI; it does not bundle, redistribute or modify
it. Install it from Anthropic and use it under Anthropic's terms. "Claude", "Claude Code"
and "Anthropic" are trademarks of Anthropic, PBC, used here only to describe
interoperability. This project is unofficial and not affiliated with Anthropic.
