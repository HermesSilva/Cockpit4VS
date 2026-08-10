# Troubleshooting

Start here, in this order: the output pane, then the activity log, then the scripts.

- **Output pane** — **View > Output**, pane *Tootega Cockpit*. Turn on
  **Tools > Options > Tootega Cockpit > Advanced > Debug logging** for the verbose version.
- **Activity log** — start the IDE with `devenv /log` and read
  `%APPDATA%\Microsoft\VisualStudio\<hive>\ActivityLog.xml`. It is UTF-16; open it in the
  browser or convert it before grepping.
- **`./scripts/check-install.ps1`** — checks each thing that can silently break an install,
  separately, so the answer is a cause rather than "it doesn't work".

---

## The menus do not appear

The commands can be registered, live and enabled and still show nowhere. Check in this
order.

**1. Is the extension actually installed and enabled?**
**Extensions > Manage Extensions > Installed**.

**2. Did the install register the package?**

```powershell
./scripts/check-install.ps1
```

Run it with Visual Studio closed for the registry check to be conclusive. It verifies, in
turn: the extension folder, the `Menus.ctmenu` resource inside the assembly, the `pkgdef`
that registers it, and whether that `pkgdef` reached the IDE's private registry.

**3. Did the shell merge the command table?**

```powershell
./scripts/probe-commands.ps1     # with the IDE running
./scripts/probe-menus.ps1
```

The first lists the commands the IDE knows that came from a Tootega extension; the second
lists what is actually drawn on the Extensions and View menus.

**4. Force the merge.**

```powershell
./scripts/reinstall.ps1                              # uninstall + install, IDE closed
devenv /updateconfiguration                          # re-import the pkgdefs
devenv /setup                                        # elevated: rebuild the merged menus
```

A plain reinstall is the one that matters: Visual Studio redoes the menu merge on a full
uninstall/install, not on an install over the top.

### Known: Visual Studio 2026 and main-menu contributions

On some Visual Studio 2026 (18.x) installations, **per-user extensions do not get their
main-menu contributions merged at all**, while context-menu contributions from the very same
extensions work. It affects extensions generally, not this one: on the machine where this was
diagnosed, three unrelated VSSDK extensions lost their menu bar entries together, and the
only third-party menus still under **Extensions** were installed for all users.

How to recognise it: the package loads (the activity log says
`Begin/End package load [CockpitPackage]` with no error), `check-install.ps1` passes every
check, `probe-commands.ps1` finds no named command, but the commands respond when asked for
by id — they exist, nothing draws them.

What still works meanwhile:

- the **title bar button**;
- the **Solution Explorer context menu** on the solution and project nodes;
- the keyboard: `Ctrl+Alt+C` opens the Cockpit, `Ctrl+Alt+N` starts a session,
  `Ctrl+Alt+.` interrupts.

What to try: **Repair** from the Visual Studio Installer, then `devenv /setup` elevated. If
the menus stay missing for every extension, it is worth reporting to the Developer Community
with an unrelated extension as the control case.

---

## The package does not load

The dialog says *The 'CockpitPackage' package did not load correctly* and points at the
activity log. Read the entry with our package guid
(`{92C17B2D-A9A9-460D-A1E2-D48F8F21E29F}`) — it carries the real exception.

The usual causes:

- **A stale registration.** A `pkgdef` left behind by an earlier deployment pointing at a
  folder that no longer exists. The symptom is `FileNotFoundException` on a path you no
  longer have. Reset the experimental instance (see [building](building.md#debugging)), or
  uninstall and reinstall for the real one.
- **A `CodeBase` without a separator.** `$PackageFolder$` expands *without* a trailing
  backslash. A `pkgdef` that says `$PackageFolder$Something.dll` resolves to
  `...\extensions\abc123.xyzSomething.dll` and fails to load. Ours uses
  `$PackageFolder$\Tootega.Cockpit.dll`; if you fork the registration, keep the separator.

---

## "Claude CLI not found"

The extension runs `claude` through `cmd.exe /s /c`, so it uses your `PATH` as a shell would.

- Confirm the CLI works outside the IDE: `claude --version`.
- If it is not on the `PATH`, set the full path in
  **Tools > Options > Tootega Cockpit > Engine > Claude CLI path**.
- If you have just installed it, restart Visual Studio: a process inherits the environment it
  was started with, and the installer's `PATH` change does not reach an IDE that was already
  running.

## Signed out, or a key that is not taken

- **Extensions > Tootega Cockpit > Sign in to Claude (CLI)** runs the CLI's own login.
- An Anthropic API key set through **Set Anthropic API Key** is stored in the Windows
  Credential Manager. To see or remove it by hand: **Control Panel > Credential Manager >
  Windows Credentials**, entry `Tootega.Cockpit`.

## The panel is blank

The webview loaded nothing. In order:

1. **Extensions > Tootega Cockpit > Reload Cockpit View**.
2. Check the output pane for `WebView assets missing` — a build that shipped no bundle.
3. If it persists, the WebView2 profile may be corrupt. Close Visual Studio and delete
   `%LOCALAPPDATA%\Tootega\Cockpit\WebView2`; it is a cache and is rebuilt on next open.

## The colours are wrong after changing theme

The theme is republished on `VSColorTheme.ThemeChanged`, so this should not happen. If it
does, **Reload Cockpit View** re-applies it. Fix such a bug in `UI/VsThemeBridge.cs`, never
in the webview's CSS — see [architecture](architecture.md#the-two-bridges).

## The title bar button is missing

It is grafted onto the shell's own title bar, which has no supported extension point, so it
can stop finding a place after a Visual Studio update. When that happens it writes the top of
the visual tree to `%TEMP%\tootega-cockpit-titlebar.txt` and says so in the output pane —
that file is what a bug report needs.

Turn it off in **Tools > Options > Tootega Cockpit > Interface > Title bar button**. The Hub
is also on the Extensions menu, in View > Other Windows, and in the Solution Explorer context
menu.

## Voice dictation does nothing

Dictation needs **ffmpeg**. Install it and put it on the `PATH`, or set
**Tools > Options > Tootega Cockpit > Voice > ffmpeg path**.

## Reporting a bug

Include: the Visual Studio version (**Help > About**), the extension version, the CLI version
(`claude --version`), the output pane with debug logging on, and — when the IDE itself is
involved — the `ActivityLog.xml` from a `devenv /log` run.
