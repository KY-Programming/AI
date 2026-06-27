using System.Text.Json.Nodes;
using KY.AI.Serve;
using ModelContextProtocol.Server;
using Xunit;

namespace KY.AI.Serve.Tests;

// Exercises the pure, file-system-free core of `<tool> init`: MCP-command reflection, the two
// JSON merges (idempotent, content-preserving), and the walk-up discovery of .mcp.json / .claude.
public class InitCommandTests
{
    // ── DiscoverToolNames: reflect the MCP command list off a tool's MCP tool type ──

    [Fact]
    public void DiscoverToolNames_reads_hub_tools_from_serve_assembly()
    {
        var names = InitCommand.DiscoverToolNames(typeof(InitCommand).Assembly);

        // HubTools (the commands ky-ai-ng / ky-ai-dotnet expose), sorted and de-duplicated.
        Assert.Equal(
            new[] { "list", "restart", "set_log_lines", "shutdown", "start", "status", "stop", "tail", "wait_for_build" },
            names);
    }

    [Fact]
    public void DiscoverToolNames_uses_explicit_names_falls_back_to_snake_case_and_ignores_non_tools()
    {
        // Reflects FakeTools below: explicit names are kept as-is, an attribute with no Name
        // falls back to snake_case of the method name, plain methods are ignored, output is sorted.
        var names = InitCommand.DiscoverToolNames(typeof(FakeTools).Assembly);

        Assert.Equal(new[] { "alpha", "mixed_case_name", "zebra" }, names);
    }

    // ── MergeMcpJson ──

    [Fact]
    public void MergeMcpJson_adds_server_to_empty_input()
    {
        var res = InitCommand.MergeMcpJson(null, "ky-ai-ng", 5101);

        Assert.True(res.Added);
        Assert.True(res.Changed);
        var server = JsonNode.Parse(res.Json)!["mcpServers"]!["ky-ai-ng"]!;
        Assert.Equal("http", server["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:5101/mcp", server["url"]!.GetValue<string>());
    }

    [Fact]
    public void MergeMcpJson_preserves_existing_servers()
    {
        const string existing = """{ "mcpServers": { "other": { "type": "http", "url": "http://127.0.0.1:9999/mcp" } } }""";

        var res = InitCommand.MergeMcpJson(existing, "ky-ai-dotnet", 5102);

        var servers = JsonNode.Parse(res.Json)!["mcpServers"]!.AsObject();
        Assert.True(servers.ContainsKey("other"));
        Assert.True(servers.ContainsKey("ky-ai-dotnet"));
        Assert.Equal("http://127.0.0.1:9999/mcp", servers["other"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void MergeMcpJson_is_idempotent_when_already_present()
    {
        var first = InitCommand.MergeMcpJson(null, "ky-ai-ng", 5101);
        var second = InitCommand.MergeMcpJson(first.Json, "ky-ai-ng", 5101);

        Assert.False(second.Changed);
        Assert.False(second.Added);
    }

    [Fact]
    public void MergeMcpJson_updates_when_url_differs()
    {
        var first = InitCommand.MergeMcpJson(null, "ky-ai-ng", 5101);

        var changed = InitCommand.MergeMcpJson(first.Json, "ky-ai-ng", 5999); // different port

        Assert.True(changed.Changed);
        Assert.False(changed.Added);  // updated, not added
        Assert.Contains("5999", changed.Json);
    }

    [Fact]
    public void MergeMcpJson_throws_on_non_object_root()
    {
        Assert.Throws<InvalidDataException>(() => InitCommand.MergeMcpJson("[1,2,3]", "ky-ai-ng", 5101));
    }

    // ── MergeMcpJson with non-Claude agent shapes ──

    [Fact]
    public void MergeMcpJson_cursor_shape_omits_type_under_mcpServers()
    {
        var res = InitCommand.MergeMcpJson(null, "ky-ai-ng", 5101, AgentTargets.Cursor.Shape);

        Assert.True(res.Added);
        var server = JsonNode.Parse(res.Json)!["mcpServers"]!["ky-ai-ng"]!.AsObject();
        Assert.False(server.ContainsKey("type"));                              // Cursor infers from `url`
        Assert.Equal("http://127.0.0.1:5101/mcp", server["url"]!.GetValue<string>());
    }

    [Fact]
    public void MergeMcpJson_cursor_shape_is_idempotent()
    {
        var first = InitCommand.MergeMcpJson(null, "ky-ai-ng", 5101, AgentTargets.Cursor.Shape);
        var second = InitCommand.MergeMcpJson(first.Json, "ky-ai-ng", 5101, AgentTargets.Cursor.Shape);

        Assert.False(second.Changed);
        Assert.False(second.Added);
    }

    [Fact]
    public void MergeMcpJson_vscode_shape_uses_servers_key_with_type()
    {
        var res = InitCommand.MergeMcpJson(null, "ky-ai-ng", 5101, AgentTargets.VsCode.Shape);

        var root = JsonNode.Parse(res.Json)!.AsObject();
        Assert.False(root.ContainsKey("mcpServers"));                          // VS Code uses `servers`
        var server = root["servers"]!["ky-ai-ng"]!.AsObject();
        Assert.Equal("http", server["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:5101/mcp", server["url"]!.GetValue<string>());
    }

    [Fact]
    public void MergeMcpJson_vscode_shape_preserves_existing_servers()
    {
        const string existing = """{ "servers": { "other": { "type": "http", "url": "http://127.0.0.1:9999/mcp" } } }""";

        var res = InitCommand.MergeMcpJson(existing, "ky-ai-dotnet", 5102, AgentTargets.VsCode.Shape);

        var servers = JsonNode.Parse(res.Json)!["servers"]!.AsObject();
        Assert.True(servers.ContainsKey("other"));
        Assert.True(servers.ContainsKey("ky-ai-dotnet"));
        Assert.Equal("http://127.0.0.1:9999/mcp", servers["other"]!["url"]!.GetValue<string>());
    }

    // ── agent resolution / detection ──

    [Fact]
    public void Resolve_matches_known_ids_case_insensitively_and_rejects_unknown()
    {
        Assert.Same(AgentTargets.Claude, AgentTargets.Resolve("claude"));
        Assert.Same(AgentTargets.Cursor, AgentTargets.Resolve("Cursor"));
        Assert.Same(AgentTargets.VsCode, AgentTargets.Resolve("VSCODE"));
        Assert.Null(AgentTargets.Resolve("windsurf"));    // no project-scoped config — unsupported
        Assert.Null(AgentTargets.Resolve("nope"));
    }

    [Fact]
    public void Detect_prefers_claude_when_multiple_markers_present()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".claude"));
        Directory.CreateDirectory(Path.Combine(temp.Path, ".cursor"));

        Assert.Same(AgentTargets.Claude, AgentTargets.Detect(temp.Path));
    }

    [Fact]
    public void Detect_picks_cursor_when_only_cursor_marker_present_and_null_when_none()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".cursor"));
        var nested = Path.Combine(temp.Path, "src", "app");
        Directory.CreateDirectory(nested);

        Assert.Same(AgentTargets.Cursor, AgentTargets.Detect(nested));   // walks up to find .cursor/

        using var empty = new TempDir();
        Assert.Null(AgentTargets.Detect(empty.Path));
    }

    // ── MergeSettingsJson ──

    [Fact]
    public void MergeSettingsJson_allows_all_commands_and_enables_server()
    {
        var cmds = new[] { "list", "status", "restart" };

        var res = InitCommand.MergeSettingsJson(null, "ky-ai-ng", cmds);

        Assert.Equal(3, res.CommandsAdded);
        Assert.True(res.ServerEnabledAdded);

        var root = JsonNode.Parse(res.Json)!;
        var allow = root["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("mcp__ky-ai-ng__list", allow);
        Assert.Contains("mcp__ky-ai-ng__status", allow);
        Assert.Contains("mcp__ky-ai-ng__restart", allow);
        var enabled = root["enabledMcpjsonServers"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains("ky-ai-ng", enabled);
    }

    [Fact]
    public void MergeSettingsJson_preserves_unrelated_allow_entries()
    {
        const string existing = """
        { "permissions": { "allow": [ "Bash(dotnet run *)", "mcp__ky-ai-ng__list" ] } }
        """;

        var res = InitCommand.MergeSettingsJson(existing, "ky-ai-ng", new[] { "list", "status" });

        Assert.Equal(1, res.CommandsAdded);          // only `status` is new; `list` already there
        Assert.True(res.ServerEnabledAdded);
        var allow = JsonNode.Parse(res.Json)!["permissions"]!["allow"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("Bash(dotnet run *)", allow); // untouched
        Assert.Single(allow, a => a == "mcp__ky-ai-ng__list"); // not duplicated
        Assert.Contains("mcp__ky-ai-ng__status", allow);
    }

    [Fact]
    public void MergeSettingsJson_is_idempotent()
    {
        var cmds = new[] { "list", "status" };
        var first = InitCommand.MergeSettingsJson(null, "ky-ai-ng", cmds);

        var second = InitCommand.MergeSettingsJson(first.Json, "ky-ai-ng", cmds);

        Assert.Equal(0, second.CommandsAdded);
        Assert.False(second.ServerEnabledAdded);
    }

    // ── Discover (walks up from a start directory) ──

    [Fact]
    public void Discover_finds_mcp_and_claude_walking_up_from_a_nested_dir()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        Directory.CreateDirectory(Path.Combine(root, ".claude"));
        File.WriteAllText(Path.Combine(root, ".mcp.json"), "{}");
        File.WriteAllText(Path.Combine(root, ".claude", "settings.local.json"), "{}");
        var nested = Path.Combine(root, "src", "app", "deep");
        Directory.CreateDirectory(nested);

        var paths = InitCommand.Discover(nested);

        Assert.True(paths.AgentDetected);
        Assert.True(paths.McpExists);
        Assert.True(paths.SettingsExists);
        Assert.Equal(Path.Combine(root, ".mcp.json"), paths.McpPath);
        Assert.Equal(Path.Combine(root, ".claude", "settings.local.json"), paths.SettingsPath);
    }

    [Fact]
    public void Discover_plans_files_in_start_dir_when_nothing_is_found()
    {
        using var temp = new TempDir();
        var start = Path.Combine(temp.Path, "lonely");
        Directory.CreateDirectory(start);

        var paths = InitCommand.Discover(start);

        Assert.False(paths.AgentDetected);
        Assert.False(paths.McpExists);
        Assert.False(paths.SettingsExists);
        Assert.Equal(Path.Combine(start, ".mcp.json"), paths.McpPath);
        Assert.Equal(Path.Combine(start, ".claude", "settings.local.json"), paths.SettingsPath);
    }

    [Fact]
    public void Discover_cursor_places_mcp_inside_cursor_dir_and_has_no_settings()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        Directory.CreateDirectory(Path.Combine(root, ".cursor"));
        var nested = Path.Combine(root, "src", "app");
        Directory.CreateDirectory(nested);

        var paths = InitCommand.Discover(nested, AgentTargets.Cursor);

        Assert.True(paths.AgentDetected);                                  // .cursor/ found walking up
        Assert.Equal(Path.Combine(root, ".cursor", "mcp.json"), paths.McpPath);
        Assert.False(paths.McpExists);                                     // dir exists, file does not yet
        Assert.Null(paths.SettingsPath);                                   // no allow-list step
        Assert.False(paths.SettingsExists);
    }

    [Fact]
    public void Discover_vscode_plans_mcp_in_start_dir_when_no_marker_found()
    {
        using var temp = new TempDir();
        var start = Path.Combine(temp.Path, "lonely");
        Directory.CreateDirectory(start);

        var paths = InitCommand.Discover(start, AgentTargets.VsCode);

        Assert.False(paths.AgentDetected);
        Assert.Equal(Path.Combine(start, ".vscode", "mcp.json"), paths.McpPath);
        Assert.Null(paths.SettingsPath);
    }

    // A throwaway tool type used only to verify reflection (explicit names, snake_case fallback,
    // and that non-tool methods are skipped). It's the only [McpServerToolType] in this assembly.
    [McpServerToolType]
    internal static class FakeTools
    {
        [McpServerTool(Name = "zebra")] public static string Z() => "";
        [McpServerTool(Name = "alpha")] public static string A() => "";
        [McpServerTool] public static string MixedCaseName() => "";   // no Name → snake_case
        public static string NotATool() => "";                        // no attribute → ignored
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "kyai-init-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
