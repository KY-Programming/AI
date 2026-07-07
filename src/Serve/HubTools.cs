using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KY.AI.Serve;

// MCP tools exposed by the hub. Each (except shutdown/list) takes a `project` name (from `list`)
// and is forwarded to that supervisor's REST control API; `project` may be omitted when exactly
// one dev server is registered. Shared by both tools; allow-list as mcp__ky-ai-ng__<name> /
// mcp__ky-ai-dotnet__<name>.
[McpServerToolType]
internal static class HubTools
{
    [McpServerTool(Name = "list"), Description(
        "List the dev servers currently registered with the hub. Call this first to learn the project " +
        "names the other tools expect. Compact by default — each entry is {name, running, pid, build:" +
        "{status, errors, warnings, building, pending}}, just the headline. Set detail=true (or use " +
        "`status` with no project) for the full per-server payload incl. structured diagnostics, file " +
        "lists and log paths. If this or any tool in this group is unreachable / errors with a connection " +
        "failure, the hub (and its dev servers) is down — stop, tell the user, and ask them to restart it " +
        "themselves; don't try to work around it (no port scans, no launching the dev server process " +
        "yourself). Likewise if a project you expect is missing here, ask the user to start it rather than " +
        "launching it yourself.")]
    public static Task<string> List(
        [Description("Include each server's full status (diagnostics, files, log paths) instead of the headline")] bool detail = false)
        => Hub.ListAsync(detail);

    [McpServerTool(Name = "status"), Description(
        "Get the status of one dev server (pass project) or all of them (omit project): running, pid, " +
        "log path/capacity, and the last build result — including `building` (a rebuild is running), " +
        "`pending` (a saved change the latest build hasn't incorporated yet), `errors`/`warnings`, the " +
        "structured `diagnostics`, and `filesInLastBuild`/`lastChangeAt`. For just the headline across " +
        "all servers, use `list` instead.")]
    public static Task<string> Status([Description("Project name; omit for all")] string? project = null)
        => string.IsNullOrWhiteSpace(project) ? Hub.ListAsync(detail: true) : Hub.ForwardAsync(project!, HttpMethod.Get, "/status", 5);

    [McpServerTool(Name = "wait_for_build"), Description(
        "Block until the dev server's in-flight rebuild settles (incorporating the latest file change, " +
        "debouncing rapid multi-file saves) and return the verdict: {status, errors, warnings, " +
        "errorLines, warningLines, diagnostics, durationMs, startedBy, settledBy, lastChangeAt, " +
        "filesInLastBuild, seq} plus a noise-free `summary` — the build's trigger/error/warning/settle " +
        "lines with the esbuild chunk table and [vite] ws-proxy spam dropped. `diagnostics` are " +
        "structured {severity, file, line, col, message, raw} (raw is always kept). `settledBy` is the " +
        "verbatim dev-server line that produced the verdict (its timestamp, if any, is the dev server's " +
        "own). `filesInLastBuild` lists the source files this build incorporated, so you can confirm your " +
        "edit is reflected. When `mayHaveStaleInstances` is true the rebuild changed code (not just " +
        "templates/styles) and a hot reload may have kept already-created objects on the old version — " +
        "`staleHint` explains it; reload the page (ky-ai-browser's reload_page) if runtime state looks " +
        "stale. Use this after editing files to verify deterministically instead of polling tail. Returns " +
        "timedOut:true if it doesn't settle within the timeout. Omit project when only one dev server is " +
        "registered.")]
    public static Task<string> WaitForBuild(
        [Description("Project name; omit when only one is registered")] string? project = null,
        [Description("Max ms to wait (default 60000)")] int timeoutMs = 60000)
    {
        var sec = Math.Clamp(timeoutMs / 1000 + 5, 5, 124);
        return Hub.ForwardAsync(project, HttpMethod.Post, $"/wait-for-build?timeout={timeoutMs}", sec);
    }

    [McpServerTool(Name = "restart"), Description(
        "Restart a dev server and wait for the rebuild; returns the build verdict (status, error/warning " +
        "counts, duration, diagnostics) plus a noise-free `summary` of the build's key lines. Only needed " +
        "for changes the dev server does not hot-reload (config/build files, new dependencies) or when it " +
        "is wedged. Use this instead of killing and relaunching the process yourself. Omit project when " +
        "only one is registered.")]
    public static Task<string> Restart([Description("Project name; omit when only one is registered")] string? project = null)
        => Hub.ForwardAsync(project, HttpMethod.Post, "/restart", 124);

    [McpServerTool(Name = "stop"), Description(
        "Stop a dev server's child process (kills the whole tree, freeing the port). The supervisor stays " +
        "registered. This is the sanctioned way to stop it — do not kill the process yourself (no " +
        "`Stop-Process`/`taskkill`, no port scanning) outside this hub. Omit project when only one is " +
        "registered.")]
    public static Task<string> Stop([Description("Project name; omit when only one is registered")] string? project = null)
        => Hub.ForwardAsync(project, HttpMethod.Post, "/stop", 30);

    [McpServerTool(Name = "start"), Description(
        "Start a dev server if it is stopped; waits for the build and returns the verdict. Use this " +
        "instead of launching the process yourself. Omit project when only one is registered.")]
    public static Task<string> Start([Description("Project name; omit when only one is registered")] string? project = null)
        => Hub.ForwardAsync(project, HttpMethod.Post, "/start", 124);

    [McpServerTool(Name = "tail"), Description(
        "Return the last N lines of a dev server's rolling log (0 = whole buffer). Filters compose: " +
        "summary=true keeps only build-relevant lines (trigger/errors/warnings/settle) and drops the " +
        "esbuild chunk table + [vite] ws-proxy noise; sinceSeq returns only lines from builds at or after " +
        "a given seq (pass a verdict's build.seq for 'just this rebuild'); grep keeps lines containing a " +
        "substring (case-insensitive). Omit project when only one is registered.")]
    public static Task<string> Tail(
        [Description("Project name; omit when only one is registered")] string? project = null,
        [Description("Trailing lines; 0 = whole buffer")] int lines = 0,
        [Description("Only build-relevant lines (drops chunk table + vite noise)")] bool summary = false,
        [Description("Only lines from builds at/after this seq; 0 = all")] long sinceSeq = 0,
        [Description("Keep only lines containing this substring (case-insensitive)")] string? grep = null)
    {
        var q = $"/tail?lines={lines}";
        if (summary) q += "&summary=true";
        if (sinceSeq > 0) q += $"&sinceSeq={sinceSeq}";
        if (!string.IsNullOrEmpty(grep)) q += $"&grep={Uri.EscapeDataString(grep)}";
        return Hub.ForwardAsync(project, HttpMethod.Get, q, 5);
    }

    [McpServerTool(Name = "set_log_lines"), Description(
        "Change how many lines a dev server's rolling log keeps (buffer + file). Default is 200; 0 = " +
        "unlimited. Omit project when only one is registered.")]
    public static Task<string> SetLogLines(
        [Description("Lines to keep (>= 1)")] int count,
        [Description("Project name; omit when only one is registered")] string? project = null)
        => Hub.ForwardAsync(project, HttpMethod.Post, $"/set-log-lines?count={count}", 5);

    [McpServerTool(Name = "shutdown"), Description(
        "Tear down the whole stack: stop every registered dev server (each frees its port) and then " +
        "the hub process itself. Use this to release the published binaries for a re-publish, or to " +
        "stop everything at once — instead of killing the hub process yourself. The `<tool> shutdown` " +
        "CLI command and POST/GET /shutdown do the same.")]
    public static Task<string> Shutdown() => Hub.ShutdownAllAsync();
}
