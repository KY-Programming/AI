namespace KY.AI.Browser;

// Process-wide handle to the live capture buffer, set once at startup. The MCP tools (BrowserTools)
// are static and read it directly — ky-ai-browser hosts the collector and its MCP surface in one
// process, so there's nothing to forward.
internal static class Capture
{
    public static ConsoleCollector? Collector;

    // The ng frontend's current build seq, refreshed by ky-ai-browser's heartbeat loop. The collector
    // tags each ingested console event with it (console↔build correlation; console_tail sinceBuildSeq).
    public static long BuildSeq;
}
