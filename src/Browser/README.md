# ky-ai-browser

Read a served frontend's **browser/runtime console** — and **inspect and drive its live runtime**
— from an AI **agent** over MCP. The runtime counterpart to [`ky-ai-ng`](../Ng/README.md)'s build
tools: where `ky-ai-ng` answers *"did it compile?"*, `ky-ai-browser` answers *"does it actually work
in the page?"*. It captures `console.log/info/warn/error`, uncaught exceptions and unhandled promise
rejections (with source location + stack) so the agent can read them, and lets it run code in the
page, query the DOM, read Angular component state, synthesise input, and reload — so the agent
confirms behaviour instead of guessing from source.

---

# For Humans

## How it works

`ky-ai-browser` is a **process you run** next to a running `ky-ai-ng serve` — its lifetime is the
on/off switch, so *you* control the manipulation.

Like `ky-ai-ng` it's a **hub + instances**: a small MCP hub (auto-started on `127.0.0.1:5104`, self-
exits when idle) holds the tool surface, and each `ky-ai-browser` you run is a capture **instance**
that binds its own OS-assigned port and registers with the hub under the frontend's name. So you can
run **several at once** — one per `ky-ai-ng` frontend — while the agent talks to the single hub URL
and routes by `project`. You never start the hub yourself.

Usually you launch it together with the dev server — let `ky-ai-ng serve` start `ky-ai-browser` once
the first build settles:

```
ky-ai-ng serve --after-start ky-ai-browser -y
```

1. On start it finds the running `ky-ai-ng` frontend automatically and injects a tiny capture `<script>` into the app's `index.html`.
2. The page reloads, and from then on the agent can read the browser console and inspect or drive
   the live page over MCP.
3. On **Ctrl+C** the script is removed and `index.html` is restored — and if `ky-ai-browser` ever
   dies without cleaning up, `ky-ai-ng` reverts the file on its own, so it's never left modified.

## Usage

```
ky-ai-browser [options]            # run alongside `ky-ai-ng serve`
  --project <id>      Which ky-ai-ng frontend to attach to (default: the only one registered);
                      also the name this capture registers under in the hub
  --name <id>         Override the hub registry name (default: the attached frontend's name)
  --hub-port <N>      ky-ai-browser hub port to register with (default: 5104; auto-started)
  --ng-hub-port <N>   ky-ai-ng hub port to discover the frontend (default: 5101)
  --rest-port <N>     this instance's own control/ingest port (default: OS-assigned)
  --no-hub            standalone: capture locally only; no hub, no agent access
  -y, --yes           Skip the inject confirmation (default answer is yes anyway)

ky-ai-browser shutdown [--hub-port <N>]  # tear down the whole stack (every instance removes its script, restores index.html)
ky-ai-browser init [--agent claude|cursor|vscode] [-y] [--dir <path>]   # wire it into your agent (default: auto-detect)
ky-ai-browser update                     # update to the latest release
```

`shutdown` tears down the hub and **every** capture instance it supervises — each detaches like a
**Ctrl+C** would. Handy when an instance was launched via `ky-ai-ng serve --after-start ky-ai-browser`
(sharing ng's console), so you can detach the console capture without taking ng down. To stop just one
instance, Ctrl+C it in its own terminal.

## Connect your agent

`ky-ai-browser init` wires it into your agent for you — it targets **Claude Code, Cursor, or VS
Code** (`--agent <claude|cursor|vscode>`, default auto-detect with an interactive picker; re-run it
after an update to pick up new tools). To wire it by hand, add the server to the agent's MCP config —
**Claude Code** `.mcp.json` / **Cursor** `.cursor/mcp.json` (both use `mcpServers`; drop `type` for
Cursor) / **VS Code** `.vscode/mcp.json` (top-level `servers` key):

```json
{ "mcpServers": { "ky-ai-browser": { "type": "http", "url": "http://127.0.0.1:5104/mcp" } } }
```

For Claude Code, also allow its tools in `.claude/settings.local.json` (Cursor and VS Code have no
file-based allow-list — tools are toggled in the editor's UI):

```json
{ "permissions": { "allow": [
  "mcp__ky-ai-browser__console_tail", "mcp__ky-ai-browser__console_clear",
  "mcp__ky-ai-browser__evaluate_js", "mcp__ky-ai-browser__query_dom", "mcp__ky-ai-browser__get_styles",
  "mcp__ky-ai-browser__read_component",
  "mcp__ky-ai-browser__start_interaction", "mcp__ky-ai-browser__stop_interaction",
  "mcp__ky-ai-browser__click", "mcp__ky-ai-browser__move", "mcp__ky-ai-browser__send_key",
  "mcp__ky-ai-browser__type_text", "mcp__ky-ai-browser__scroll", "mcp__ky-ai-browser__focus",
  "mcp__ky-ai-browser__wait_for", "mcp__ky-ai-browser__reload_page", "mcp__ky-ai-browser__navigate",
  "mcp__ky-ai-browser__batch"
] } }
```

## Update

```bash
ky-ai-browser update
```

Updates to the latest release. It first stops any running `ky-ai-browser`, cleaning up the injected
script as it goes.

## Safety

Everything is loopback-only — nothing leaves your machine. `evaluate_js` and the interaction tools
run code and synthetic input in your app's page; that's the point, and it's safe because it's a
dev-only tool whose lifetime is the `ky-ai-browser` process you started, gated by the inject you
confirmed.

---

# For Agents

Server: `http://127.0.0.1:5104/mcp`. The page is woken from a long-poll on every call (no 25s wait),
so a call is typically tens of ms plus transport — **the cost of a flow is the number of
round-trips**, so prefer fewer, richer calls (one `evaluate_js` / `batch` over many small reads).
Inspect/interact tools that need the page return `pageConnected:false` (rather than hanging) when no
browser has the app open.

## Read

| Tool | Args | Purpose |
|---|---|---|
| `console_tail` | `lines?`, `level?`, `sinceSeq?`, `grep?`, `pageLoad?`, `currentPageOnly?`, `compact?`, `appOnly?`, `dropFrameworkNoise?` | recent browser console events: `{seq, level, args, text, source, line, col, stack, timestamp, pageLoadId}` + `dropped` + `enabled` + `currentPageLoadId` (the live page load — tell fresh from stale without a second call). `currentPageOnly` scopes to that page (the one-call "did my reload clear it?" check; an explicit `pageLoad` wins). `compact` drops `args` when `text` carries them and clips stacks (much smaller payloads); `appOnly` drops transport churn (SignalR/WebSocket negotiation, `[vite]` HMR socket noise); `dropFrameworkNoise` **separately** drops known-benign framework banners (DevExtreme/Inferno production-build notice, the Angular dev-mode banner, a dev-only router "Transition was aborted") — set both for a fully clean channel |
| `console_clear` | — | clear the buffer (e.g. before reproducing an issue) |

## Inspect (ungated)

| Tool | Args | Purpose |
|---|---|---|
| `evaluate_js` | `expression`, `awaitPromise?`, `json?`, `timeoutMs?` | evaluate JS in the page (global scope) → `{ok, type, value}` — read live state, e.g. `ng.getComponent(document.querySelector('app-wire')).energized()`. Signals are getter **functions** — **call** them (`.value()`, not `.value`). `value` comes back as a **string** (objects are JSON-stringified) — return `JSON.stringify(...)` and parse your side |
| `query_dom` | `selector`, `all?`, `limit?`, `detail?`, `timeoutMs?` | describe matched element(s): `{tag, id, classes, attributes, text, rect, html}` + `count`. `detail:false` slims each match to `{tag, id?, text}` |
| `get_styles` | `selector`, `props?`, `timeoutMs?` | computed CSS of an element: `{styles:{prop:value,…}, target}` — confirm a transform/hover style actually applied |
| `read_component` | `selector`, `fields?`, `depth?`, `timeoutMs?` | snapshot the Angular component on/above the element: `{component, state, signals, formControls?, methods, objects?, note?}` — **signals resolved (called), FormControls unwrapped**, so a clean model read works where `ng.getComponent(el).value` came back empty. **`state` is lean by default**: it expands signals + FormControls + scalars and collapses complex/framework objects (services, Subjects, `destroyRef`, `errorHandler`, view graphs) to a one-line type tag, listing their names in `objects`. Expand the ones you need with `fields:["options",…]` (returned in full, depth-limited) or raise `depth`. Also inline as `__kyai.readComponent(el, {fields, depth})` from `evaluate_js` |

## Interact (gated — see the caveat below)

These are **gated**: call `start_interaction` first (and `stop_interaction` when done) — it shows the
user a fixed red overlay with an animated cursor so they can see the agent driving the page.

| Tool | Args | Purpose |
|---|---|---|
| `start_interaction` | `timeoutMs?` | **required before any interaction** — draws the supervision overlay; returns `{ok, shown}` |
| `stop_interaction` | `timeoutMs?` | remove the overlay and re-block interaction |
| `click` | `selector?` \| `text?`(`within?`,`exact?`) \| `x?,y?`, `button?`, modifiers, `detail?`, `timeoutMs?` | full pointer+mouse sequence then `click()`. Target by CSS `selector`, by visible `text` (deepest match; `within` scopes it, `exact` defaults true), or by `x,y` point. Returns the element actually hit |
| `move` | `toX,toY`, `fromX?,fromY?`, `durationMs?`, `steps?`, `detail?` | pointermove along a path with enter/leave bookkeeping — drives JS hover/dwell logic |
| `send_key` | `key`, `code?`, `selector?`, modifiers, `detail?` | keydown/keypress/keyup for one key (Enter, Escape, arrows, shortcuts) — does **not** change input value |
| `type_text` | `selector`, `text`, `append?` | set a field's value + fire input/change so Angular/React observe it |
| `scroll` | `selector?`, `x?`, `y?` | scrollIntoView, scroll within an element, or window.scrollTo |
| `focus` | `selector?`, `blur?` | focus (or blur) an element |
| `wait_for` | `selector?` \| `expression?`, `timeoutMs?`, `pollMs?` | poll in-page until an element appears / an expression is truthy — avoid acting before render |
| `reload_page` | `timeoutMs?` | **full** reload — re-instantiate everything after a build that changed code (HMR may keep stale instances). Not navigation — use `navigate` to change route without a reload |
| `navigate` (ungated) | `path`, `replace?`, `timeoutMs?` | change the SPA route **without** a hard reload — finds the Angular `Router` on a dev build and calls `navigateByUrl(path)`, falling back to the History API (pushState + synthetic popstate) otherwise. Returns `{ok, from, to, navigated, method:'router'\|'history'}`; `to` is the settled URL (confirm even a guard redirect). Services/singletons stay live — reach for `reload_page` when you need those re-instantiated |
| `batch` | `steps[]`, `timeoutMs?` | run an ordered sequence of actions in **one** page round-trip — much faster for multi-step flows. Each step is `{action, …that action's fields}`, `action ∈ click \| move \| key \| type \| wait \| scroll \| focus \| styles \| query \| component \| eval`; steps run in order and **stop at the first failure**. Returns `{ok, count, results:[…], failedAt?}`. Manipulation steps still require `start_interaction` first |

Every interaction returns the element it actually targeted so you can confirm you hit the right
thing. That `target` is **minimal by default** (`{tag, id?, text}`) — enough to confirm the hit and
cheap on multi-step flows; pass **`detail:true`** on any interaction tool for the full element
(classes, attributes, rect, clipped `outerHTML`).

> **`navigate` vs `reload_page`.** `navigate` changes route *client-side* — it drives the Angular
> `Router` (History-API fallback) without tearing down the app, so already-created services/singletons
> stay live; follow with `wait_for` `location.pathname` (or a destination selector) if the route
> resolves async. You can still route the user's way — `click` a nav link / `routerLink` — when you
> want the real DOM path. `reload_page` is a *full* page reload (a new `pageLoadId`); reach for it only
> after a code change that needs everything re-instantiated, not to move around.

> **Synthetic-event caveat.** Interaction events are dispatched in-page, so `isTrusted` is `false`:
> they fire JS handlers but do **not** drive CSS `:hover`, nor user-activation-gated APIs
> (`window.open`, clipboard, fullscreen). A JS-state hover (`(mouseenter)` handler) reproduces via
> `move`; a pure CSS `:hover` does not — that needs a debugger-driven tool (CDP), which this
> deliberately isn't.

**Supervision overlay.** While interaction is open, a fixed, non-interactable (`pointer-events:none`,
in a shadow root so it never touches your app or its `querySelectorAll`) red frame is drawn over the
page with a cursor icon, and each action animates it — a ripple on `click`, a key cap on
`send_key`/`type_text`, the cursor gliding on `move`. So a human watching always sees, and can
follow, what the agent is doing. The overlay restores itself if the page reloads mid-interaction, and
clears itself if `ky-ai-browser` goes away.

## Recipes

ky-ai-browser is a **read → act → verify** loop over the live page.

**1. Explore first (read tools are ungated).** Do multi-step DOM work in a *single* `evaluate_js`
instead of many small reads:

```
// one call returns the whole nav map, not ten little ones
evaluate_js({ expression:
  "JSON.stringify([...document.querySelectorAll('a[href]')].map(a=>({t:a.textContent.trim(),href:a.getAttribute('href')})))" })
```

**2. Open interaction, act, verify, close.**

```
start_interaction()                                   // draws the overlay; REQUIRED before click/move/key/type/scroll/focus
click({ selector: 'a[href="/elements/dropdown"]' })   // returns the element actually hit
wait_for({ expression: 'location.pathname==="/elements/dropdown"' })   // don't act before the route/render settles
// …read state to confirm…
stop_interaction()
```

To *just change route* (no reload, no overlay needed) skip the click: `navigate({ path: '/elements/dropdown' })`
drives the router directly and returns the settled `to` URL. Since it doesn't reload, the `pageLoadId`
is unchanged — to read only what the new route logged, page from the prior tail's max `seq` with
`console_tail({ sinceSeq })`. (`currentPageOnly` segments *reloads*, e.g. after `reload_page`.)

**3. Target by visible text.** `click` takes `text` directly (`within` to scope, `exact:false` for
substring) — no need to read a rect and click a point:

```
click({ text: 'Zwei', within: 'm-dropdown' })
```

For a multi-step flow, fold it into one round-trip with `batch`:

```
batch({ steps: [
  { action: 'click', selector: '.menu' },
  { action: 'wait',  selector: '.item' },
  { action: 'click', text: 'Zwei' }
] })
```

**4. Verify the model, not just the text.** The rendered text proves "the user saw it change"; the
bound model proves "the app's state actually changed". Use **`read_component`** for a clean read — it
resolves the trap that `ng.getComponent(el).value` comes back empty because modern Angular values
are **signals** (`cmp.value` is a getter *function* — you must *call* it). It calls signal getters,
unwraps FormControls, and lists drivable methods:

```
read_component({ selector: 'm-dropdown' })
// → { ok, component:'MDropdown', state:{ value:1, … }, signals:['value'], methods:['selectIndex','setValue',…] }
```

**When a synthetic click doesn't "take" (custom widgets).** Synthetic events are `isTrusted:false`.
Most things respond (links, buttons, JS-state hover), but some custom components — e.g. a
Fomantic-style `<m-dropdown>` — won't commit state from a synthetic click, *and neither will a
programmatic `.click()`*. You detect this by **verifying after acting** (e.g. `read_component`): the
element was hit, but the value didn't change. The reliable fallback is to drive the component model
directly through `ng.getComponent` — the `methods` from `read_component` tell you what's drivable:

```
evaluate_js({ expression:
  "(()=>{const c=ng.getComponent(document.querySelector('m-dropdown'));c.selectIndex(1);return c.value()})()" })
```

`read_component` to verify, `evaluate_js` + `ng.getComponent(el)` to drive — together they read or
change component state when DOM-level input hits a wall. Inline, the same probing is
`__kyai.readComponent(el)`.

**5. Read the console around an action** — `compact:true` shrinks payloads, `appOnly:true` drops
SignalR/`[vite]` transport churn so the app's own logs/errors stand out:

```
console_tail({ compact: true, appOnly: true, lines: 20 })
```

---

## Files (internals)

The capture buffer, event types and the in-page snippet live in this project; the reversible inject
mechanism it drives is a generic `POST /inject { file?, path, content }` (+ `/uninject`) on the
`ky-ai-ng` supervisor's control API.

- `Program.cs` — the `hub` subcommand (MCP control plane via the shared `HubHost`) and the capture
  **instance**: frontend discovery via the ng hub, the OS-assigned control/ingest host, hub
  registration, and the inject/uninject lifecycle.
- `BrowserTools.cs` — the MCP tool surface (read / inspect / interact / batch), running in the hub and
  forwarding each call to the right instance by `project`.
- `InstanceEval.cs` — instance-side dispatch for a forwarded `EvalRequest`: the supervised-interaction
  gate, then enqueue on the channel.
- `Capture.cs` · `ConsoleCollector.cs` · `ConsoleEvent.cs` · `ConsoleEventLog.cs` — the cross-origin
  ingest endpoint and the rolling console-event buffer.
- `EvalChannel.cs` — the half-duplex long-poll return channel (inspect/interact requests → results),
  guarded by the misroute token.
