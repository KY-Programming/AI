namespace KY.AI.Browser;

// Process-wide handle to the live capture buffer, set once at startup of a capture INSTANCE. The
// instance's loopback control routes (/console/*, /eval) read it directly; the hub process (which
// hosts the MCP surface) never touches it — it forwards each call to the owning instance over HTTP.
internal static class Capture
{
    public static ConsoleCollector? Collector;

    // The per-tab return channel the runtime-inspection tools (evaluate_js / query_dom / reload_page)
    // push work through; each browser tab long-polls its own channel. The registry owns one channel per
    // tab plus agent ownership/handoff. Set once at startup alongside the collector.
    public static TabRegistry? Eval;

    // The ng frontend's current build seq, refreshed by ky-ai-browser's heartbeat loop. The collector
    // tags each ingested console event with it (console↔build correlation; console_tail sinceBuildSeq).
    public static long BuildSeq;
}
