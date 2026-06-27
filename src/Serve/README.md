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
- **`InitCommand` / `ShutdownCommand` / `UpdateCommand`** — the shared CLI commands each exe
  dispatches: `<tool> init` wires the tool into a Claude Code workspace (merges its MCP server into
  the nearest `.mcp.json` and its command allow-list + `enabledMcpjsonServers` into
  `.claude/settings.local.json`), `<tool> shutdown` tears down the hub and its supervisors, and
  `<tool> update` stops running instances and runs the right package-manager update.

A framework tool supplies a `BuildMatcher` (mapping CLI output to build start/settle/error/warning,
and optionally parsing diagnostic lines into structured `{severity, file, line, col, message}`) and
its supervisor/hub configuration; the rest is shared here.

Loopback-only — nothing is exposed off the machine. See the
[project README](../../README.md) for the full picture.
