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
./scripts/probe-ctm.ps1          # the IDE may be closed
./scripts/probe-commands.ps1     # with the IDE running
./scripts/probe-menus.ps1
```

`probe-ctm.ps1` is the one to run first, and the only one that answers the question that
matters, because the two possible causes want opposite fixes. `devenv.CTM` is the merged
command table the shell draws from: a count above zero for `guidCockpitCmdSet` means the
contribution reached the merge and is simply not being drawn — read the section on VS 2026
below. A count of zero means it never reached the merge at all, and the target is the install
and its pkgdef, not the placement.

Run it while the extension is installed. After an uninstall the table is rebuilt without it,
so zero is what it should say and it proves nothing.

The other two need the IDE running: the first lists the commands it knows that came from a
Tootega extension, the second lists what is actually drawn on the Extensions and View menus.

**A placement that looks right can still be wrong**, and comparing against an extension whose
menu does appear is what shows it. Decompiling both command tables is how the submenu's parent
was found to be the problem once already: it hung from the `IDG_VS_EXTENSIONS` group, while
the extensions that do get drawn parent straight to `IDM_VS_MENU_EXTENSIONS`.

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

## It installed, and Visual Studio has never heard of it

The installer reported success, the extension folder is under
`%LOCALAPPDATA%\Microsoft\VisualStudio\<hive>\Extensions\`, and yet nothing works and
**Manage Extensions does not list it**.

The installer's job ends at leaving a marker — `extensions.configurationchanged`, beside the
extension folders — for the shell to act on at its next start. On some Visual Studio 2026
installations that never happens: the marker stays where it was written, the pkgdef is never
imported, and the IDE goes on as if nothing had been installed.

Confirm it in one look: if that file is still there after the IDE has started and closed
again, it was not consumed.

```powershell
Get-Item "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_*\Extensions\extensions.configurationchanged"
```

Applying it by hand takes a few seconds, with the IDE closed:

```powershell
devenv /updateconfiguration
```

`scripts/reinstall.ps1` does this as its last step, so a local build never lands in this
state. The installer's own log — `%TEMP%\dd_VSIXInstaller_*.log`, newest first — is where to
confirm the install itself was clean; look for "has been committed to the
'PerUserEnabledExtensionsCache' cache".

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

## The conversation jumps back to its beginning when the panel gets the focus

Fixed in `VsWebView2`, by intercepting `IKeyboardInputSink.TabInto`.

WPF hands the focus to a hosted sink by calling `TabInto`, and the shell does that every time it
activates the tool window. The control answers it with
`MoveFocus(CoreWebView2MoveFocusReason.Next)` — "focus the next element in the page" — and from
outside, the next element is the *first tabbable one in the document*. In a conversation that is
a button in the topmost message, so the browser scrolls the transcript up to reveal it, and
takes the focus off the composer on the way.

Measured against the bare control in a WPF host of its own, with the scroller at 1200:

| call | offset after | focused after |
|---|---|---|
| `MoveFocus(Programmatic)` | 1200 | unchanged |
| `MoveFocus(Next)` | **0** | first button |
| `TabInto(First)` | **0** | first button |
| `TabInto(First)`, intercepted | 1200 | unchanged |

So the focus now arrives through `Programmatic`, which picks no element and moves nothing.
Tabbing *within* the page is the browser's own and is untouched; the way out
(`MoveFocusRequested`) still belongs to the control.

Two other causes were proposed and killed by the same measurements — do not bring them back:
`CoreWebView2Controller.Bounds` does **not** collapse when the panel is hidden (it survived a
tab switch, a `Visibility.Collapsed` and a window minimise without a single `SizeChanged`), and
`MoveFocus(Programmatic)` scrolls nothing.

`CockpitWebView.BuildScrollTrace()` is the instrument that settled it, and is still there for
the next question of this kind. Turn on **Tools > Options > Tootega Cockpit > Advanced > Debug
logging**, then **Extensions > Tootega Cockpit > Reload Cockpit View**, and the output pane gets
a line for each way an offset can be lost: an assignment with the caller that made it, a
scroller replaced or removed, a measurement that changed, the focus landing inside the
transcript, and the host messages arriving around it. Timestamps are relative to the start of
tracing, because the page and the host do not share a clock.

Do not "fix" a scroll problem by reading the offset and writing it back. That makes the host a
second author of the scroll position, racing the user's own wheel.

## Editing keys do not work in the composer

Fixed. `WebView2Base` answers the browser's accelerator notification by manufacturing a WPF
`KeyEventArgs` and raising it on the control as `PreviewKeyDown`, which tunnels from the root
of the WPF tree downwards; the composition control then re-raises it as `KeyDown`, which
bubbles back up. Whatever anything on either route makes of the key is returned to the browser
as `Handled` — and a handled key never reaches the page. Home, End, Page Up/Down, the arrows,
Tab and Delete all travel that way, because keys with no typed representation always raise the
notification, so each was being offered to a tree of WPF controls with no business with a
caret in a text box.

`VsWebView2.StopWpfReplay` removes that replay. The IDE's claim on a keystroke is unaffected:
it is made deliberately in `OnAcceleratorKeyPressed`, through the shell's own key-binding
resolution, for the keys that are the IDE's business and no others.

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
