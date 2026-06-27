# ky-ai-dotnet

Run .NET backends so an **agent** can read build/run logs and control them **without managing
OS processes** — no port scanning, no `Stop-Process`, no orphaned `dotnet` host left locking the
build output.

The backend counterpart of [`ky-ai-ng`](../Ng/README.md). Built for **many interdependent
backends**: one **hub** exposes a single MCP server; each backend runs a
**supervisor** that auto-registers with the hub. The agent can
discover what's running, then targets any backend by name.

```
                      ┌────────────── ky-ai-dotnet hub ────────────────────────┐
   agent ── MCP ────► │  MCP server  +  registry of supervisors                │
                      └───▲───────────────▲───────────────▲────────────────────┘
                          │ register      │ register      │ register / forward
                  ky-ai-dotnet serve    ky-ai-dotnet serve  ky-ai-dotnet serve
                  (Api)                 (Worker)            (Gateway)
                  dotnet watch run      dotnet watch run    dotnet watch run
```

---

# For Humans

## What it does

`ky-ai-dotnet serve` wraps `dotnet watch run` for one backend. You keep your normal console view,
while the agent gets to read the build/run logs and ask for a restart over MCP. You start and stop
it; the agent only asks questions and requests a restart.

### Ownership model

**You own the backends** — you run each `ky-ai-dotnet serve` in your IDE and watch its live
console. The agent never starts or stops processes itself; it just talks to the tools, and a restart
re-runs only that one backend while your console stays live. Stop it however you like (`Ctrl+C`,
IDE's Stop button) — the whole `dotnet` process tree always goes down with it, so the port is
never left orphaned.

## Commands

- **`serve`** — one per backend: runs `dotnet watch run`, mirrors the output to your console and to
  the agent, and tracks the build/run state. Start order doesn't matter and it survives a restart.
- **`init`** — wire ky-ai-dotnet into your agent (see [Connect your agent](#connect-your-agent)).
- **`update`** — update to the latest release (see [Update](#update)).
- **`shutdown`** — stop everything ky-ai-dotnet is running.
- **one-shot** — tee any other `dotnet` command (`build`, `test`, …) to the console. (Typing
  `ky-ai-dotnet run` / `watch` lands here too, with a hint pointing you at `serve` — there is no
  separate `run` subcommand on this tool.)

### Usage

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

ky-ai-dotnet init [--agent claude|cursor|vscode] [-y] [--dir <path>]   # wire it into your agent (default: auto-detect)

ky-ai-dotnet <dotnet args...> [--log-file f.log]   # one-shot tee (--log-file also writes a file)
```

Spawns `dotnet` from PATH. Works from a bare terminal or a Rider run configuration, and is fully
independent from `ky-ai-ng` — you can run both at once.

### Project name

Each supervisor registers under a name the agent uses to target it. Default: the `.csproj` filename
in the current directory (e.g. `MyApp`), else the folder name. Override with `--name`. **Keep names
unique** — a duplicate overwrites the earlier registration.

## Run it from your IDE

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
   a terminal tab Rider doesn't track, so it shows as *not running* and the Stop button won't apply.

One config per backend. The MCP hub auto-starts, so there's no separate hub config.

### Debugging (breakpoints)

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

## Connect your agent

**`ky-ai-dotnet init` wires it into your agent for you** — it targets **Claude Code, Cursor, or
VS Code**. Choose with `--agent <claude|cursor|vscode>`, or omit it to auto-detect the agent your
workspace already uses and confirm via an interactive picker.

To wire it by hand instead, the per-agent files are:

**Claude Code** — `.mcp.json` (one entry total, regardless of how many backends):

```json
{ "mcpServers": { "ky-ai-dotnet": { "type": "http", "url": "http://127.0.0.1:5102/mcp" } } }
```

plus allowing its tools in `.claude/settings.local.json`:

```json
{
  "permissions": { "allow": [
    "mcp__ky-ai-dotnet__list", "mcp__ky-ai-dotnet__status", "mcp__ky-ai-dotnet__wait_for_build",
    "mcp__ky-ai-dotnet__restart", "mcp__ky-ai-dotnet__stop", "mcp__ky-ai-dotnet__start",
    "mcp__ky-ai-dotnet__tail", "mcp__ky-ai-dotnet__set_log_lines", "mcp__ky-ai-dotnet__shutdown"
  ] },
  "enabledMcpjsonServers": ["ky-ai-dotnet"]
}
```

**Cursor** — `.cursor/mcp.json` (transport inferred from `url`); **VS Code** — `.vscode/mcp.json`
(note the top-level `servers` key). Neither has a file-based allow-list — tools are toggled in the
editor's UI:

```json
{ "mcpServers": { "ky-ai-dotnet": { "url": "http://127.0.0.1:5102/mcp" } } }              // .cursor/mcp.json
{ "servers":    { "ky-ai-dotnet": { "type": "http", "url": "http://127.0.0.1:5102/mcp" } } } // .vscode/mcp.json
```

## Update

Update to the latest release with the tool's own command:

```bash
ky-ai-dotnet update
```

It runs the matching `dotnet tool update`. Before updating it **stops any other running instance**
first.

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

---

# For Agents

All tools are exposed by the **hub** (`http://127.0.0.1:5102/mcp`). Each (except `shutdown`/`list`)
takes a `project` (a name from `list`) — **omit it when only one backend is registered** and it
resolves automatically. Allow-list each as `mcp__ky-ai-dotnet__<name>`. All return JSON except
`tail` (text).

| Tool | Args | Purpose |
|---|---|---|
| `list` | `detail?` | running backends, each a compact `{name, running, pid, build:{status, errors, warnings, building, pending}}`. **Call first.** `detail=true` (or `status` with no project) for the full payload |
| `status` | `project?` | one backend, or all if omitted — includes `building`/`pending`, `errors`/`warnings`, `diagnostics`, `filesInLastBuild` |
| `wait_for_build` | `project?`, `timeoutMs?` (default 60000) | **block until the rebuild/restart settles** (debounced), return the verdict + a noise-free `summary` — the deterministic way to verify after an edit |
| `restart` | `project?` | restart, wait for it to come back up, return the verdict + `summary` |
| `stop` | `project?` | stop the backend (frees the port, unlocks output files); stays registered |
| `start` | `project?` | start if stopped; waits for it to come up |
| `tail` | `project?`, `lines?`, `summary?`, `sinceSeq?`, `grep?` | last N log lines (`0` = whole buffer); `summary` keeps only build-relevant lines, `sinceSeq` scopes to one rebuild, `grep` filters by substring |
| `set_log_lines` | `count`, `project?` | change how many log lines are kept (`0` = unlimited) |
| `shutdown` | — | tear down the **whole stack** — stop every running backend (freeing their ports) and then the hub. Same as the `ky-ai-dotnet shutdown` CLI command and `POST`/`GET /shutdown`. To stop just one app, stop its process in your IDE. |

> The default `wait_for_build` timeout is **60s**. Backend cold builds can be slow — pass a larger
> `timeoutMs` (e.g. `120000`) for a first build after a clean.

## Verifying an edit

For routine edits, just call `wait_for_build` — its verdict adds `warnings`/`diagnostics` and
`filesInLastBuild`/`lastChangeAt` (the files that build incorporated, so you can confirm your edit
landed). Stored log lines are ANSI-stripped and all ky-ai-dotnet-emitted timestamps are ISO-8601
with offset.

**When to `restart`:** `dotnet watch` hot-reloads code, so restart only for changes it can't apply
(new files, `.csproj` / config changes, rude edits) or a wedged process.

## Build verdict

Detection keys off `dotnet watch` / ASP.NET host phrasing:

- **success** — `Application started` / `Now listening on` / `Hot reload of changes succeeded`
- **failed** — `Build FAILED` / `Waiting for a file to change before restarting`
- **errors** — lines containing `: error ` (counted only during a build, so runtime log lines that
  mention "error" don't inflate the count)
- **warnings** — lines containing `: warning ` (same build-only guard), surfaced as
  `warnings`/`warningLines` so a green build still flags deprecations

Roslyn/MSBuild diagnostics (`File.cs(12,34): error CS0103: …`) are parsed into structured
`diagnostics` (`{severity, file, line, col, message, raw}`), with `raw` kept when a line doesn't
parse. The verdict carries **`settledBy`** — the exact line that produced it — so a mis-matched
detector is obvious. These are string matches; if a verdict looks wrong, check `settledBy` and tune
`DotnetBuildMatcher.cs`.

---

## Files (internals)

This project is the thin **.NET seam**; the hub, supervisor, rolling log, build tracker and MCP
tool surface all live in the shared **[`KY.AI.Serve`](../Serve)** library.

- `Program.cs` — arg parsing (`serve` / `shutdown` / `init` / `update` / one-shot) and the .NET
  `SupervisorConfig` / `HubConfig`: the `dotnet [watch] run` command, watched extensions, port and
  names.
- `DotnetBuildMatcher.cs` — maps `dotnet watch` / ASP.NET host output lines to build-start / settle
  / error / warning verdicts and parses single-line Roslyn/MSBuild diagnostics into
  `{severity, file, line, col, message}`.

In `KY.AI.Serve` (shared): `HubHost` · `Hub` · `HubTools` (incl. `shutdown`) · `SupervisorHost` ·
`DevServer` · `RollingLog` · `BuildTracker` · `InitCommand` · `UpdateCommand` · `JobObject` · `Ansi`.
