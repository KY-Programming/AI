# KY.AI

A small suite of dev-loop tools that run a framework's CLI with output mirrored for AI agents —
one **hub** per stack plus a **supervisor** per app, controllable over MCP.

| Tool | Exe | Drives |
|------|-----|--------|
| [`KY.AI.Ng`](src/Ng/README.md)   | `ky-ai-ng`  | the Angular CLI (`ng serve` / `ng build`) — frontends |
| [`KY.AI.Net`](src/Net/README.md) | `ky-ai-dotnet` | the .NET CLI (`dotnet run` / `dotnet build`) — backends |
| `KY.AI.Serve` | — | the shared hub / supervisor / MCP engine the two tools build on |

<!-- ===== TEMP: two Goal variants for comparison — delete the loser before committing ===== -->

## Goal — Option A (original / more technical)

When an AI agent works on a real app it needs the **dev server running and its build output
visible** — but it shouldn't have to juggle OS processes to get there. Left to raw shell commands an
agent ends up port-scanning, `Stop-Process`-ing the wrong PID, orphaning `node` / `dotnet` trees that
keep holding the port, and scraping colour-coded console spam to guess whether the last edit
compiled.

KY.AI removes that. **You** own the dev servers — you start one supervisor per app in your IDE and
watch its live console as usual. Each supervisor tees that output to an in-memory log, tracks the
build state, and registers with a per-stack **hub** that exposes a single MCP server. The **agent**
never touches processes: it calls `list` to see what's running, then `wait_for_build` / `tail` /
`restart` / `stop` against any app by name.

- **No orphaned processes** — every child runs under a Windows Job Object, so the tree dies with its
  supervisor however it's stopped (Ctrl+C, Rider's Stop button, a hard kill).
- **No port hunting** — apps are addressed by name through the hub, not by scanning for ports.
- **Readable logs** — output is ANSI-stripped into a rolling buffer the agent reads over MCP.
- **Deterministic verification** — `wait_for_build` blocks until the rebuild that includes your edit
  settles, then reports `success` / `failed` plus the exact line that decided it (`settledBy`).

One hub per stack runs side by side (`ky-ai-ng` on 5101, `ky-ai-dotnet` on 5102), so a full-stack
agent drives frontends and backends through two independent MCP servers.

<!-- ===== TEMP: current developer-facing rewrite ===== -->

## Goal — Option B (developer-facing rewrite)

You've paired an AI agent with a real app, and the same friction keeps coming back: it changes
some code, then has to ask **you** whether it actually built. So you alt-tab to the dev server,
copy the red errors out of the console, paste them in, wait — and do it again on the next change.
Hand the agent the dev server instead and it's worse: it kills the wrong process, leaves a zombie
still holding your port, and your own start won't come back up until you go hunt it down.

KY.AI ends that loop. **You** run your app exactly like you do today — start it once in your IDE
and keep your live console. Your **agent** gets its own safe, by-name way to ask *"did it build?
what broke? please restart"* — without ever reaching into your processes or taking your port.

The payoff: you stop being the agent's copy-paste relay, and it stops flying blind — after each
change it sees the build go green (or reads the exact error) on its own. Same for frontends and
backends, so a full-stack agent can drive both at once.

## Getting Started

TODO: Add a note how to install the tools

With both tools on your `PATH`:

1. **Run a supervisor** per app, from the app's own folder — the hub auto-starts on first use:

   ```powershell
   ky-ai-ng serve      # in an Angular workspace (the ClientApp folder)
   ky-ai-dotnet run    # in a .NET project folder
   ```

2. **Wire the MCP client** — one `.mcp.json` per workspace; the ports are fixed:

   ```json
   {
     "mcpServers": {
       "ky-ai-ng":     { "type": "http", "url": "http://127.0.0.1:5101/mcp" },
       "ky-ai-dotnet": { "type": "http", "url": "http://127.0.0.1:5102/mcp" }
     }
   }
   ```

   For Claude Code, also enable the servers and allow their tools (`mcp__ky-ai-ng__*`,
   `mcp__ky-ai-dotnet__*`, including `shutdown`) in `.claude/settings.local.json`. Each tool's README
   has the full allow-list and Rider run-configuration setup.

All traffic is loopback-only — nothing is exposed off the machine.

## Versioning

Each tool's **major version tracks the framework it targets**, so you match the number to your
stack: `ky-ai-ng 22.x` for an Angular 22 workspace, `ky-ai-dotnet 10.x` for the .NET 10 SDK. When a
new framework major lands we verify compatibility and release a matching major, so "use the version that matches your framework" always holds. `KY.AI.Serve` is the
shared engine and carries its own product version.

### Supported versions

| Tool | Version line | Targets | Notes |
|------|--------------|---------|-------|
| `KY.AI.Ng`    | 22.x | Angular 22  | major **=** Angular major |
| `KY.AI.Net`   | 10.x | .NET 10 SDK | major **=** the .NET **SDK** major whose build output it parses (not its own TFM) |
| `KY.AI.Serve` | 1.x  | —           | shared engine; own product version |

### Running an older major alongside the latest

`PATH` always points at the **latest** major of each tool — what you want most of the time. When a
project pins an older framework (say Angular 21), install that major into its own versioned folder
and call it by full path; leave `PATH` on the newest:

```powershell
# everyday: latest, via PATH
ky-ai-ng serve

# an older Angular 21 project: pin the matching major by full path
%USERPROFILE%\.nuget\packages\ky.ai.ng\21.0.0\tools\ky-ai-ng.exe serve
```

That path is the NuGet global-packages cache (where `dotnet restore` unpacks a package); a
`--tool-path` install places the exe elsewhere. The same applies to `ky-ai-dotnet` across .NET SDK
majors — see each tool's README.

## Repository Layout

```
src/      tool projects (Serve, Ng, Net)
scripts/  build / publish / version automation
dist/     aggregated output: every tool's exe + shared DLLs (git-ignored; one PATH entry)
```