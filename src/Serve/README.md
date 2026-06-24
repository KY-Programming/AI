# KY.AI.Serve

The shared engine behind the KY.AI dev-loop tools — the **hub**, **supervisor**, rolling log,
build tracker and MCP tool surface that [`ky-ai-ng`](https://www.nuget.org/packages/KY.AI.Ng) and
[`ky-ai-dotnet`](https://www.nuget.org/packages/KY.AI.Net) build on.

Most users want one of those tools, not this library directly — install them with
`dotnet tool install --global KY.AI.Ng` / `KY.AI.Net`. This package exists so a new framework seam
can reuse the same hub/supervisor machinery:

- **`HubHost` / `Hub` / `HubTools`** — the control plane: one MCP server plus a registry of
  supervisors, addressed by name (incl. the `shutdown` tool).
- **`SupervisorHost` / `DevServer`** — runs a framework CLI, tees output, tracks build state, and
  auto-registers with the hub.
- **`RollingLog` · `BuildTracker` · `JobObject` · `Ansi`** — the build-aware log buffer (each line
  tagged with its build seq + classification, so it serves the raw view, a noise-free `summary`,
  and since-seq / grep filters), the build-verdict logic (errors **and** warnings, structured
  `diagnostics`, and change→build correlation), Windows Job Object process reaping, and ANSI
  stripping.
- **`SetupCommand` / `ShutdownCommand`** — the shared CLI commands each exe dispatches:
  `<tool> setup` wires the tool into a Claude Code workspace (merges its MCP server into the
  nearest `.mcp.json` and its command allow-list + `enabledMcpjsonServers` into
  `.claude/settings.local.json`), and `<tool> shutdown` tears down the hub and its supervisors.

A framework tool supplies a `BuildMatcher` (mapping CLI output to build start/settle/error/warning,
and optionally parsing diagnostic lines into structured `{severity, file, line, col, message}`) and
its supervisor/hub configuration; the rest is shared here.

Loopback-only — nothing is exposed off the machine. See the
[project README](https://github.com/) for the full picture.
