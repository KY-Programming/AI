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

- **`serve`** — one per frontend: runs `ng serve`, tees output to the console (live, for you)
  and to an **in-memory rolling buffer** (last N lines, default 200, served over MCP — add
  `--log-file` to also mirror it to disk), tracks the build
  state, hosts a small loopback **REST control API**, and **auto-registers with the hub**
  (re-registers every 15s — start order doesn't matter, and it survives a hub restart).
  Stopping always reaps the whole ng tree: `Ctrl+C` deregisters and tears it down, and a **hard
  kill of `ky-ai-ng` (e.g. Rider's Stop button) also kills the tree** via a Windows Job Object
  (`KILL_ON_JOB_CLOSE`) — the port is never left orphaned, however ky-ai-ng is stopped.
- **`run <script>`** — supervise an **npm script** (`npm run <script>`) exactly like `serve`: same
  rolling log, REST control, hub registration and build tracking, so the agent watches its builds
  the same way. Use it for `package.json` scripts that wrap `ng serve` (e.g. `start:debug`).
- **`shutdown`** — stop the hub and every frontend it supervises (see below).
- **`init`** — wire ky-ai-ng into a Claude Code workspace: finds the nearest `.mcp.json` /
  `.claude/` and, each step confirmed, adds the MCP server and allows its commands (see
  [Client init](#client-init)).
- **one-shot** — tee any other `ng` command (`build`, `version`, …) to the console, and to a log
  file when you add `--log-file`.
- **`hub`** — the control plane: one MCP server (`/mcp`) + a registry, no ng child. Auto-managed:
  a `serve` starts one on demand (detached, self-exiting when idle) and the agent talks to it — you
  never run it yourself.

## Ownership model

**You own the dev servers** — you run each `ky-ai-ng serve` in your IDE (Rider) and watch its
live console; the hub is **auto-started on demand**. Agents never start/stop OS processes directly; they call the hub's
MCP tools, which forward to the right supervisor (a restart re-spawns only that ng child;
your console stays live). When a frontend's `serve` isn't running, it simply isn't in `list`.

## Usage

```
ky-ai-ng serve [options]                   # one per frontend
  --name <id>         Project name in the hub (default: parent folder of ClientApp)
  --log-lines <N>     Lines kept in the in-memory log buffer (default: 200; 0 = unlimited)
  --log-file <file>   Also mirror the buffer to a file (default: off — MCP serves logs)
  --rest-port <N>     Local REST control port (default: OS-assigned)
  --hub-port <N>      Hub port to register with (default: 5101; rarely needed — doesn't start a hub)
  --no-hub            Standalone: buffer + local REST only; no hub, no agent access
  --after-start <cmd...>  Run <cmd> once the first build settles (the dev server is up); greedy,
                          so put it last. Replaces `serve & sleep 1 && cmd` (PowerShell has no `&`).
                          Shares this console and is killed when serve stops.
  (anything else after `serve` is forwarded to `ng serve`, e.g. --port 4015)
  e.g. ky-ai-ng serve --after-start ky-ai-browser -y

ky-ai-ng run <script> [options] [-- <args>]  # supervise `npm run <script>` like serve
  (same options as serve; runs in the nearest package.json dir, then ./ClientApp)
  e.g. ky-ai-ng run start:debug

ky-ai-ng shutdown                          # stop the hub + every frontend it supervises

ky-ai-ng init [-y] [--dir <path>]          # wire it into a Claude Code workspace (.mcp.json + allow-list)

ky-ai-ng <ng args...> [--log-file f.log]   # one-shot tee (--log-file also writes a file)
```

The Angular CLI (`node_modules\@angular\cli\bin\ng.js`, run via `node`) is found by searching up
from the current directory, then in a `ClientApp` subfolder — so `serve` works whether you launch
from the workspace itself or a full-stack repo root. If neither has it, `ky-ai-ng` errors (it does
**not** fall back to a global `ng`). Make sure dependencies are installed (`npm install`).

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

**Alternative — a Shell Script config** (no `package.json` edit; mirrors the `ky-ai-dotnet` setup):

1. **Run/Debug Configurations → `+` → Shell Script**
2. **Name:** e.g. `MyApp frontend (ky-ai-ng)`
3. **Execute:** `Script text`
4. **Script text:** `ky-ai-ng serve`  *(needs `ky-ai-ng.exe` on PATH; otherwise the full publish path)*
5. **Working directory:** the Angular workspace (the `ClientApp` folder, where `angular.json` is) —
   this is how the name defaults to the parent folder of `ClientApp`
6. **Interpreter path:** `powershell.exe`
7. **Leave "Execute in the terminal" unchecked** — Rider then runs it as a managed process in the
   **Run** tool window (green running state + a working red Stop button). Checked, the script runs in
   a terminal tab Rider doesn't track, so it shows as *not running*.

One config per frontend either way; the MCP hub auto-starts, so there's no separate hub config.

## Client init

**`ky-ai-ng init` writes both files below for you** — it walks up to the nearest `.mcp.json` and
`.claude/`, adds the server, and allows the commands (idempotent; `-y` skips the prompts,
`--dir <path>` starts the search elsewhere). To wire it by hand instead:

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

## Update

Update to the latest release with the tool's own command — it detects how it was installed and runs
the matching package manager:

```bash
ky-ai-ng update
```

- installed via **npm** → `npm install --global @ky-ai/ng@latest`
- installed as a **.NET global tool** → `dotnet tool update --global KY.AI.Ng --no-cache`
  (`--no-cache` forces a fresh feed query; without it `dotnet` may report the tool is already up to
  date from a stale local cache and skip the update)

Before updating it **stops any other running instance** (the hub, `serve` supervisors, stray
one-shots) — they keep the installed files locked, which is the other reason an update silently does
nothing. It lists them, gives you a chance to close them, sends a graceful shutdown, waits a few
seconds, then hard-kills whatever is left, printing each step.

On Windows the update runs in a **new window that opens once `ky-ai-ng` exits** — a running tool
can't overwrite its own files, so it waits for this process to close first. (You can always run the
underlying command yourself.)

## MCP tools (for agents)

Exposed by the **hub**; each (except `shutdown`/`list`) takes a `project` (from `list`) — **omit
it when only one frontend is registered** and it resolves automatically. Allow-list as
`mcp__ky-ai-ng__<name>`. All return JSON except `tail` (text).

| Tool | Args | Purpose |
|---|---|---|
| `list` | `detail?` | running frontends, each a compact `{name, running, pid, build:{status, errors, warnings, building, pending}}`. **Call first.** `detail=true` (or `status` with no project) for the full payload |
| `status` | `project?` | one frontend, or all if omitted — includes `building`/`pending`, `errors`/`warnings`, `diagnostics`, `filesInLastBuild` |
| `wait_for_build` | `project?`, `timeoutMs?` | **block until the in-flight rebuild settles** (debounced), return the verdict + a noise-free `summary` — the deterministic way to verify after an edit |
| `restart` | `project?` | restart, **wait for the rebuild**, return the verdict + `summary` |
| `stop` | `project?` | stop the ng child (frees the port); stays registered |
| `start` | `project?` | start if stopped; waits for the build |
| `tail` | `project?`, `lines?`, `summary?`, `sinceSeq?`, `grep?` | last N log lines (`0` = whole buffer); `summary` drops the chunk table + vite ws-proxy noise, `sinceSeq` scopes to one rebuild, `grep` filters by substring |
| `set_log_lines` | `count`, `project?` | change how many log lines are kept (`0` = unlimited) |
| `shutdown` | — | tear down the **whole stack** — stop every running frontend (freeing their ports) and then the hub. Same as the `ky-ai-ng shutdown` CLI command and `POST`/`GET /shutdown`. To stop just one app, stop its process in your IDE. |

**Verifying an edit:** call `wait_for_build` — it blocks until the rebuild that includes your
change settles (debouncing rapid multi-file saves) and returns the verdict. The verdict carries:

- `errors`/`warnings` counts, `errorLines`/`warningLines` (raw), and structured `diagnostics`
  (`{severity, file, line, col, message, raw}`) so you can jump straight to a fix — `raw` is always
  kept when a line doesn't parse.
- `settledBy` — the verbatim ng line it matched to decide success/failed (its timestamp, if any, is
  the dev server's own — not one ky-ai-ng emits).
- `filesInLastBuild` + `lastChangeAt` — the source files this build incorporated, so you can
  confirm **your** edit is reflected rather than rebuilding to be sure.
- a `summary` alongside the verdict — the build's trigger/error/warning/settle lines only, with the
  esbuild chunk-size table and `[vite] ws proxy error` spam dropped (the same filtering `tail`'s
  `summary=true` applies).

`status` also exposes `building` (a rebuild is running) and `pending` (a saved change the latest
build hasn't incorporated yet) if you'd rather poll. Stored log lines are ANSI-stripped and all
ky-ai-ng-emitted timestamps are ISO-8601 with offset.

**When to `restart`:** `ng serve` hot-reloads code, so restart only for changes it doesn't
pick up — `angular.json` / proxy / `tsconfig` paths, new dependencies — or a wedged server.

### Example `list` payload

Compact by default — the headline per frontend:

```json
{ "frontends": [
  { "name": "MyApp", "running": true, "pid": 4242,
    "build": { "status": "success", "errors": 0, "warnings": 1, "building": false, "pending": false } }
] }
```

With `detail=true` (or `status` with no project) each entry instead carries `controlUrl` and the
full `/status` clone — `durationMs`, `diagnostics`, `filesInLastBuild`, log paths, timestamps.

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

- `Program.cs` — arg parsing (`serve` / `run` / `shutdown` / `init` / one-shot) and the Angular
  `SupervisorConfig` / `HubConfig`: CLI resolution (`node_modules\@angular\cli`), the npm-script
  runner, watched extensions, port and names.
- `NgBuildMatcher.cs` — maps ng/esbuild output lines to build-start / settle / error / warning
  verdicts and parses esbuild's two-line diagnostics into `{severity, file, line, col, message}`.

In `KY.AI.Serve` (shared): `HubHost` · `Hub` · `HubTools` (incl. `shutdown`) · `SupervisorHost` ·
`DevServer` · `RollingLog` · `BuildTracker` · `InitCommand` · `JobObject` · `Ansi`.
