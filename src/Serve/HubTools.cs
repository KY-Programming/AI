using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KY.AI.Serve;

// MCP tools exposed by the hub. Each (except shutdown) takes a `project` name (from `list`)
// and is forwarded to that supervisor's REST control API. Shared by both tools; allow-list as
// mcp__ky-ai-ng__<name> / mcp__ky-ai-dotnet__<name>.
[McpServerToolType]
internal static class HubTools
{
    [McpServerTool(Name = "list"), Description(
        "List the dev servers currently registered with the hub, each with its last build " +
        "status. Call this first to learn the project names the other tools expect.")]
    public static Task<string> List() => Hub.ListAsync();

    [McpServerTool(Name = "status"), Description(
        "Get the status of one dev server (pass project) or all of them (omit project): running, pid, " +
        "log path/capacity, and the last build result, including `building` (a rebuild is running) " +
        "and `pending` (a saved change the latest build hasn't incorporated yet).")]
    public static Task<string> Status([Description("Project name; omit for all")] string? project = null)
        => string.IsNullOrWhiteSpace(project) ? Hub.ListAsync() : Hub.ForwardAsync(project!, HttpMethod.Get, "/status", 5);

    [McpServerTool(Name = "wait_for_build"), Description(
        "Block until the dev server's in-flight rebuild settles (incorporating the latest file change, " +
        "debouncing rapid multi-file saves) and return the verdict: {status, errors, errorLines, " +
        "durationMs, settledBy}. settledBy is the exact output line that produced the verdict (so a " +
        "wrong status is easy to spot). Use this after editing files to verify deterministically " +
        "instead of polling tail. Returns timedOut:true if it doesn't settle within the timeout.")]
    public static Task<string> WaitForBuild(
        [Description("Project name (see list)")] string project,
        [Description("Max ms to wait (default 60000)")] int timeoutMs = 60000)
    {
        var sec = Math.Clamp(timeoutMs / 1000 + 5, 5, 124);
        return Hub.ForwardAsync(project, HttpMethod.Post, $"/wait-for-build?timeout={timeoutMs}", sec);
    }

    [McpServerTool(Name = "restart"), Description(
        "Restart a dev server and wait for the rebuild; returns the build verdict (status, error " +
        "count, duration, error lines, log tail). Only needed for changes the dev server does not " +
        "hot-reload (config/build files, new dependencies) or when it is wedged.")]
    public static Task<string> Restart([Description("Project name (see list)")] string project)
        => Hub.ForwardAsync(project, HttpMethod.Post, "/restart", 124);

    [McpServerTool(Name = "stop"), Description(
        "Stop a dev server's child process (kills the whole tree, freeing the port). The supervisor stays registered.")]
    public static Task<string> Stop([Description("Project name (see list)")] string project)
        => Hub.ForwardAsync(project, HttpMethod.Post, "/stop", 30);

    [McpServerTool(Name = "start"), Description(
        "Start a dev server if it is stopped; waits for the build and returns the verdict.")]
    public static Task<string> Start([Description("Project name (see list)")] string project)
        => Hub.ForwardAsync(project, HttpMethod.Post, "/start", 124);

    [McpServerTool(Name = "tail"), Description(
        "Return the last N lines of a dev server's rolling log (0 = whole buffer).")]
    public static Task<string> Tail(
        [Description("Project name (see list)")] string project,
        [Description("Trailing lines; 0 = whole buffer")] int lines = 0)
        => Hub.ForwardAsync(project, HttpMethod.Get, $"/tail?lines={lines}", 5);

    [McpServerTool(Name = "set_log_lines"), Description(
        "Change how many lines a dev server's rolling log keeps (buffer + file). Default is 200; 0 = unlimited.")]
    public static Task<string> SetLogLines(
        [Description("Project name (see list)")] string project,
        [Description("Lines to keep (>= 1)")] int count)
        => Hub.ForwardAsync(project, HttpMethod.Post, $"/set-log-lines?count={count}", 5);

    [McpServerTool(Name = "shutdown"), Description(
        "Tear down the whole stack: stop every registered dev server (each frees its port) and then " +
        "the hub process itself. Use this to release the published binaries for a re-publish, or to " +
        "stop everything at once. The `<tool> shutdown` CLI command and POST/GET /shutdown do the same.")]
    public static Task<string> Shutdown() => Hub.ShutdownAllAsync();
}
