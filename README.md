# KY.AI

A small suite of dev-loop tools that run a framework's CLI with output mirrored for AI agents —
one **hub** per stack plus a **supervisor** per app, controllable over MCP.

| Tool | Exe | Drives |
|------|-----|--------|
| [`KY.AI.Ng`](src/Ng/README.md)   | `ky-ai-ng`  | the Angular CLI (`ng serve` / `ng build`) — frontends |
| [`KY.AI.Net`](src/Net/README.md) | `ky-ai-dotnet` | the .NET CLI (`dotnet run` / `dotnet build`) — backends |
| [`KY.AI.Browser`](src/Browser/README.md) | `ky-ai-browser` | the served app's runtime console — attaches to a running `ky-ai-ng` |
| `KY.AI.Serve` | — | the shared hub / supervisor / MCP engine the tools build on |

## Goal

You've paired an AI agent with a real app, and the same friction keeps coming back: it changes
some code, then has to ask **you** whether it actually built. So you search the window of your dev server,
copy the red errors out, paste them in, wait — and do it again on the next change.
Hand the agent the dev server instead and it's worse: for one quick rebuild it asks you to approve
finding the process id, then asks again to kill that process, then asks again to start it back up —
burning a pile of tokens and minutes on something that should be instant. And when you go to restart
the app yourself, the port it left behind is already blocked.

KY.AI ends that loop. **You** run your app exactly like you do today — start it once in your IDE
and keep your live console. Your **agent** gets its own safe, by-name way to ask *"did it build?
what broke? please restart"* — without ever reaching into your processes or taking your port.

The payoff: you stop being the agent's copy-paste relay, and it stops flying blind — after each
change it sees the build go green (or reads the exact error) on its own. Same for frontends and
backends, so a full-stack agent can drive both at once.

## Getting Started

Pick whichever install matches your stack — both work on **Windows, macOS and Linux**.

**.NET global tools** — for devs who have the .NET SDK (e.g. full-stack); installs the tools:

```bash
dotnet tool install --global KY.AI.Ng
dotnet tool install --global KY.AI.Net
dotnet tool install --global KY.AI.Browser   # optional: browser/runtime console capture for ky-ai-ng
```

These are framework-dependent, so the **.NET 10 runtime** must be installed.

This puts `ky-ai-ng` and `ky-ai-dotnet` on your `PATH` (via the .NET tools dir —
`%USERPROFILE%\.dotnet\tools` on Windows, `~/.dotnet/tools` on macOS/Linux); update later with
`dotnet tool update --global KY.AI.Ng`.

**npm** — for Angular devs with **no .NET installed**; `ky-ai-ng` ships a self-contained binary
(the runtime is bundled in) for each platform:

```bash
npm install --global @ky-ai/ng     # or add it as a devDependency in your Angular project
```

This puts `ky-ai-ng` on your `PATH` with nothing else to install. (`ky-ai-dotnet` stays
.NET-tool-only — its users already have .NET.) To run a build straight from this repo instead, see
[Building from source](#building-from-source).

With the tool(s) on your `PATH`:

1. **Run a supervisor** per app, from the app's own folder — the hub auto-starts on first use:

   ```powershell
   ky-ai-ng serve      # in an Angular workspace (the ClientApp folder)
   ky-ai-dotnet serve  # in a .NET project folder
   ```

2. **Wire the MCP client.** The quickest way for **Claude Code** is the built-in `init` command —
   run it once per tool from anywhere in your workspace:

   ```powershell
   ky-ai-ng init         # add -y to skip the prompts
   ky-ai-dotnet init
   ```

   It walks up to the nearest `.mcp.json` and `.claude/` folder, then — each step confirmed — adds
   the tool's MCP server to `.mcp.json` and allows its commands (and pre-enables the server) in
   `.claude/settings.local.json`. Both writes merge into existing files and are safe to re-run.

   To wire it **by hand** instead — one `.mcp.json` per workspace; the ports are fixed:

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
| `KY.AI.Browser` | 1.x | —          | console-capture add-on for `ky-ai-ng` |
| `KY.AI.Serve` | 1.x  | —           | shared engine; own product version |

### Running an older major alongside the latest

A global install points `PATH` at each tool's **latest** major — what you want most of the time.
When a project pins an older framework (say Angular 21), pin the matching tool major to that project
with a **local tool manifest**, leaving the global install newest:

```powershell
# everyday: latest, globally installed
ky-ai-ng serve

# an older Angular 21 project: pin the matching major locally
dotnet new tool-manifest                     # once per repo (creates .config\dotnet-tools.json)
dotnet tool install KY.AI.Ng --version 21.*  # pinned for this repo only
dotnet ky-ai-ng serve                        # runs the pinned version
```

On the **npm** side it's even simpler: pin `@ky-ai/ng` to the matching major as a project
devDependency and run it through `npx`, so it's versioned with the rest of the Angular workspace:

```bash
npm install --save-dev @ky-ai/ng@21   # pinned in this project's package.json
npx ky-ai-ng serve                     # runs the pinned version
```

A `dotnet tool install --tool-path <dir>` install (or the copy in the NuGet global-packages cache,
`%USERPROFILE%\.nuget\packages\ky.ai.ng\<version>\tools\`) works too. The same applies to
`ky-ai-dotnet` across .NET SDK majors — see each tool's README.

## Repository Layout

```
src/        tool projects (Serve, Ng, Net, Browser)
scripts/    pack / publish / dist / version automation (dotnet-run C# scripts)
artifacts/  packed NuGet packages — scripts\pack.cmd output (git-ignored)
dist/       runnable local build for testing — scripts\dist.cmd output (git-ignored; put on PATH)
```

## Building from source

The `scripts/` folder holds dependency-free C# automation — run each with `dotnet run`, or use the
matching `.cmd` launcher (which needs no PowerShell execution-policy change):

```powershell
scripts\dist.cmd       # publish a runnable build into dist\ for local PATH testing
scripts\pack.cmd       # pack the NuGet packages into artifacts\
scripts\publish.cmd    # push artifacts\*.nupkg to NuGet  (needs NUGET_API_KEY; --dry-run to preview)
scripts\bump.cmd       # bump a project version (interactive, or e.g.  bump Ng --part minor)
```

A release is `pack` then `publish`; for local development, `dist` gives you the same `ky-ai-ng` /
`ky-ai-dotnet` exes on `PATH` without installing from NuGet.