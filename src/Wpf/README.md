# ky-ai-wpf

Let an AI **agent** see and steer a running **WPF desktop app** over MCP — read its automation tree,
press its buttons, fill its fields, assert on its text, screenshot it — **without ever taking your
focus, your mouse or your keyboard**. What [`ky-ai-browser`](../Browser/README.md) is for a served
frontend, this is for a window on your desktop.

The difference from every other way to drive a desktop app is the thing it refuses to do. There is
no `SendKeys`, no `keybd_event`, no `SetForegroundWindow`, no cursor move — anywhere in the binary,
enforced by its own test suite. Everything goes through **UI Automation**, the same interface a
screen reader uses, which works on a window that is unfocused, behind three others, or on another
virtual desktop. So an agent can exercise your app for ten minutes while you keep typing in your
editor, and nothing lands in the wrong window.

---

# For Humans

## Why

From a real session: an agent testing a WPF app fell back to `SendKeys` and `SetFocus` because it
had no other way in. It typed its test input **into the developer's terminal**, moved their caret and
overwrote their clipboard. Everything it actually needed — the row it wanted to click, the text box
it wanted to fill, the labels it wanted to assert on — was available focus-free through UI
Automation the whole time. It just had no tool for it. This is that tool.

## How it works

There is **no `serve` command** here, and that is the design. The other KY.AI tools supervise a
process you start through them; this one attaches to a window that is **already running**, started
however you normally start it — from your IDE, from the Explorer, from a shortcut. It never starts
your app and never stops it.

So there is nothing to register and no supervisor: a small MCP hub (auto-started on
`127.0.0.1:5106`, self-exits when idle) drives UI Automation in-process, and `list` reads the
desktop rather than a registry. You never run the hub yourself — your agent's connection starts it
and keeps it alive.

```
   you ──start your app however you like──►  a WPF window
                                                  ▲
                                   UI Automation (read + patterns, focus-free)
                                                  │
                                    ky-ai-wpf hub ──MCP──►  agent
                                      (port 5106)
```

## Usage

```
ky-ai-wpf list                           # the windows the agent can see (process, pid, title, handle)
ky-ai-wpf init [--agent claude|cursor|vscode] [-y] [--dir <path>]   # wire it into your agent (default: auto-detect)
ky-ai-wpf shutdown                       # stop the hub
ky-ai-wpf update                         # update to the latest release
```

`list` is the only command you are likely to run: it prints exactly what the agent's `list` tool
returns, which is the quickest way to check that your window is visible and to learn what to call
it.

## Connect your agent

`ky-ai-wpf init` wires it in for you — **Claude Code, Cursor, or VS Code** (`--agent`, default
auto-detect with an interactive picker; re-run after an update to pick up new tools). To wire it by
hand, add the server to the agent's MCP config — **Claude Code** `.mcp.json` / **Cursor**
`.cursor/mcp.json` (both use `mcpServers`; drop `type` for Cursor) / **VS Code** `.vscode/mcp.json`
(top-level `servers` key):

```json
{ "mcpServers": { "ky-ai-wpf": { "type": "http", "url": "http://127.0.0.1:5106/mcp" } } }
```

For Claude Code, also allow its tools in `.claude/settings.local.json` (Cursor and VS Code have no
file-based allow-list — tools are toggled in the editor's UI):

```json
{ "permissions": { "allow": [
  "mcp__ky-ai-wpf__list", "mcp__ky-ai-wpf__tree", "mcp__ky-ai-wpf__find", "mcp__ky-ai-wpf__text",
  "mcp__ky-ai-wpf__invoke", "mcp__ky-ai-wpf__set_value", "mcp__ky-ai-wpf__toggle",
  "mcp__ky-ai-wpf__expand", "mcp__ky-ai-wpf__select", "mcp__ky-ai-wpf__scroll_into_view",
  "mcp__ky-ai-wpf__screenshot"
] } }
```

## Make your app reachable

UI Automation can only name what your XAML names. Give every clickable row an
`AutomationProperties.Name` (*what* it is) and an `AutomationId` (*which kind* of thing it is), and
an agent addresses it the way a person would — "the SolutionRow called Switchboard":

```xml
<Button AutomationProperties.AutomationId="SolutionRow"
        AutomationProperties.Name="{Binding Title}" … />
```

Without that, the only handle on a row is its index in the tree — and an index moves the moment the
list is filtered, which is exactly when a test needs it most.

## Safety

Loopback-only; nothing leaves your machine. The tool reads and drives windows you already have
open — it starts nothing, kills nothing, and installs nothing in your app. It cannot type, click,
raise a window or touch your clipboard, because the calls to do any of that are not in the binary
(`NoSyntheticInputTests` fails the build if one appears). The single operation that changes anything
you can see is `scroll_into_view`, which is a separate tool for that reason.

Not raising a window takes one more thing than not calling `SetForegroundWindow`, and it is not the
default: **UI Automation focuses the element it acts on**, and focusing an element in another
process activates that process's window. So the client here runs with `AutoSetFocus` off — see
[Why the COM automation client](#why-the-com-automation-client).

---

# For Agents

Server: `http://127.0.0.1:5106/mcp`. Windows-only. Call `list` first to learn the window names.

Everything here works on an **unfocused window behind other windows**. Reads never focus, activate
or scroll it; actions drive the control's own automation pattern, so the app runs the same handler
it runs for a real click while the user's foreground window stays theirs. Every action reply carries
`foreground: {changed, before, after}` so you can prove it.

## Targeting

`window` takes a process name, a window title, a substring of either, or `#<handle>` from `list`.
Most specific tier wins (exact process → exact title → substring), and an ambiguous name is refused
with the candidates rather than guessed.

**Dialogs are windows.** A modal dialog — including a WPF one with `ShowInTaskbar="False"` — is an
*owned* window, and `list` shows it with an `ownedBy: {title, handle}` naming its owner. Target it by
its own title. Naming the **app** (by process) keeps meaning the app's own window even while a dialog
is up: an owner and its own dialog are not treated as an ambiguity. Two *independent* windows of one
process still are, and are still refused with both listed.

A dialog also appears inside its owner's automation tree, so `window: "MyApp"` plus a selector
reaches it too. Targeting it directly is cheaper and leaves less room to hit the wrong element.

Elements are named by one **selector**, shared by `find` and every action — the fields AND together:

| Field | Meaning |
|---|---|
| `path` | exact node position from `tree`/`find` (`"14.16.0.0"`). Wins over everything else |
| `automationId` | exact `AutomationId` (case-insensitive) — usually the row *kind* |
| `name` | exact `Name` (case-insensitive) — usually *which* row |
| `controlType` | `Button`, `Edit`, `ListItem`, `Text`, `TabItem`, … |
| `contains` | substring over name, automationId **and** value |
| `index` | which of several matches, zero-based in tree order |

An action whose selector matches **more than one** element is **refused**, not silently applied to
the first — narrow it, or pass `index`. `path` is a position, so it is only valid until the tree
changes shape: after a filter, an expand or a reload, read again rather than reusing one.

**Every path is absolute**, counted from the window root — including the ones a `tree` or `text`
scoped with `path` gives back. So a path from any tool can be handed straight to any other, and
scoping to a subtree changes what you get back, never how to address it. (The `root` field just
echoes the scope you asked for.)

## Read

| Tool | Args | Purpose |
|---|---|---|
| `list` | — | windows the tool can see: `{process, pid, title, handle, minimized, foreground, ownedBy?, rect}`. `ownedBy` marks a dialog and names its owner |
| `tree` | `window?`, `path?`, `depth?`, `includeOffscreen?`, `limit?` | the automation tree, flat and in top-to-bottom order: `{path, depth, type, automationId, name, value, enabled, offscreen, rect, patterns, children}`. `patterns` lists which actions will work on that node. A whole WPF tree is huge — start at the default depth and drill in with the `path` of the subtree you care about, rather than raising `depth` blindly. `truncated:true` means you hit the node cap |
| `find` | `window?`, `automationId?`, `name?`, `controlType?`, `contains?`, `depth?`, `limit?` | the same node shape, for the elements matching a selector. Much cheaper than a deep `tree` when you know what you are after |
| `text` | `window?`, `path?`, `depth?`, `limit?` | every text/label/edit/document/link element plus anything carrying a value, each with its `path` — the cheap way to assert on what is on screen after an action |
| `screenshot` | `window?`, `path?` | PrintWindow capture to a PNG you can read back. Works while the window is **behind** others; fails (rather than restoring it) if the window is minimized |

**Elevated windows read as empty, not as an error.** UI Automation will not hand a window's provider
to a client at a lower integrity level, and it does not say so — anything started as administrator
(Task Manager, regedit) comes back as a lone root element with **no children**, and refuses
PrintWindow too. It still appears in `list`, because the window manager reports it either way. Left
alone that is the most misleading answer this tool could give ("the app has no UI"), so `tree`,
`find` and `text` add a `hint` whenever a walk that was allowed to descend returns nothing but the
root. Nothing is broken; the app you meant is almost certainly a different row.

`includeOffscreen` is `false` by default, and it prunes offscreen nodes **and their subtrees** —
that is what keeps a virtualized WPF list cheap, since rows scrolled out of view are the ones you
cannot act on anyway.

## Act

| Tool | Pattern | Purpose |
|---|---|---|
| `invoke` | `InvokePattern`, falling back to `SelectionItem` | press a button / activate a row |
| `set_value` | `ValuePattern` | fill a text box. Does **not** focus and does **not** type — the reply carries the value **read back**, since a mask or validating binding can store something else |
| `toggle` | `TogglePattern` | `state:"on"`/`"off"` to reach a state (three-state checkboxes are stepped toward it), or omit to advance one step |
| `expand` | `ExpandCollapsePattern` | open/close a tree item, expander or combo box. Expanding is what makes a virtualized node's children appear — follow it with `tree` on that node's `path` |
| `select` | `SelectionItemPattern` | select a list item, tab or tree item; `addToSelection` extends a multi-select |
| `scroll_into_view` | `ScrollItemPattern` | the **only** tool that moves anything the user can see. Use it to realize a virtualized row, or before a screenshot that has to show one |

If an element does not support the pattern you asked for, the call is **refused** and tells you what
it *does* support. It will not fall back to a synthetic keystroke — that is the tool's whole point.
If a behaviour is genuinely keyboard-only, say so and ask the user to perform it, or have the app
expose the pattern (a WPF control reachable by keyboard alone usually just needs an automation
peer).

## Recipe

The loop is **find → act → assert**, and it costs one round-trip per step, so prefer `find` and
`text` over walking a deep `tree`.

```
list()                                                    // learn the window name
find({ window:'MyApp', automationId:'SearchBox' })        // confirm the field is there
set_value({ window:'MyApp', automationId:'SearchBox', value:'switchboard' })
find({ window:'MyApp', automationId:'SolutionRow' })      // read the filtered rows back
invoke({ window:'MyApp', automationId:'SolutionRow', name:'Switchboard' })
text({ window:'MyApp', limit:40 })                        // assert on what is now on screen
screenshot({ window:'MyApp' })                            // and look at it, without raising it
```

**`foreground.changed:true` is not the tool.** ky-ai-wpf has no way to activate a window: the calls
do not exist in it, and UI Automation's own auto-focus is switched off. When you see it, the **app
raised itself** in reaction to the change you made (a focus call in its own change handler is the
usual cause). The reply says so; treat it as a finding about the app, not about the tool.

---

## Why the COM automation client

The whole promise of this tool is that it drives a window without taking it, and .NET's obvious
client — `System.Windows.Automation` — cannot keep that promise. **UI Automation focuses the element
it acts on before every pattern call**, and focusing an element in another process activates that
process's window. Setting a search box's value on an app the user was not looking at pulled that app
to the front, mid-sentence. It is the client that does this, not the app: the same call raises
Notepad.

The COM client can be told not to (`IUIAutomation2::AutoSetFocus`), and the managed one cannot, so
the client surface is hand-rolled COM interop in `UiaCom.cs`.

Three things about that are worth knowing before touching it:

- **It has to be the whole client, not just the write path.** Once `System.Windows.Automation` has
  been used *anywhere in the process*, `AutoSetFocus = false` silently stops working — measured, in
  both orders and on separate threads. One `using` added for one convenient property re-breaks
  focus-freedom everywhere, and nothing fails except a user's foreground window.
  `NoSyntheticInputTests` fails the build if the assembly references it again.
- **A COM interface is its vtable.** Every method must be declared in IDL order, including the ones
  never called — those are the `_Unused*` slots. Insert a new method at its IDL position; appending
  it silently calls the wrong function. `get_CurrentBoundingRectangle` returns a Win32 `RECT` of four
  **LONG**s, not the double-based `UiaRect` the provider side uses; declaring it wrong does not fail,
  it just reports nonsense rectangles for every element.
- **Not every UIA error arrives as a `COMException`.** `UIA_E_INVALIDOPERATION` is `0x80131509`,
  which is the CLR's own `COR_E_INVALIDOPERATION`, so interop raises a plain
  `InvalidOperationException`. It is an ordinary "not right now" from a control, and every catch
  around a pattern call has to list it beside `COMException` — otherwise it escapes to the catch-all
  and gets reported as an unreachable provider, sending the reader after an elevation problem that
  does not exist.

---

## Files (internals)

- `Program.cs` — the CLI: `hub` (MCP control plane via the shared `HubHost`), `connect`, `init`,
  `shutdown`, `update`, and the human-facing `list`. Deliberately no `serve`.
- `WpfTools.cs` — the MCP tool surface, running in the hub.
- `Engine.cs` — what each tool does: resolve the window, walk the subtree, report or act. Owns the
  ambiguity refusal and the foreground before/after report.
- `WindowFinder.cs` · `WindowMatcher.cs` — enumerate the desktop's application windows, and resolve
  the agent's `window` string to exactly one of them (pure, so the tiers are unit-tested).
- `AutomationReader.cs` — the ControlView tree walk into flat `NodeInfo` records, with each node's
  live `IUIAutomationElement` kept beside it.
- `UiaCom.cs` — the UI Automation COM client, declared by hand. The reason it is not
  `System.Windows.Automation` is [above](#why-the-com-automation-client), and it is the difference
  between an action that takes the user's window and one that does not.
- `ElementSelector.cs` — one selector shared by `find` and every action (pure, unit-tested).
- `PatternActions.cs` — the five drivable patterns plus `ScrollIntoView`, and the refusal that names
  what an element does support instead of faking input.
- `ScreenCapture.cs` — the PrintWindow capture (`PW_RENDERFULLCONTENT`, without which a WPF window
  comes back blank) encoded to PNG.
- `Uia.cs` — the single MTA thread every automation call runs on, and the one client object it uses:
  the place where `AutoSetFocus` is turned off, and refuses to hand out a client if it cannot be.
- `Native.cs` — the read-only Win32 surface. Kept free of the input-synthesis and focus-stealing
  families on purpose; `NoSyntheticInputTests` enforces it.
- `AssemblyAttributes.cs` — `[assembly: SupportedOSPlatform("windows")]`. The project targets plain
  `net10.0`, **not** `net10.0-windows`, because `PackAsTool` rejects a target-platform TFM and every
  tool here ships as a .NET global tool — same trade `ky-ai-terminal` makes for ConPTY. The
  attribute is what keeps that honest without silencing the platform analyzer.
