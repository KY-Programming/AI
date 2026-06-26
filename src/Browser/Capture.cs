namespace KY.AI.Browser;

// Process-wide handle to the live capture buffer, set once at startup. The MCP tools (BrowserTools)
// are static and read it directly — ky-ai-browser hosts the collector and its MCP surface in one
// process, so there's nothing to forward.
internal static class Capture
{
    public static ConsoleCollector? Collector;

    // The return channel the runtime-inspection tools (evaluate_js / query_dom / reload_page) push
    // work through; the capture snippet long-polls it. Set once at startup alongside the collector.
    public static EvalChannel? Eval;

    // The ng frontend's current build seq, refreshed by ky-ai-browser's heartbeat loop. The collector
    // tags each ingested console event with it (console↔build correlation; console_tail sinceBuildSeq).
    public static long BuildSeq;
}
