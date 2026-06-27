# KY.AI

A small suite of dev-loop tools that let an **AI agent** see and steer your running app —
**without ever touching your OS processes or taking your port**. You run your app exactly like
you do today; the agent gets a safe, by-name way over [MCP](https://modelcontextprotocol.io) to ask
*"did it build? what broke? please restart"* — and, for frontends, to read the browser console and
poke the live page.

| Tool | Command | What it drives |
|------|---------|----------------|
| [`KY.AI.Ng`](src/Ng/README.md)       | `ky-ai-ng`       | the Angular CLI (`ng serve` / `ng build`) — **frontends** |
| [`KY.AI.Net`](src/Net/README.md)     | `ky-ai-dotnet`   | the .NET CLI (`dotnet watch run` / `build`) — **backends** |
| [`KY.AI.Terminal`](src/Terminal/README.md) | `ky-ai-terminal` | a **shared interactive shell** you drive while the agent rides along |
| [`KY.AI.Browser`](src/Browser/README.md)   | `ky-ai-browser`  | a served app's **browser console + live runtime** (attaches to `ky-ai-ng`) |
| [`KY.AI.Serve`](src/Serve/README.md) | —                | the shared hub / supervisor / MCP engine the tools build on |

## The idea

You've paired an AI agent with a real app, and the same friction keeps coming back: it changes
some code, then has to ask **you** whether it actually built. So you search the window of your dev
server, copy the red errors out, paste them in, wait — and do it again on the next change.
Hand the agent the dev server instead and it's worse: for one quick rebuild it asks you to approve
finding the process id, then asks again to kill that process, then asks again to start it back up —
burning a pile of tokens and minutes on something that should be instant. And when you go to restart
the app yourself, the port it left behind is already blocked.

KY.AI ends that loop. **You** run your app exactly like you do today — start it once in your IDE
and keep your live console. Your **agent** gets its own safe, by-name way to ask *"did it build?
what broke? please restart"* — without ever reaching into your processes or taking your port.

The payoff: you stop being the agent's copy-paste relay, and it stops flying blind — after each
change it sees the build go green (or reads the exact error) on its own. Same model for frontends,
backends, the browser runtime, and an interactive shell, so a full-stack agent can drive the whole
loop at once.

### How it fits together

Each tool follows the same shape: **you own the process**, the agent talks to a **hub** (one MCP
endpoint per tool) that forwards to the right **supervisor** by name. Nothing the agent does ever
spawns or kills an OS process directly or steals your port.

```
   you ──run──►  ky-ai-ng serve (MyApp)  ──registers──►  ky-ai-ng hub ──MCP──►  agent
   you ──run──►  ky-ai-ng serve (Admin)  ──registers──►   (port 5101)
                       ▲ live console stays yours          "did MyApp build? restart Admin"
```

A `serve`/session auto-starts its hub on first use; the agent calls `list` to discover what's
running, then targets any app by name (or omits the name when only one is registered).

## What our customers say

> `ky-ai-ng` `wait_for_build` was the workhorse — a deterministic green/red verdict with structured
> diagnostics after every edit, far better than shelling out to npm. The `summary` field that strips
> the esbuild chunk table and `[vite]` ws-proxy spam is exactly right. Combined with the dev server
> consuming your workspace libraries from source, "green build = lib + plugin + templates all
> compile" gave a tight loop. I leaned on it dozens of times today and it never lied to me.
>
> — **Claude**

## Getting Started

Pick whichever install matches your stack — both work on **Windows, macOS and Linux**.

### Using .NET 10 or newer

Install the tools as .NET global tools:

```bash
dotnet tool install --global KY.AI.Ng         # Angular frontends — ng serve / ng build, mirrored for agents
dotnet tool install --global KY.AI.Net        # .NET backends — dotnet watch run / build, mirrored for agents
dotnet tool install --global KY.AI.Terminal   # shared interactive shell — you drive it, the agent rides along
dotnet tool install --global KY.AI.Browser    # browser/runtime console capture for ky-ai-ng
```

This puts the `ky-ai-*` tools on your `PATH`. Wire each into your agent (see
[Wire your agent](#wire-your-agent) below). Update later with `ky-ai-ng update` — it runs the right
`dotnet tool update --no-cache` for you and stops any running instances first.

### Using npm

For Angular devs with no .NET installed, `ky-ai-ng` ships a self-contained binary for each platform:

```bash
npm install --global @ky-ai/ng     # or add it as a devDependency in your Angular project
```

This puts `ky-ai-ng` on your `PATH` with nothing else to install. Update later with `ky-ai-ng update`
— it detects the npm install and runs `npm install --global @ky-ai/ng@latest`, stopping any running
instances first.

### Wire your agent

Run each tool's `init` command once, from anywhere in your workspace — it registers the tool's MCP
server with your agent and allows its tools, detecting your agent and writing the right config for it
(Claude Code, Cursor, VS Code, …). It merges into existing files and is safe to re-run:

```powershell
ky-ai-ng init
ky-ai-dotnet init
ky-ai-terminal init
ky-ai-browser init
```

To wire an agent by hand instead, each tool's README has the exact MCP entry, the full tool
allow-list, and the Rider run-configuration setup.

### Run your project

Start your project through its matching tool, run from the project's own folder — one per project. Everything
the agent needs comes online automatically:

```powershell
ky-ai-ng serve      # in an Angular workspace (the ClientApp folder)
ky-ai-dotnet serve  # in a .NET project folder
```

All traffic is loopback-only — nothing is exposed off the machine.

## Versioning

Each tool's **major version tracks the framework it targets**, so you match the number to your
stack: `ky-ai-ng 22.x` for an Angular 22 workspace, `ky-ai-dotnet 10.x` for the .NET 10 SDK. When a
new framework major lands we verify compatibility and release a matching major, so "use the version
that matches your framework" always holds. `KY.AI.Serve` is the shared engine and carries its own
product version.

### Supported versions

| Tool | Version line | Targets | Notes |
|------|--------------|---------|-------|
| `KY.AI.Ng`       | 22.x | Angular 22  | major **=** Angular major |
| `KY.AI.Net`      | 10.x | .NET 10 SDK | major **=** the .NET **SDK** major whose build output it parses (not its own TFM) |
| `KY.AI.Browser`  | 1.x  | —           | console-capture add-on for `ky-ai-ng` |
| `KY.AI.Terminal` | 1.x  | —           | shared interactive shell you and the agent both drive |
| `KY.AI.Serve`    | 1.x  | —           | shared engine; own product version |

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
src/        tool projects: Serve (shared engine) · Ng · Net · Browser · Terminal (+ *.Tests)
scripts/    pack / publish / dist / version automation (dependency-free C# scripts)
artifacts/  packed NuGet + npm packages — scripts\pack.cmd output (git-ignored)
dist/       runnable local build for testing — scripts\dist.cmd output (git-ignored; put on PATH)
```

The **`Serve`** library holds the hub, supervisor, rolling log, build tracker, MCP plumbing and the
shared `init` / `shutdown` / `update` commands; each framework tool (`Ng`, `Net`, `Browser`,
`Terminal`) is a thin seam on top that supplies its CLI wiring and a build/output matcher.

## Building from source

The `scripts/` folder holds dependency-free C# automation — run each with `dotnet run`, or use the
matching `.cmd` launcher:

```powershell
scripts\dist.cmd       # publish a runnable build of all tools into dist\ for local PATH testing
```
