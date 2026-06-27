using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace KY.AI.Serve;

// `<tool> init` — wire THIS tool's MCP server into an AI agent's workspace. The target agent
// (Claude Code, Cursor, or VS Code — see AgentTargets) is chosen with `--agent`, or auto-detected
// from the workspace and confirmed via an interactive picker. Each agent differs only in where its
// project MCP config lives and the JSON shape of a server entry; Claude Code additionally keeps an
// allow-list. So the work is one or two steps:
//   1. add the tool's MCP server to the agent's project MCP config (e.g. nearest `.mcp.json` for
//      Claude, `.cursor/mcp.json` for Cursor, `.vscode/mcp.json` for VS Code)
//   2. (Claude only) allow the tool's MCP commands (and pre-enable the server) in
//      `.claude/settings.local.json`
// Each step prompts ONLY when it would actually change the file; a step that is already in place
// just reports "no change" without a question. So re-running after new commands ship (or once per
// tool in a full-stack repo) asks about exactly what's missing and nothing else. Each step merges
// into existing files without disturbing other content; the MCP command list is read by reflection
// from the tool's own MCP tool type, so it never drifts.
//
// The discovery and JSON-merge logic is split into pure, file-system-free statics (Discover,
// MergeMcpJson, MergeSettingsJson, DiscoverToolNames) so they can be unit-tested directly.
public static class InitCommand
{
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string Check = "✓"; // ✓
    private const string Bullet = "·"; // ·

    // SettingsPath is non-null only for an agent that keeps a second (allow-list) file — i.e. Claude.
    public sealed record InitPaths(
        string Root,
        string McpPath, bool McpExists,
        string? SettingsPath, bool SettingsExists,
        bool AgentDetected);

    public sealed record McpMergeResult(string Json, bool Changed, bool Added);
    public sealed record SettingsMergeResult(string Json, int CommandsAdded, bool ServerEnabledAdded);

    // ── entry point (called from each tool's Program) ──
    // toolsAssembly defaults to this (Serve) assembly, which carries HubTools — correct for
    // ky-ai-ng / ky-ai-dotnet. ky-ai-terminal passes its own assembly (TerminalTools).
    // runHint is the subcommand to suggest at the end ("serve" for ng/net). Pass null for a tool that
    // is just run by name (e.g. ky-ai-browser) so the closing line doesn't suggest a bogus subcommand.
    public static int Run(string toolName, int hubPort, string[] rest, Assembly? toolsAssembly = null, string? runHint = "serve")
    {
        Cli.TrySetUtf8Console();

        var assumeYes = false;
        string? agentId = null;
        var startDir = Environment.CurrentDirectory;
        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "-y" or "--yes": assumeYes = true; break;
                case "--dir": if (++i < rest.Length) startDir = Path.GetFullPath(rest[i]); break;
                case "--agent":
                    if (++i >= rest.Length)
                    {
                        Console.Error.WriteLine($"{toolName} init: --agent needs a value ({AgentNameList()}).");
                        return 1;
                    }
                    agentId = rest[i];
                    break;
                case "-h" or "--help": PrintHelp(toolName, hubPort); return 0;
                default:
                    Console.Error.WriteLine($"{toolName} init: unknown option '{rest[i]}'. Try `{toolName} init --help`.");
                    return 1;
            }
        }

        // ── pick the target agent: explicit --agent, else auto-detect (confirmed via a picker) ──
        AgentTarget agent;
        if (agentId is not null)
        {
            var resolved = AgentTargets.Resolve(agentId);
            if (resolved is null)
            {
                var hint = string.Equals(agentId, "windsurf", StringComparison.OrdinalIgnoreCase)
                    ? " (Windsurf has no project-scoped MCP config, so init can't target it)"
                    : "";
                Console.Error.WriteLine($"{toolName} init: unknown agent '{agentId}'.{hint} Valid: {AgentNameList()}.");
                return 1;
            }
            agent = resolved;
        }
        else
        {
            var detected = AgentTargets.Detect(startDir) ?? AgentTargets.Claude;
            agent = (assumeYes || Console.IsInputRedirected) ? detected : SelectAgent(detected);
        }

        // The allow-list step needs the tool's MCP command names; only Claude uses it.
        IReadOnlyList<string> commands = Array.Empty<string>();
        if (agent.HasAllowList)
        {
            commands = DiscoverToolNames(toolsAssembly ?? typeof(InitCommand).Assembly);
            if (commands.Count == 0)
            {
                Console.Error.WriteLine($"{toolName} init: found no MCP commands to allow (internal error).");
                return 1;
            }
        }

        var paths = Discover(startDir, agent);
        var url = $"http://127.0.0.1:{hubPort}/mcp";
        var mcpLabel = agent.McpInMarkerDir ? $"{agent.MarkerDir}/{agent.McpFileName}" : agent.McpFileName;

        Console.WriteLine($"Agent:        {agent.DisplayName}{(paths.AgentDetected ? "" : $"  (no {agent.MarkerDir}/ — will create under {paths.Root})")}");
        Console.WriteLine($"MCP config:   {paths.McpPath}{(paths.McpExists ? "" : "  (will create)")}");
        if (agent.HasAllowList)
            Console.WriteLine($"Local config: {paths.SettingsPath}{(paths.SettingsExists ? "" : "  (will create)")}");
        Console.WriteLine();

        var rc = 0;
        var anyChange = false;

        // ── step 1: the MCP server entry ── (prompt only when it would actually change the file)
        try
        {
            var existing = paths.McpExists ? File.ReadAllText(paths.McpPath) : null;
            var res = MergeMcpJson(existing, toolName, hubPort, agent.Shape);
            if (!res.Changed)
            {
                Console.WriteLine($"{Check} MCP server '{toolName}' already configured — no change.");
            }
            else if (Confirm($"Add the {toolName} MCP server to {mcpLabel}?", assumeYes))
            {
                WriteFile(paths.McpPath, res.Json);
                anyChange = true;
                Console.WriteLine($"{Check} {(res.Added ? "added" : "updated")} MCP server '{toolName}' → {url}");
            }
            else
            {
                Console.WriteLine($"{Bullet} skipped MCP server.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"! could not update {paths.McpPath}: {ex.Message}");
            rc = 1;
        }

        // ── step 2: the allow-list + enabled server ── (Claude only; prompt only when something is missing)
        if (agent.HasAllowList)
        {
            Console.WriteLine();
            try
            {
                var existing = paths.SettingsExists ? File.ReadAllText(paths.SettingsPath!) : null;
                var res = MergeSettingsJson(existing, toolName, commands);
                if (res.CommandsAdded == 0 && !res.ServerEnabledAdded)
                {
                    Console.WriteLine($"{Check} all {commands.Count} commands already allowed — no change.");
                }
                else if (Confirm($"Allow all {commands.Count} of {toolName}'s MCP commands for this project?", assumeYes))
                {
                    WriteFile(paths.SettingsPath!, res.Json);
                    anyChange = true;
                    var partial = res.CommandsAdded > 0 && res.CommandsAdded < commands.Count;
                    var bits = new List<string>();
                    if (res.CommandsAdded > 0)
                        bits.Add($"{res.CommandsAdded} command{(res.CommandsAdded == 1 ? "" : "s")} allowed{(partial ? $" (of {commands.Count})" : "")}");
                    if (res.ServerEnabledAdded) bits.Add("server enabled");
                    Console.WriteLine($"{Check} {string.Join(", ", bits)}.");
                }
                else
                {
                    Console.WriteLine($"{Bullet} skipped allow-list.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"! could not update {paths.SettingsPath}: {ex.Message}");
                rc = 1;
            }
        }

        Console.WriteLine();
        var runHintPart = string.IsNullOrEmpty(runHint) ? "" : $" Run `{toolName} {runHint}` to start.";
        if (rc != 0)
            Console.WriteLine("Finished with errors — see the messages above.");
        else if (anyChange)
            Console.WriteLine($"Done. Reload your MCP client to pick up the new config.{runHintPart}");
        else
            Console.WriteLine($"Already configured — nothing to do.{runHintPart}");
        return rc;
    }

    private static string AgentNameList() => string.Join(" | ", AgentTargets.All.Select(a => a.Id));

    // ── discovery: the agent's config folder (and, for Claude, the nearest `.mcp.json`), walking
    // up from startDir ── Missing pieces are planned next to whichever was found (the project root),
    // else in startDir. The 1-arg overload keeps the original Claude-only behaviour for callers/tests.
    public static InitPaths Discover(string startDir) => Discover(startDir, AgentTargets.Claude);

    public static InitPaths Discover(string startDir, AgentTarget agent)
    {
        startDir = Path.GetFullPath(startDir);
        var stopBefore = HomeStop();

        // The agent's marker/config folder (.claude / .cursor / .vscode).
        var markerDir = FindUp(startDir, stopBefore, d =>
        {
            var p = Path.Combine(d, agent.MarkerDir);
            return Directory.Exists(p) ? p : null;
        });

        string root, mcpPath;
        bool mcpExists;
        if (agent.McpInMarkerDir)
        {
            // Cursor / VS Code: the MCP config lives at <root>/<marker>/<file>.
            root = markerDir is not null ? Path.GetDirectoryName(markerDir)! : startDir;
            var markerOut = markerDir ?? Path.Combine(root, agent.MarkerDir);
            mcpPath = Path.Combine(markerOut, agent.McpFileName);
            mcpExists = File.Exists(mcpPath);
        }
        else
        {
            // Claude: `.mcp.json` sits at the project root, found independently of `.claude/`.
            // Stop before the user's home directory: `~/.mcp.json` is not project-scoped.
            var mcpFile = FindUp(startDir, stopBefore, d =>
            {
                var p = Path.Combine(d, agent.McpFileName);
                return File.Exists(p) ? p : null;
            });
            root =
                markerDir is not null ? Path.GetDirectoryName(markerDir)! :
                mcpFile is not null ? Path.GetDirectoryName(mcpFile)! :
                startDir;
            mcpPath = mcpFile ?? Path.Combine(root, agent.McpFileName);
            mcpExists = mcpFile is not null;
        }

        string? settingsPath = null;
        var settingsExists = false;
        if (agent.HasAllowList)
        {
            var markerOut = markerDir ?? Path.Combine(root, agent.MarkerDir);
            settingsPath = Path.Combine(markerOut, "settings.local.json");
            settingsExists = File.Exists(settingsPath);
        }

        return new InitPaths(root, mcpPath, mcpExists, settingsPath, settingsExists, markerDir is not null);
    }

    // ── merge the tool's server into the agent's MCP config (preserving any other content) ──
    // `shape` captures the per-agent JSON differences; it defaults to Claude's for back-compat.
    public static McpMergeResult MergeMcpJson(string? existing, string toolName, int hubPort, McpShape? shape = null)
    {
        shape ??= McpShape.Claude;
        var root = ParseObjectOrNew(existing);
        if (root[shape.ServersKey] is not JsonObject servers)
        {
            servers = new JsonObject();
            root[shape.ServersKey] = servers;
        }

        var desired = new JsonObject();
        if (shape.IncludeType) desired["type"] = "http";
        desired[shape.UrlKey] = $"http://127.0.0.1:{hubPort}/mcp";

        var existed = servers.ContainsKey(toolName);
        var changed = !existed || !JsonNode.DeepEquals(servers[toolName], desired);
        if (changed) servers[toolName] = desired;

        return new McpMergeResult(root.ToJsonString(WriteOpts), changed, !existed);
    }

    // ── merge the tool's allow-list entries + enabled-server flag into settings.local.json ──
    public static SettingsMergeResult MergeSettingsJson(string? existing, string toolName, IReadOnlyList<string> commands)
    {
        var root = ParseObjectOrNew(existing);

        if (root["permissions"] is not JsonObject perms)
        {
            perms = new JsonObject();
            root["permissions"] = perms;
        }
        if (perms["allow"] is not JsonArray allow)
        {
            allow = new JsonArray();
            perms["allow"] = allow;
        }

        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in allow)
            if (AsString(n) is { } s) have.Add(s);

        var added = 0;
        foreach (var cmd in commands)
        {
            var perm = $"mcp__{toolName}__{cmd}";
            if (have.Add(perm))
            {
                allow.Add(perm);
                added++;
            }
        }

        if (root["enabledMcpjsonServers"] is not JsonArray enabled)
        {
            enabled = new JsonArray();
            root["enabledMcpjsonServers"] = enabled;
        }
        var alreadyEnabled = enabled.Any(n => string.Equals(AsString(n), toolName, StringComparison.Ordinal));
        if (!alreadyEnabled) enabled.Add(toolName);

        return new SettingsMergeResult(root.ToJsonString(WriteOpts), added, !alreadyEnabled);
    }

    // ── reflect the MCP command names off the tool's MCP tool type(s) ──
    public static IReadOnlyList<string> DiscoverToolNames(Assembly asm)
    {
        var names = new List<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var type in SafeGetTypes(asm))
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;
            foreach (var m in type.GetMethods(flags))
            {
                var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null) continue;
                names.Add(string.IsNullOrWhiteSpace(attr.Name) ? ToSnakeCase(m.Name) : attr.Name!);
            }
        }
        return names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    // ── helpers ──
    private static void WriteFile(string path, string json)
    {
        Cli.EnsureDir(path);
        File.WriteAllText(path, json + Environment.NewLine); // UTF-8 (no BOM), like the inputs
    }

    // Interactive arrow-key chooser shown when `init` runs without --agent on a real terminal.
    // ↑/↓ (or k/j) move, Enter accepts, Esc/q falls back to `preselect` (the auto-detected agent).
    // Redraws the list in place. Callers must guard non-interactive runs; it also returns `preselect`
    // unchanged when stdin is redirected (mirrors Confirm's EOF = default).
    private static AgentTarget SelectAgent(AgentTarget preselect)
    {
        if (Console.IsInputRedirected) return preselect;

        var items = AgentTargets.All;
        var idx = Math.Max(0, IndexOfId(items, preselect.Id));

        Console.WriteLine("Select the agent to configure  (↑/↓ to move, Enter to accept):");
        DrawAgents(items, idx);

        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow or ConsoleKey.K: idx = (idx - 1 + items.Count) % items.Count; break;
                case ConsoleKey.DownArrow or ConsoleKey.J: idx = (idx + 1) % items.Count; break;
                case ConsoleKey.Enter: return items[idx];
                case ConsoleKey.Escape or ConsoleKey.Q: return preselect;
                default: continue;
            }
            // Redraw over the list (cursor sits just below it). If the console can't reposition
            // (scrolled buffer, no real cursor), fall back to redrawing inline.
            try { Console.SetCursorPosition(0, Console.CursorTop - items.Count); }
            catch { /* leave the cursor where it is */ }
            DrawAgents(items, idx);
        }
    }

    private static void DrawAgents(IReadOnlyList<AgentTarget> items, int idx)
    {
        for (var i = 0; i < items.Count; i++)
            Console.WriteLine($"  {(i == idx ? "›" : " ")} {items[i].DisplayName}");
    }

    private static int IndexOfId(IReadOnlyList<AgentTarget> items, string id)
    {
        for (var i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static bool Confirm(string question, bool assumeYes)
    {
        if (assumeYes)
        {
            Console.WriteLine($"{question} [Y/n] y");
            return true;
        }
        Console.Write($"{question} [Y/n] ");
        var line = Console.ReadLine();              // EOF (redirected/empty) → default Yes
        if (Console.IsInputRedirected) Console.WriteLine();
        return string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    // Stop walking before the user's home directory: a project's search must never escape into the
    // user-global config (`~/.claude`, `~/.mcp.json`, …). Null when the home dir can't be resolved.
    private static string? HomeStop()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.GetFullPath(home);
    }

    // Does `markerDir` (e.g. ".cursor") exist anywhere from startDir up to (not into) the home dir?
    // Used by AgentTargets.Detect to auto-pick the agent a workspace already uses.
    internal static bool HasMarkerUp(string startDir, string markerDir) =>
        FindUp(Path.GetFullPath(startDir), HomeStop(), d =>
        {
            var p = Path.Combine(d, markerDir);
            return Directory.Exists(p) ? p : null;
        }) is not null;

    // Walk up from `start`, returning the first probe hit. Stops *before* probing `stopBefore`
    // (the home dir) so a project's search never escapes into the user-global config.
    private static string? FindUp(string start, string? stopBefore, Func<string, string?> probe)
    {
        var dir = start;
        while (true)
        {
            if (stopBefore is not null && PathEquals(dir, stopBefore)) return null;
            if (probe(dir) is { } hit) return hit;
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) return null;
            dir = parent;
        }
    }

    private static bool PathEquals(string a, string b)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            cmp);
    }

    private static JsonObject ParseObjectOrNew(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var node = JsonNode.Parse(text);
        return node as JsonObject
               ?? throw new InvalidDataException("expected a JSON object at the document root");
    }

    private static string? AsString(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToArray()!; }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static void PrintHelp(string toolName, int hubPort)
    {
        Console.WriteLine($"""
        {toolName} init — wire this tool into an AI agent's workspace (Claude Code, Cursor, or VS Code).

        Walks up from the current directory to find the agent's config folder, then adds the
        {toolName} MCP server (127.0.0.1:{hubPort}) to that agent's project MCP config:
            claude   .mcp.json   (+ allows its commands in .claude/settings.local.json)
            cursor   .cursor/mcp.json
            vscode   .vscode/mcp.json
        Each step prompts only when it would change the file (an already-configured step just reports
        "no change"); writes merge into existing files and are safe to re-run.

          {toolName} init [options]
            --agent <name>   {AgentNameList()}. Default: auto-detect from the workspace and confirm
                             via an interactive picker (under -y / piped input the detected one is used)
            -y, --yes        Accept all prompts (non-interactive)
            --dir <path>     Start the search from <path> instead of the current directory
            -h, --help       Show this help

        Run it once per tool in a full-stack repo; each merges into the same shared files.
        """);
    }
}

// ── agent targets ──
// One supported `init` target. Agents differ only in WHERE their project MCP config lives and the
// JSON SHAPE of a server entry (McpShape); Claude Code additionally keeps an allow-list file.
//
// MarkerDir is the agent's config folder, used both to detect the agent and to place its files.
// McpInMarkerDir is true when the MCP config sits inside MarkerDir (Cursor/VS Code: `.cursor/mcp.json`);
// it's false for Claude, whose `.mcp.json` lives at the project root beside `.claude/`.
public sealed record AgentTarget(
    string Id,
    string DisplayName,
    string MarkerDir,
    bool McpInMarkerDir,
    string McpFileName,
    McpShape Shape,
    bool HasAllowList);

// The three ways an HTTP MCP server entry is written across agents:
//   • ServersKey  — the top-level object holding servers ("mcpServers", or "servers" for VS Code)
//   • IncludeType — whether to emit "type": "http" (Cursor infers transport from `url` and omits it)
//   • UrlKey      — the URL property name ("url"; agents that use a different key set it here)
public sealed record McpShape(string ServersKey, bool IncludeType, string UrlKey)
{
    public static readonly McpShape Claude = new("mcpServers", IncludeType: true, UrlKey: "url");
    public static readonly McpShape Cursor = new("mcpServers", IncludeType: false, UrlKey: "url");
    public static readonly McpShape VsCode = new("servers", IncludeType: true, UrlKey: "url");
}

public static class AgentTargets
{
    public static readonly AgentTarget Claude = new(
        "claude", "Claude Code", ".claude", McpInMarkerDir: false, ".mcp.json", McpShape.Claude, HasAllowList: true);
    public static readonly AgentTarget Cursor = new(
        "cursor", "Cursor", ".cursor", McpInMarkerDir: true, "mcp.json", McpShape.Cursor, HasAllowList: false);
    public static readonly AgentTarget VsCode = new(
        "vscode", "VS Code", ".vscode", McpInMarkerDir: true, "mcp.json", McpShape.VsCode, HasAllowList: false);

    // Claude-first order: detection priority (a workspace with `.claude/` stays on Claude) and the
    // order shown in the picker.
    public static readonly IReadOnlyList<AgentTarget> All = new[] { Claude, Cursor, VsCode };

    // Resolve a --agent id (case-insensitive); null for an unknown id so the caller can report it.
    public static AgentTarget? Resolve(string id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    // Auto-pick the agent a workspace already uses by walking up for each agent's marker dir, in
    // `All` (Claude-first) order. Null when none is found.
    public static AgentTarget? Detect(string startDir) =>
        All.FirstOrDefault(a => InitCommand.HasMarkerUp(startDir, a.MarkerDir));
}
