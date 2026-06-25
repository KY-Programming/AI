using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace KY.AI.Browser;

// MCP tools ky-ai-browser exposes (allow-list as mcp__ky-ai-browser__console_tail / __console_clear).
// They read the in-process capture buffer directly. If capture isn't attached they return
// enabled:false rather than erroring, so the agent can tell "not running" from "no events".
[McpServerToolType]
internal static class BrowserTools
{
    [McpServerTool(Name = "console_tail"), Description(
        "Return recent BROWSER/runtime console events from the app ky-ai-browser is attached to " +
        "(console.log/info/warn/error, uncaught exceptions, unhandled promise rejections). Each event is " +
        "{seq, level, args, text, source, line, col, stack, timestamp, pageLoadId}; the response also " +
        "carries `dropped` (events lost to a flood) and `enabled` (false when ky-ai-browser isn't running " +
        "— start it next to your `ky-ai-ng serve`). Filters compose: level keeps that severity and above " +
        "(debug<log<info<warn<error<exception); sinceSeq returns events after a prior tail's max seq; grep " +
        "is a case-insensitive substring over text+stack; pageLoad isolates one page load (reload boundary).")]
    public static string ConsoleTail(
        [Description("Trailing events; 0 = whole buffer (default 200)")] int lines = 0,
        [Description("Min severity: debug|log|info|warn|error|exception")] string? level = null,
        [Description("Only events with seq at/after this; 0 = all")] long sinceSeq = 0,
        [Description("Keep only events whose text/stack contains this substring (case-insensitive)")] string? grep = null,
        [Description("Only events from this pageLoadId (one reload boundary)")] string? pageLoad = null)
        => Capture.Collector is { } c
            ? c.TailJson("browser", enabled: true, lines <= 0 ? 200 : lines, level, sinceSeq, sinceBuildSeq: 0, grep, pageLoad)
            : JsonSerializer.Serialize(new { enabled = false, error = "ky-ai-browser capture not running" });

    [McpServerTool(Name = "console_clear"), Description(
        "Clear the browser-console buffer (e.g. to start a clean run before reproducing an issue).")]
    public static string ConsoleClear()
    {
        Capture.Collector?.Clear();
        return JsonSerializer.Serialize(new { ok = true, action = "console_clear" });
    }
}
