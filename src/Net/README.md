# ky-ai-dotnet

Run .NET backends so an **agent** can read build/run logs and control them **without managing
OS processes** — no port scanning, no `Stop-Process`, no orphaned `dotnet` host left locking the
build output.

The backend counterpart of `ky-ai-ng`. Built for **many interdependent backends**: one **hub**
(control plane) exposes a single MCP server; each backend runs a **supervisor** (`dotnet watch
run`) that auto-registers with the hub. The agent calls `list` to discover what's running, then
targets any backend by name.

```
                      ┌────────────── ky-ai-dotnet hub (one process) ─────────────┐
   agent ── MCP ────► │  MCP server  +  registry of supervisors                │
                      └───▲───────────────▲───────────────▲────────────────────┘
                          │ register      │ register      │ register / forward
                  ky-ai-dotnet serve    ky-ai-dotnet serve  ky-ai-dotnet serve
                  (MyApp)            (...)            (...)
                  dotnet watch run   dotnet watch run dotnet watch run
```

- **`serve`** — one per backend: runs `dotnet watch run`, tees output to the console (live, for you)
  and to a **rolling log** (last N lines, default 200, oldest dropped), tracks the build/run
  state, hosts a small loopback **REST control API**, and **auto-registers with the hub**
  (re-registers every 15s — start order doesn't matter, and it survives a hub restart).
  `Ctrl+C` kills the whole tree and deregisters.
- **`shutdown`** — stop the hub and every backend it supervises (see below).
- **`init`** — wire ky-ai-dotnet into a Claude Code workspace: finds the nearest `.mcp.json` /
  `.claude/` and, each step confirmed, adds the MCP server and allows its commands (see
  [Client init](#client-init)).
- **one-shot** — tee any other `dotnet` command (`build`, `test`, …) to the console, and to a log
  file when you add `--log-file`.
- **`hub`** — the control plane: one MCP server (`/mcp`) + a registry, no child process. Auto-managed:
  a `serve` starts one on demand (detached, self-exiting when idle) and the agent talks to it — you
  never run it yourself.

## Ownership model

**You own the backends** — you run each `ky-ai-dotnet serve` in your IDE (Rider) and watch its live
console; the hub is **auto-started on demand**. Agents never start/stop OS processes directly;
they call the hub's MCP tools, which forward to the right supervisor (a restart re-runs only that
backend; your console stays live). When a backend's `serve` isn't running, it simply isn't in `list`.

## Usage

```
ky-ai-dotnet serve [options]                # one per backend (dotnet watch run)
  --name <id>         Project name in the hub (default: the .csproj / folder name)
  --log-lines <N>     Lines kept in the rolling log (default: 200; 0 = unlimited)
  --log-file <file>   Also mirror the rolling log to a file (default: off — MCP serves logs)
  --rest-port <N>     Local REST control port (default: OS-assigned)
  --no-watch          Use `dotnet run` instead of `dotnet watch run` (stable for debugging)
  --hub-port <N>      Hub port to register with (default: 5102; rarely needed — doesn't start a hub)
  --no-hub            Standalone: tee + rolling log + local REST only; no hub, no agent access
  --after-start <cmd...>  Run <cmd> once the first build settles (the backend is up); greedy, so
                          put it last. Replaces `serve & sleep 1 && cmd` (PowerShell has no `&`).
                          Shares this console and is killed when serve stops.
  (anything else after `serve` is forwarded to dotnet, e.g. --project ./Api.csproj)

ky-ai-dotnet shutdown                       # stop the hub + every backend it supervises

ky-ai-dotnet init [-y] [--dir <path>]       # wire it into a Claude Code workspace (.mcp.json + allow-list)

ky-ai-dotnet <dotnet args...> [--log-file f.log]   # one-shot tee (--log-file also writes a file)
```

Spawns `dotnet` from PATH. Works from a bare terminal or a Rider run configuration.

## Client init

**`ky-ai-dotnet init` writes both files below for you** — it walks up to the nearest `.mcp.json`
and `.claude/`, adds the server, and allows the commands (idempotent; `-y` skips the prompts,
`--dir <path>` starts the search elsewhere). To wire it by hand instead:

Per-project `.mcp.json` (one entry total, regardless of how many backends):

```json
{
  "mcpServers": {
    "ky-ai-dotnet": {
      "type": "http",
      "url": "http://127.0.0.1:5102/mcp"
    }
  }
}
```

For Claude Code, enable it and allow the tools (`.claude/settings.local.json`):

```json
{
  "permissions": {
    "allow": [
      "mcp__ky-ai-dotnet__list",
      "mcp__ky-ai-dotnet__status",
      "mcp__ky-ai-dotnet__wait_for_build",
      "mcp__ky-ai-dotnet__restart",
      "mcp__ky-ai-dotnet__stop",
      "mcp__ky-ai-dotnet__start",
      "mcp__ky-ai-dotnet__tail",
      "mcp__ky-ai-dotnet__set_log_lines",
      "mcp__ky-ai-dotnet__shutdown"
    ]
  },
  "enabledMcpjsonServers": [
    "ky-ai-dotnet"
  ]
}
```

Runs on port **5102** — the two are fully independent control planes.

### Project name

Each supervisor registers under a name the agent uses to target it. Default: the `.csproj`
filename in the current directory (e.g. `MyApp`), else the folder name. Override with
`--name`. **Keep names unique** — a duplicate overwrites the earlier registration.

### Run it from your IDE

`ky-ai-dotnet` is a plain executable, so run it from a **Shell Script** configuration in Rider —
*not* a .NET config (that would debug ky-ai-dotnet itself):

1. **Run/Debug Configurations → `+` → Shell Script**
2. **Name:** e.g. `MyApp backend (ky-ai-dotnet)`
3. **Execute:** `Script text`
4. **Script text:** `ky-ai-dotnet serve`  *(needs `ky-ai-dotnet.exe` on PATH; otherwise the full publish path)*
5. **Working directory:** the backend project folder (where the `.csproj` is) — this is how
   `dotnet watch` finds the project and how the name defaults to the `.csproj` name
6. **Interpreter path:** `powershell.exe`
7. **Leave "Execute in the terminal" unchecked** — Rider then runs it as a managed process in the
   **Run** tool window (green running state + a working red Stop button). Checked, the script runs in
   a terminal tab Rider doesn't track, so it shows as *not running* and the Stop button below won't
   apply to it.

One config per backend. The MCP hub auto-starts, so there's no separate hub config.

Stopping is safe **however** you do it: Rider's red Stop button hard-kills the process, but the
Job Object (`KILL_ON_JOB_CLOSE`) means the OS still tears down the whole `dotnet` tree — nothing
is left holding the port.

#### Debugging (breakpoints)

**A) Attach to Process — keeps the agent loop.** `dotnet run` / `dotnet watch run` build Debug by
default, so symbols are present.
1. Start your `ky-ai-dotnet serve` config.
2. **Run → Attach to Process** (Ctrl+Alt+F5).
3. Pick the **app** process — `dotnet` running your DLL (not the `dotnet watch` host, not ky-ai-dotnet).
4. Breakpoints hit.

A **rude edit** makes `dotnet watch` restart the app, detaching the debugger (re-attach);
hot-reloadable edits keep it attached. If you debug a lot, run that backend with **`--no-watch`**
(plain `dotnet run`; the process only restarts on an explicit `restart`), so the attach stays stable.

**B) Native Debug session — no ky-ai-dotnet.** Use Rider's normal **.NET Launch Settings Profile**
(green Debug button). Fully integrated, but that instance isn't managed by ky-ai-dotnet (no MCP
control) and can't share the port — so it's either/or per backend, per session.

## Update

Update to the latest release with the tool's own command:

```bash
ky-ai-dotnet update
```

It runs `dotnet tool update --global KY.AI.Net`. On Windows the update runs in a **new window that
opens once `ky-ai-dotnet` exits** — a running tool can't overwrite its own files, so it waits for
this process to close first. (You can always run the `dotnet tool update` command yourself.)

## Build verdict

Detection keys off `dotnet watch` / ASP.NET host phrasing:

- **success** — `Application started` / `Now listening on` / `Hot reload of changes succeeded`
- **failed** — `Build FAILED` / `Waiting for a file to change before restarting`
- **errors** — lines containing `: error ` (counted only during a build, so runtime log lines
  that mention "error" don't inflate the count)
- **warnings** — lines containing `: warning ` (same build-only guard), surfaced as `warnings`/
  `warningLines` so a green build still flags deprecations

Roslyn/MSBuild diagnostics (`File.cs(12,34): error CS0103: …`) are parsed into structured
`diagnostics` (`{severity, file, line, col, message, raw}`), with `raw` kept when a line doesn't
parse. The verdict carries **`settledBy`** — the exact line that produced it — so a mis-matched
detector is obvious. These are string matches; if a verdict looks wrong, check `settledBy` and tune
`DotnetBuildMatcher.cs`.

## MCP tools (for agents)

Exposed by the **hub**; each (except `shutdown`/`list`) takes a `project` (from `list`) — **omit it
when only one backend is registered** and it resolves automatically. Allow-list as
`mcp__ky-ai-dotnet__<name>`. All return JSON except `tail` (text).

| Tool | Args | Purpose |
|---|---|---|
| `list` | — | running backends + each one's last build status. **Call first.** |
| `status` | `project?` | one backend, or all if omitted — includes `building`/`pending`, `errors`/`warnings`, `diagnostics`, `filesInLastBuild` |
| `wait_for_build` | `project?`, `timeoutMs?` | **block until the rebuild/restart settles** (debounced), return the verdict + a noise-free `summary` — the deterministic way to verify after an edit (default 90s) |
| `restart` | `project?` | restart, wait for it to come back up, return the verdict + `summary` |
| `stop` | `project?` | stop the backend (frees the port, unlocks output files); stays registered |
| `start` | `project?` | start if stopped; waits for it to come up |
| `tail` | `project?`, `lines?`, `summary?`, `sinceSeq?`, `grep?` | last N log lines (`0` = whole buffer); `summary` keeps only build-relevant lines, `sinceSeq` scopes to one rebuild, `grep` filters by substring |
| `set_log_lines` | `count`, `project?` | change how many log lines are kept (`0` = unlimited) |
| `shutdown` | — | tear down the **whole stack** — stop every running backend (freeing their ports) and then the hub. Same as the `ky-ai-dotnet shutdown` CLI command and `POST`/`GET /shutdown`. To stop just one app, stop its process in your IDE. |

**When to `restart`:** `dotnet watch` hot-reloads code, so restart only for changes it can't apply
(new files, `.csproj` / config changes, rude edits) or a wedged process. For routine edits, just
`wait_for_build` — its verdict adds `warnings`/`diagnostics` and `filesInLastBuild`/`lastChangeAt`
(the files that build incorporated, so you can confirm your edit landed). Stored log lines are
ANSI-stripped and all ky-ai-dotnet-emitted timestamps are ISO-8601 with offset.

## What the version tracks, and multiple .NET SDKs

`ky-ai-dotnet`'s major version tracks the **.NET SDK whose build output it parses** — not its own
target framework. They usually coincide, but if you build a `net8.0` project with the .NET 10 SDK,
it's the SDK (10) that shapes the output `ky-ai-dotnet` reads, so match the major to your **SDK**
(see the [supported versions](../../README.md#supported-versions) table).

As with `ky-ai-ng`, `PATH` holds the latest major. To run an older major against an older
toolchain, install it into its own versioned folder and invoke it by full path, leaving `PATH` on
the newest.

```powershell
ky-ai-dotnet serve                                            # latest, via PATH
%USERPROFILE%\.nuget\packages\ky.ai.net\9.0.0\tools\ky-ai-dotnet.exe serve   # against the .NET 9 SDK
```

## Files

This project is the thin **.NET seam**; the hub, supervisor, rolling log, build tracker and MCP
tool surface all live in the shared **[`KY.AI.Serve`](../Serve)** library.

- `Program.cs` — arg parsing (`serve` / `shutdown` / `init` / one-shot) and the .NET
  `SupervisorConfig` / `HubConfig`: the `dotnet [watch] run` command, watched extensions, port and
  names.
- `DotnetBuildMatcher.cs` — maps `dotnet watch` / ASP.NET host output lines to build-start /
  settle / error / warning verdicts and parses single-line Roslyn/MSBuild diagnostics into
  `{severity, file, line, col, message}`.

In `KY.AI.Serve` (shared): `HubHost` · `Hub` · `HubTools` (incl. `shutdown`) · `SupervisorHost` ·
`DevServer` · `RollingLog` · `BuildTracker` · `InitCommand` · `JobObject` · `Ansi`.
