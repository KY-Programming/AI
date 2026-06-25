# ky-ai-browser

Read a served front-end's **browser/runtime console** from an AI **agent** over MCP — the runtime
counterpart to [`ky-ai-ng`](https://www.nuget.org/packages/KY.AI.Ng)'s build tools. It captures
`console.log/info/warn/error`, uncaught exceptions and unhandled promise rejections (with source
location + stack) so the agent can read them via `console_tail` instead of you copy-pasting from
devtools.

## How it works

`ky-ai-browser` is a **process you run** next to a running `ky-ai-ng serve` — its lifetime is the
on/off switch, so *you* control the (reversible) manipulation:

1. On start it finds the running `ky-ai-ng` frontend (via the hub's registry) and — after a
   confirmation (**default yes**) — asks `ky-ai-ng` to inject a tiny capture `<script>` into the
   app's `index.html` (wrapped in `ky-ai-ng-inject` markers, at `/html/head`).
2. The dev server reloads the page; the snippet patches `console.*` + the error events and POSTs
   them back to `ky-ai-browser` (loopback, cross-origin). No proxy, HMR stays native.
3. The agent reads them with the **`console_tail`** MCP tool (and `console_clear` to reset).
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
```

## MCP tools (for agents)

| Tool | Args | Purpose |
|---|---|---|
| `console_tail` | `lines?`, `level?`, `sinceSeq?`, `grep?`, `pageLoad?` | recent browser console events: `{seq, level, args, text, source, line, col, stack, timestamp, pageLoadId}` + `dropped` + `enabled` |
| `console_clear` | — | clear the buffer (e.g. before reproducing an issue) |

Add the server to `.mcp.json` and allow the tools:

```json
{ "mcpServers": { "ky-ai-browser": { "type": "http", "url": "http://127.0.0.1:5104/mcp" } } }
```
```json
{ "permissions": { "allow": [
  "mcp__ky-ai-browser__console_tail", "mcp__ky-ai-browser__console_clear"
] } }
```

All HTTP is loopback-only; nothing leaves the machine. The capture buffer, event types and the
snippet live in this project; the reversible inject mechanism it drives is a generic
`POST /inject { file?, path, content }` (+ `/uninject`) on the `ky-ai-ng` supervisor's control API.
