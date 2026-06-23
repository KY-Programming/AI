# ky-ai-ng

Run Angular dev servers so an **agent** can read build logs and control them **without
managing OS processes** — no port scanning, no `Stop-Process`, no orphaned `node.exe`.

Built for **many interdependent frontends**: one **hub** (control plane) exposes a single
MCP server; each frontend runs a **supervisor** that auto-registers with the hub. The agent
calls `list` to discover what's running, then targets any frontend by name.

```
                      ┌─────────────── ky-ai-ng hub (one process) ───────────────┐
   agent ── MCP ────► │  MCP server  +  registry of supervisors               │
                      └───▲───────────────▲───────────────▲───────────────────┘
                          │ register      │ register      │ register / forward
                  ky-ai-ng serve        ky-ai-ng serve     ky-ai-ng serve
                  (MyApp)            (...)            (...)
                  ng serve + log     ng serve + log  ng serve + log
```

- **`hub`** — the control plane: one MCP server (`/mcp`) + a registry. No ng child. You never run
  it — a `serve` auto-starts one (detached, self-exiting when idle) and the agent talks to it.
- **`serve`** — one per frontend: runs `ng serve`, tees output to the console (live, for you)
  and to an **in-memory rolling buffer** (last N lines, default 200, served over MCP — add
  `--log-file` to also mirror it to disk), tracks the build
  state, hosts a small loopback **REST control API**, and **auto-registers with the hub**
  (re-registers every 15s — start order doesn't matter, and it survives a hub restart).
  Stopping always reaps the whole ng tree: `Ctrl+C` deregisters and tears it down, and a **hard
  kill of `ky-ai-ng` (e.g. Rider's Stop button) also kills the tree** via a Windows Job Object
  (`KILL_ON_JOB_CLOSE`) — the port is never left orphaned, however ky-ai-ng is stopped.
- **one-shot** — tee any other `ng` command (`build`, `version`, …) to console + a full log.

## Ownership model

**You own the dev servers** — you run each `ky-ai-ng serve` in your IDE (Rider) and watch its
live console; the hub is **auto-started on demand**. Agents never start/stop OS processes directly; they call the hub's
MCP tools, which forward to the right supervisor (a restart re-spawns only that ng child;
your console stays live). When a frontend's `serve` isn't running, it simply isn't in `list`.

## Usage

```
ky-ai-ng serve [options]                   # one per frontend
  --name <id>         Project name in the hub (default: parent folder of ClientApp)
  --hub <url>         Hub URL (default: http://127.0.0.1:5101)
  --log-lines <N>     Lines kept in the in-memory log buffer (default: 200)
  --log-file <file>   Also mirror the buffer to a file (default: off — MCP serves logs)
  --control-port <N>  Local REST control port (default: OS-assigned)
  --no-hub            Buffer-only; don't register
  --no-hub-autostart  Use a hub if up, but don't auto-start one
  (anything else after `serve` is forwarded to `ng serve`, e.g. --port 4015)

ky-ai-ng <ng args...> [logfile]            # one-shot tee (full log)
```

The Angular CLI is resolved from the nearest `node_modules\@angular\cli\bin\ng.js` (run via
`node`); otherwise a global `ng` on PATH is used. So it works from a bare terminal, an npm
script, or a Rider run configuration.

### Project name

Each supervisor registers under a name the agent uses to target it. Default: the parent
folder of `ClientApp` (so `C:\...\MyApp\ClientApp` → `MyApp`). Override with
`--name`. **Keep names unique** — a duplicate name overwrites the earlier registration.

### Run it from your IDE

A `start:ai` script per frontend, run in Rider — the first one auto-starts the hub:

```jsonc
// each ClientApp/package.json
"scripts": { "start:ai": "ky-ai-ng serve" }
```

Requires `ky-ai-ng.exe` on `PATH`.

## MCP tools (for agents)

Exposed by the **hub**; each (except `shutdown`) takes a `project` (from `list`). Allow-list as
`mcp__ky-ai-ng__<name>`. All return JSON except `tail` (text).

| Tool | Args | Purpose |
|---|---|---|
| `list` | — | running frontends + each one's last build status. **Call first.** |
| `status` | `project?` | one frontend, or all if omitted — includes `building`/`pending` flags |
| `wait_for_build` | `project`, `timeoutMs?` | **block until the in-flight rebuild settles** (debounced), return `{status, errors, errorLines, durationMs}` — the deterministic way to verify after an edit |
| `restart` | `project` | restart, **wait for the rebuild**, return the verdict (status, errors, duration, error lines, tail) |
| `stop` | `project` | stop the ng child (frees the port); stays registered |
| `start` | `project` | start if stopped; waits for the build |
| `tail` | `project`, `lines?` | last N log lines (`0` = whole buffer) |
| `set_log_lines` | `project`, `count` | change how many log lines are kept |
| `shutdown` | — | stop the **hub** process itself (not a frontend) — frees the published binary so it can be re-published, or cleans up an auto-started hub. Supervisors keep running; a hub auto-starts again the next time a `serve` launches. Also reachable as `POST`/`GET /shutdown`. |

**Verifying an edit:** call `wait_for_build` — it blocks until the rebuild that includes your
change settles (debouncing rapid multi-file saves) and returns the verdict. The verdict carries
`settledBy` — the exact ng line it matched to decide success/failed — so a mis-matched detector
is obvious at a glance. `status` also
exposes `building` (a rebuild is running) and `pending` (a saved change the latest build hasn't
incorporated yet) if you'd rather poll. Stored log lines are ANSI-stripped and all ky-ai-ng
timestamps are ISO-8601 with offset.

**When to `restart`:** `ng serve` hot-reloads code, so restart only for changes it doesn't
pick up — `angular.json` / proxy / `tsconfig` paths, new dependencies — or a wedged server.

### Example `list` payload

```json
{ "frontends": [
  { "name": "MyApp", "controlUrl": "http://127.0.0.1:51234",
    "status": { "running": true, "build": { "status": "success", "errors": 0, "durationMs": 2310 } } }
] }
```

## Client configuration

Per-project `.mcp.json` (one entry total, regardless of how many frontends):

```json
{ "mcpServers": { "ky-ai-ng": { "type": "http", "url": "http://127.0.0.1:5101/mcp" } } }
```

For Claude Code, enable it and allow the tools (`.claude/settings.local.json`):

```json
{
  "permissions": { "allow": [
    "mcp__ky-ai-ng__list", "mcp__ky-ai-ng__status", "mcp__ky-ai-ng__wait_for_build",
    "mcp__ky-ai-ng__restart", "mcp__ky-ai-ng__stop", "mcp__ky-ai-ng__start",
    "mcp__ky-ai-ng__tail", "mcp__ky-ai-ng__set_log_lines", "mcp__ky-ai-ng__shutdown"
  ] },
  "enabledMcpjsonServers": ["ky-ai-ng"]
}
```

## Running multiple Angular majors on one machine

`ky-ai-ng`'s major version tracks the Angular major it targets (see the
[supported versions](../../README.md#supported-versions) table), so `PATH` points at the **latest**
major — right for everyday work. To drive a project pinned to an older Angular, don't rely on
`PATH`: install that major into its own versioned folder and call it by full path, leaving `PATH`
on the newest.

```powershell
ky-ai-ng serve                                           # latest, via PATH
%USERPROFILE%\.nuget\packages\ky.ai.ng\21.0.0\tools\ky-ai-ng.exe serve   # an Angular 21 project
```

That path is the NuGet global-packages cache (where `dotnet restore` unpacks a package); a
`--tool-path` install places the exe elsewhere.

## Files

This project is the thin **Angular seam**; the hub, supervisor, rolling log, build tracker and MCP
tool surface all live in the shared **[`KY.AI.Serve`](../Serve)** library.

- `Program.cs` — arg parsing (`hub` / `serve` / one-shot) and the Angular `SupervisorConfig` /
  `HubConfig`: CLI resolution (`node_modules\@angular\cli`), watched extensions, port and names.
- `NgBuildMatcher.cs` — maps ng/esbuild output lines to build-start / settle / error verdicts.

In `KY.AI.Serve` (shared): `HubHost` · `Hub` · `HubTools` (incl. `shutdown`) · `SupervisorHost` ·
`DevServer` · `RollingLog` · `BuildTracker` · `JobObject` · `Ansi`.
