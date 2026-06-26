# ky-ai-browser

Read a served front-end's **browser/runtime console** — and **inspect its live runtime** — from an
AI **agent** over MCP, the runtime counterpart to [`ky-ai-ng`](https://www.nuget.org/packages/KY.AI.Ng)'s
build tools. It captures `console.log/info/warn/error`, uncaught exceptions and unhandled promise
rejections (with source location + stack) so the agent can read them via `console_tail`, and lets it
run code in the page (`evaluate_js`), query the DOM (`query_dom`) and reload it (`reload_page`) —
so the agent confirms state instead of guessing from source.

## How it works

`ky-ai-browser` is a **process you run** next to a running `ky-ai-ng serve` — its lifetime is the
on/off switch, so *you* control the (reversible) manipulation:

1. On start it finds the running `ky-ai-ng` frontend (via the hub's registry) and — after a
   confirmation (**default yes**) — asks `ky-ai-ng` to inject a tiny capture `<script>` into the
   app's `index.html` (wrapped in `ky-ai-ng-inject` markers, at `/html/head`).
2. The dev server reloads the page; the snippet patches `console.*` + the error events and POSTs
   them back to `ky-ai-browser` (loopback, cross-origin). No proxy, HMR stays native. The same
   snippet also long-polls `ky-ai-browser` for inspection requests, runs them in the page and posts
   the results back — a half-duplex return channel over the same loopback origin.
3. The agent reads console events with **`console_tail`** (and `console_clear` to reset), and
   inspects the live runtime with **`evaluate_js`** / **`query_dom`** / **`reload_page`**.
4. On **Ctrl+C** the script is removed and `index.html` is restored. If `ky-ai-browser` ever dies
   without cleaning up, `ky-ai-ng` strips the leftover marker automatically on its next start
   (self-heal), and reverts on its own shutdown — so the file is never left modified.

Injection only ever captures from page-load forward (it can't read console history that printed
before the script loaded), and a strict `script-src 'self'` CSP would block the cross-origin
snippet — both inherent to in-page capture.

## Usage

```
ky-ai-browser [options]            # run alongside `ky-ai-ng serve`
  --project <id>      Which ky-ai-ng frontend to attach to (default: the only one registered)
  --port <N>          ky-ai-browser's own MCP + ingest port (default: 5104)
  --ng-hub-port <N>   ky-ai-ng hub port to discover the frontend (default: 5101)
  -y, --yes           Skip the inject confirmation (default answer is yes anyway)

ky-ai-browser shutdown [--port <N>]   # stop a running instance (removes the script, restores index.html)
```

`shutdown` does the same as **Ctrl+C** but from another terminal — useful when it was launched via
`ky-ai-ng serve --after-start ky-ai-browser` (sharing ng's console), so you can detach the console
capture without taking ng down.

Usually you launch it together with the dev server — let `ky-ai-ng serve` start
`ky-ai-browser` once the first build settles (it shares the console and is killed when serve stops):

```
ky-ai-ng serve --after-start ky-ai-browser -y
```

## MCP tools (for agents)

**Read:**

| Tool | Args | Purpose |
|---|---|---|
| `console_tail` | `lines?`, `level?`, `sinceSeq?`, `grep?`, `pageLoad?`, `compact?`, `appOnly?` | recent browser console events: `{seq, level, args, text, source, line, col, stack, timestamp, pageLoadId}` + `dropped` + `enabled`. `compact` drops `args` when `text` carries them and clips stacks (much smaller payloads); `appOnly` drops transport churn (SignalR/WebSocket negotiation, `[vite]` HMR socket noise) |
| `console_clear` | — | clear the buffer (e.g. before reproducing an issue) |

**Inspect:**

| Tool | Args | Purpose |
|---|---|---|
| `evaluate_js` | `expression`, `awaitPromise?`, `json?`, `timeoutMs?` | evaluate JS in the page (global scope) → `{ok, type, value}` — read live state, e.g. `ng.getComponent(document.querySelector('app-wire')).energized()`. Signals are getter **functions** — **call** them (`.value()`, not `.value`) |
| `query_dom` | `selector`, `all?`, `limit?`, `detail?`, `timeoutMs?` | describe matched element(s): `{tag, id, classes, attributes, text, rect, html}` + `count`. `detail:false` slims each match to `{tag, id?, text}` |
| `get_styles` | `selector`, `props?`, `timeoutMs?` | computed CSS of an element: `{styles:{prop:value,…}, target}` — confirm a transform/hover style actually applied |
| `read_component` | `selector`, `timeoutMs?` | snapshot the Angular component on/above the element: `{component, state, signals, formControls?, methods}` — **signals resolved (called), FormControls unwrapped**, so a clean model read works where `ng.getComponent(el).value` came back empty. Also inline as `__kyai.readComponent(el)` from `evaluate_js` |

**Interact** (synthetic events — see the caveat below). These are **gated**: call `start_interaction`
first (and `stop_interaction` when done) — it shows the user a fixed red overlay with an animated
cursor so they can see the agent driving the page.

| Tool | Args | Purpose |
|---|---|---|
| `start_interaction` | `timeoutMs?` | **required before any interaction** — draws the supervision overlay; returns `{ok, shown}` |
| `stop_interaction` | `timeoutMs?` | remove the overlay and re-block interaction |
| `click` | `selector?` \| `x?,y?`, `button?`, modifiers, `timeoutMs?` | full pointer+mouse sequence then `click()` (default actions fire); returns the element actually hit |
| `move` | `toX,toY`, `fromX?,fromY?`, `durationMs?`, `steps?` | pointermove along a path with enter/leave bookkeeping — drives JS hover/dwell logic |
| `send_key` | `key`, `code?`, `selector?`, modifiers | keydown/keypress/keyup for one key (Enter, Escape, arrows, shortcuts) — does **not** change input value |
| `type_text` | `selector`, `text`, `append?` | set a field's value + fire input/change so Angular/React observe it |
| `scroll` | `selector?`, `x?`, `y?` | scrollIntoView, scroll within an element, or window.scrollTo |
| `focus` | `selector?`, `blur?` | focus (or blur) an element |
| `wait_for` | `selector?` \| `expression?`, `timeoutMs?`, `pollMs?` | poll in-page until an element appears / an expression is truthy — avoid acting before render |
| `reload_page` | `timeoutMs?` | reload the page — re-instantiate everything after a build that changed code (HMR may keep stale instances) |

Inspect/interact tools push work to the page and await the result; if the app isn't open in a browser
they return `pageConnected:false` rather than hanging, and every interaction returns the element it
actually targeted so you can confirm you hit the right thing. That `target` is **minimal by default**
(`{tag, id?, text}`) — enough to confirm the hit and cheap on multi-step flows; pass **`detail:true`**
on any interaction tool for the full element (classes, attributes, rect, clipped `outerHTML`).

> **Synthetic-event caveat.** Interaction events are dispatched in-page, so `isTrusted` is `false`:
> they fire JS handlers but do **not** drive CSS `:hover`, nor user-activation-gated APIs (`window.open`,
> clipboard, fullscreen). A JS-state hover (`(mouseenter)` handler) reproduces via `move`; a pure CSS
> `:hover` does not — that needs a debugger-driven tool (CDP), which this deliberately isn't.

**Supervision overlay.** While interaction is open, a fixed, non-interactable (`pointer-events:none`,
in a shadow root so it never touches your app or its `querySelectorAll`) red frame is drawn over the
page with a cursor icon, and each action animates it — a ripple on `click`, a key cap on `send_key`/
`type_text`, the cursor gliding on `move`. So a human watching always sees, and can follow, what the
agent is doing. The overlay restores itself if the page reloads mid-interaction, and clears itself if
`ky-ai-browser` goes away.

Add the server to `.mcp.json` and allow the tools:

```json
{ "mcpServers": { "ky-ai-browser": { "type": "http", "url": "http://127.0.0.1:5104/mcp" } } }
```
```json
{ "permissions": { "allow": [
  "mcp__ky-ai-browser__console_tail", "mcp__ky-ai-browser__console_clear",
  "mcp__ky-ai-browser__evaluate_js", "mcp__ky-ai-browser__query_dom", "mcp__ky-ai-browser__get_styles",
  "mcp__ky-ai-browser__read_component",
  "mcp__ky-ai-browser__start_interaction", "mcp__ky-ai-browser__stop_interaction",
  "mcp__ky-ai-browser__click", "mcp__ky-ai-browser__move", "mcp__ky-ai-browser__send_key",
  "mcp__ky-ai-browser__type_text", "mcp__ky-ai-browser__scroll", "mcp__ky-ai-browser__focus",
  "mcp__ky-ai-browser__wait_for", "mcp__ky-ai-browser__reload_page"
] } }
```

`ky-ai-browser init` wires these automatically (it reflects the tool list off the binary), so re-run
it to pick up the new tools in an already-configured workspace.

## Update

Update to the latest release with the tool's own command:

```bash
ky-ai-browser update
```

It runs `dotnet tool update --global KY.AI.Browser --no-cache` (`--no-cache` forces a fresh feed
query; without it `dotnet` may report the tool is already up to date from a stale local cache and
skip the update).

Before updating it **stops any other running `ky-ai-browser`** — a running instance keeps the
installed files locked, which is the other reason an update silently does nothing. It lists it,
gives you a chance to close it, sends a graceful shutdown (which also removes the injected script and
restores `index.html`), waits a few seconds, then hard-kills whatever is left, printing each step.

On Windows the update runs in a **new window that opens once `ky-ai-browser` exits** — a running tool
can't overwrite its own files, so it waits for this process to close first. (You can always run the
`dotnet tool update` command yourself.)

## Recipes for agents

ky-ai-browser is a **read → act → verify** loop over the live page. Each call is one page round-trip
(the page is woken from a long-poll, runs the action, posts the result), so **pack work into fewer,
richer calls** — that's the main latency lever.

**1. Explore first (read tools are ungated).** Do multi-step DOM work in a *single* `evaluate_js`
instead of many small reads. Note `value` comes back as a **string** (objects are JSON-stringified),
so return `JSON.stringify(...)` and parse on your side.

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

**3. Click by visible text** (there's no `text:` targeting yet) — read the element's centre, then
click the point. The hit-test sees through the overlay (`pointer-events:none`) and into shadow DOM:

```
const p = evaluate_js({ expression:
  "(()=>{const e=[...document.querySelectorAll('m-dropdown-item')].find(x=>x.textContent.trim()==='Zwei');" +
  "const r=e.getBoundingClientRect();return JSON.stringify({x:Math.round(r.x+r.width/2),y:Math.round(r.y+r.height/2)})})()" })
click({ x: p.x, y: p.y })
```

**4. Verify the model, not just the text.** The rendered text proves "the user saw it change"; the
bound model proves "the app's state actually changed". Use **`read_component`** for a clean read —
it resolves the trap that `ng.getComponent(el).value` comes back empty because modern Angular values
are **signals** (`cmp.value` is a getter *function* — you must *call* it). It calls signal getters,
unwraps FormControls, and lists drivable methods:

```
read_component({ selector: 'm-dropdown' })
// → { ok, component:'MDropdown', state:{ value:1, … }, signals:['value'], methods:['selectIndex','setValue',…] }
```

**When a synthetic click doesn't "take" (custom widgets).** Synthetic events are `isTrusted:false`.
Most things respond (links, buttons, JS-state hover), but some custom components — e.g. a Fomantic-style
`<m-dropdown>` — won't commit state from a synthetic click, *and neither will a programmatic `.click()`*.
You detect this by **verifying after acting** (e.g. `read_component`): the element was hit, but the
value didn't change. The reliable fallback is to drive the component model directly through
`ng.getComponent` — the `methods` from `read_component` tell you what's drivable:

```
// the dropdown ignored synthetic input → drive its model instead (one of the methods read_component listed)
evaluate_js({ expression:
  "(()=>{const c=ng.getComponent(document.querySelector('m-dropdown'));c.selectIndex(1);return c.value()})()" })
```

`read_component` to verify, `evaluate_js` + `ng.getComponent(el)` to drive (`.select(...)`,
`.setValue(...)`) — together they read or change component state when DOM-level input hits a wall.
Inline, the same probing is `__kyai.readComponent(el)`.

**5. Read the console around an action** — `compact:true` shrinks payloads, `appOnly:true` drops
SignalR/`[vite]` transport churn so the app's own logs/errors stand out:

```
console_tail({ compact: true, appOnly: true, lines: 20 })
```

## Latency notes

- One tool call = one page round-trip; the page is woken immediately (no 25s wait), so a call is
  typically tens of ms plus your network/transport. The cost of a flow is the *number* of round-trips.
- **Fewer, richer calls win**: batch DOM reads into one `evaluate_js`; prefer `click({selector})` (find
  + click in one call) over the read-rect-then-coordinate dance; use `wait_for` instead of re-polling
  `query_dom` yourself.

All HTTP is loopback-only; nothing leaves the machine. The capture buffer, event types and the
snippet live in this project; the reversible inject mechanism it drives is a generic
`POST /inject { file?, path, content }` (+ `/uninject`) on the `ky-ai-ng` supervisor's control API.

`evaluate_js` (and the interaction tools) run arbitrary JavaScript / synthetic input in your app's
page — that's the point, and it's safe here because it's a dev-only tool: loopback-bound, gated by the
same inject you confirmed, and its lifetime is the `ky-ai-browser` process you started. The eval
channel shares the capture snippet's misroute token, so an unrelated local tab can't pull or answer requests.
