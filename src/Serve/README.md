# KY.AI.Serve

The shared engine behind the KY.AI dev-loop tools — the **hub**, **supervisor**, rolling log,
build tracker and MCP tool surface that [`ky-ai-ng`](../Ng/README.md),
[`ky-ai-dotnet`](../Net/README.md), [`ky-ai-browser`](../Browser/README.md) and
[`ky-ai-terminal`](../Terminal/README.md) build on.

Most users want one of those tools, not this library directly — install them with
`dotnet tool install --global KY.AI.Ng` / `KY.AI.Net` (plus the optional `KY.AI.Browser` console
add-on). This package exists so a new framework seam can reuse the same hub/supervisor machinery:

- **`HubHost` / `Hub` / `HubTools`** — the control plane: one MCP server plus a registry of
  supervisors, addressed by name (incl. the `shutdown` tool).
- **`SupervisorHost` / `DevServer`** — runs a framework CLI, tees output, tracks build state, and
  auto-registers with the hub.
- **`RollingLog` · `BuildTracker` · `JobObject` · `Ansi`** — the build-aware log buffer (each line
  tagged with its build seq + classification, so it serves the raw view, a noise-free `summary`,
  and since-seq / grep filters), the build-verdict logic (errors **and** warnings, structured
  `diagnostics`, and change→build correlation), Windows Job Object process reaping, and ANSI
  stripping.
- **`HtmlInjector`** — the reversible `index.html` inject/uninject mechanism (`POST /inject`,
  `ky-ai-ng-inject` markers, self-heal) that `ky-ai-browser` drives through the supervisor.
  While the injector heartbeats, the supervisor re-applies the tag whenever the file lost it: some
  projects' serve builders (e.g. `@ky/mantic:serve`) regenerate `index.html` on every `ng serve`
  start, so a dev-server `restart` would otherwise silently disconnect the page for good.
- **`InitCommand` / `ShutdownCommand` / `UpdateCommand`** — the shared CLI commands each exe
  dispatches: `<tool> init` wires the tool into an agent (for Claude Code it installs the tool's
  user-scope plugin and removes the old project entries — see the main README's "Claude Code plugins";
  with `--no-plugin`, or for Cursor/VS Code, it merges the MCP server into the project's MCP config
  and, for Claude, the command allow-list + `enabledMcpjsonServers` into `.claude/settings.local.json`),
  `<tool> shutdown` tears down the hub and its supervisors, and
  `<tool> update` stops running instances and runs the right package-manager update.

A framework tool supplies a `BuildMatcher` (mapping CLI output to build start/settle/error/warning,
and optionally parsing diagnostic lines into structured `{severity, file, line, col, message}`) and
its supervisor/hub configuration; the rest is shared here.

Loopback-only — nothing is exposed off the machine. See the
[project README](../../README.md) for the full picture.

## For Agents

- **Every way the child goes away must settle the build.** Nothing else ends a cycle once the
  dev-server process is gone, and `wait_for_build` / `restart` / `start` would sit out their full
  timeout and report `timedOut`, which reads like a slow build. So all three exits settle it: a
  crash (`Exited`) → `failed` with `exitCode`; a failed `Process.Start()` → `failed` with the start
  error in `settledBy`, and the supervisor stays up instead of throwing; our own stop mid-build →
  `stopped`. A new path that ends the child needs the same.
- **"We stopped it" vs. "it died" is told apart by identity alone.** Every intentional stop (`stop`,
  the old process of a `restart`) goes through `StopChildNoGateAsync`, which nulls `_child` *before*
  killing. The `Exited` handler ignores any process that is no longer `_child`. Keep that order, and
  keep `_child` published under `_ioSync` together with `Start()` — otherwise an instantly crashing
  child reads as an intentional stop.
