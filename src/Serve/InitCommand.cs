using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace KY.AI.Serve;

// `<tool> init` — wire THIS tool into a Claude Code workspace in two steps:
//   1. add the tool's MCP server to the nearest `.mcp.json` (walking up from the cwd)
//   2. allow the tool's MCP commands (and pre-enable the server) in `.claude/settings.local.json`
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

    public sealed record InitPaths(
        string Root,
        string McpPath, bool McpExists,
        string SettingsPath, bool SettingsExists,
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
        var startDir = Environment.CurrentDirectory;
        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "-y" or "--yes": assumeYes = true; break;
                case "--dir": if (++i < rest.Length) startDir = Path.GetFullPath(rest[i]); break;
                case "-h" or "--help": PrintHelp(toolName, hubPort); return 0;
                default:
                    Console.Error.WriteLine($"{toolName} init: unknown option '{rest[i]}'. Try `{toolName} init --help`.");
                    return 1;
            }
        }

        var commands = DiscoverToolNames(toolsAssembly ?? typeof(InitCommand).Assembly);
        if (commands.Count == 0)
        {
            Console.Error.WriteLine($"{toolName} init: found no MCP commands to allow (internal error).");
            return 1;
        }

        var paths = Discover(startDir);
        var url = $"http://127.0.0.1:{hubPort}/mcp";

        Console.WriteLine($"{toolName} init");
        Console.WriteLine($"  Agent:        Claude Code  {(paths.AgentDetected ? "(found .claude/)" : $"(no .claude/ — will create under {paths.Root})")}");
        Console.WriteLine($"  MCP config:   {paths.McpPath}{(paths.McpExists ? "" : "  (will create)")}");
        Console.WriteLine($"  Local config: {paths.SettingsPath}{(paths.SettingsExists ? "" : "  (will create)")}");
        Console.WriteLine();

        var rc = 0;

        // ── step 1: the MCP server entry ── (prompt only when it would actually change the file)
        try
        {
            var existing = paths.McpExists ? File.ReadAllText(paths.McpPath) : null;
            var res = MergeMcpJson(existing, toolName, hubPort);
            if (!res.Changed)
            {
                Console.WriteLine($"  {Check} MCP server '{toolName}' already configured — no change.");
            }
            else if (Confirm($"Add the {toolName} MCP server to .mcp.json?", assumeYes))
            {
                WriteFile(paths.McpPath, res.Json);
                Console.WriteLine($"  {Check} {(res.Added ? "added" : "updated")} MCP server '{toolName}' → {url}");
            }
            else
            {
                Console.WriteLine($"  {Bullet} skipped MCP server.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ! could not update {paths.McpPath}: {ex.Message}");
            rc = 1;
        }

        Console.WriteLine();

        // ── step 2: the allow-list + enabled server ── (prompt only when something is missing)
        try
        {
            var existing = paths.SettingsExists ? File.ReadAllText(paths.SettingsPath) : null;
            var res = MergeSettingsJson(existing, toolName, commands);
            if (res.CommandsAdded == 0 && !res.ServerEnabledAdded)
            {
                Console.WriteLine($"  {Check} all {commands.Count} commands already allowed — no change.");
            }
            else if (Confirm($"Allow all {commands.Count} of {toolName}'s MCP commands for this project?", assumeYes))
            {
                WriteFile(paths.SettingsPath, res.Json);
                var partial = res.CommandsAdded > 0 && res.CommandsAdded < commands.Count;
                var bits = new List<string>();
                if (res.CommandsAdded > 0)
                    bits.Add($"{res.CommandsAdded} command{(res.CommandsAdded == 1 ? "" : "s")} allowed{(partial ? $" (of {commands.Count})" : "")}");
                if (res.ServerEnabledAdded) bits.Add("server enabled");
                Console.WriteLine($"  {Check} {string.Join(", ", bits)}.");
            }
            else
            {
                Console.WriteLine($"  {Bullet} skipped allow-list.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ! could not update {paths.SettingsPath}: {ex.Message}");
            rc = 1;
        }

        Console.WriteLine();
        var runPart = string.IsNullOrEmpty(runHint) ? "" : $", then run `{toolName} {runHint}`";
        Console.WriteLine(rc == 0
            ? $"Done. Reload your MCP client (restart Claude Code) to pick up '{toolName}'{runPart}."
            : "Finished with errors — see the messages above.");
        return rc;
    }

    // ── discovery: nearest `.claude/` dir and `.mcp.json`, walking up from startDir ──
    // Missing pieces are planned next to whichever was found (the project root), else in startDir.
    public static InitPaths Discover(string startDir)
    {
        startDir = Path.GetFullPath(startDir);

        // Stop before the user's home directory: `~/.claude` is Claude Code's *global* config and
        // `~/.mcp.json` is not project-scoped, so neither should be treated as this project's files.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stopBefore = string.IsNullOrEmpty(home) ? null : Path.GetFullPath(home);

        var claudeDir = FindUp(startDir, stopBefore, d =>
        {
            var p = Path.Combine(d, ".claude");
            return Directory.Exists(p) ? p : null;
        });
        var mcpFile = FindUp(startDir, stopBefore, d =>
        {
            var p = Path.Combine(d, ".mcp.json");
            return File.Exists(p) ? p : null;
        });

        var root =
            claudeDir is not null ? Path.GetDirectoryName(claudeDir)! :
            mcpFile is not null ? Path.GetDirectoryName(mcpFile)! :
            startDir;

        var mcpPath = mcpFile ?? Path.Combine(root, ".mcp.json");
        var claudeOut = claudeDir ?? Path.Combine(root, ".claude");
        var settingsPath = Path.Combine(claudeOut, "settings.local.json");

        return new InitPaths(
            root,
            mcpPath, mcpFile is not null,
            settingsPath, File.Exists(settingsPath),
            claudeDir is not null);
    }

    // ── merge the tool's server into .mcp.json (preserving any other content) ──
    public static McpMergeResult MergeMcpJson(string? existing, string toolName, int hubPort)
    {
        var root = ParseObjectOrNew(existing);
        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        var desired = new JsonObject
        {
            ["type"] = "http",
            ["url"] = $"http://127.0.0.1:{hubPort}/mcp",
        };

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
        {toolName} init — wire this tool into a Claude Code workspace.

        Walks up from the current directory to find the nearest `.mcp.json` and `.claude/`
        folder, then adds the {toolName} MCP server (127.0.0.1:{hubPort}) to `.mcp.json` and
        allows its MCP commands in `.claude/settings.local.json`. Each step prompts only when it
        would change the file (an already-configured step just reports "no change"). Both steps
        merge into existing files and are safe to re-run.

          {toolName} init [options]
            -y, --yes        Accept both prompts (non-interactive)
            --dir <path>     Start the search from <path> instead of the current directory
            -h, --help       Show this help

        Run it once per tool in a full-stack repo; each merges into the same shared files.
        """);
    }
}
