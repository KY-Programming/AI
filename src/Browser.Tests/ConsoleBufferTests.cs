using System.Linq;
using System.Text.Json;
using KY.AI.Browser;
using Xunit;

namespace KY.AI.Browser.Tests;

// The pure, server-side core of browser-console capture that ky-ai-browser hosts: the structured
// ring (seq/filters/trim/dropped) and the collector (stamping, caps, token guard, clear). The
// cross-origin ingest + the ng-side reversible inject are validated by a live smoke.
public class ConsoleBufferTests
{
    private static long Add(ConsoleEventLog log, string level, string text, long buildSeq = 0, string page = "p1", string? stack = null)
        => log.Append(seq => new ConsoleEvent(seq, level, new[] { text }, text, null, null, null, stack, "t", buildSeq, page, "r"));

    // ── ConsoleEventLog ──

    [Fact]
    public void EventLog_assigns_monotonic_seq()
    {
        var log = new ConsoleEventLog(0);
        Assert.Equal(1, Add(log, "log", "a"));
        Assert.Equal(2, Add(log, "log", "b"));
        Assert.Equal(3, Add(log, "log", "c"));
    }

    [Fact]
    public void EventLog_minLevel_keeps_that_severity_and_above()
    {
        var log = new ConsoleEventLog(0);
        Add(log, "debug", "d"); Add(log, "log", "l"); Add(log, "warn", "w"); Add(log, "error", "e");

        Assert.Equal(new[] { "w", "e" }, log.Tail(0, minLevel: "warn").Select(x => x.Text));
        Assert.Equal(new[] { "e" }, log.Tail(0, minLevel: "error").Select(x => x.Text));
        // An unrecognized threshold disables the filter (everything passes).
        Assert.Equal(4, log.Tail(0, minLevel: "nonsense").Count);
    }

    [Fact]
    public void EventLog_sinceSeq_and_sinceBuildSeq_and_pageLoad_filter()
    {
        var log = new ConsoleEventLog(0);
        Add(log, "log", "a", buildSeq: 1, page: "p1");
        Add(log, "log", "b", buildSeq: 1, page: "p1");
        Add(log, "log", "c", buildSeq: 2, page: "p2");

        Assert.Equal(new[] { "b", "c" }, log.Tail(0, sinceSeq: 2).Select(x => x.Text));
        Assert.Equal(new[] { "c" }, log.Tail(0, sinceBuildSeq: 2).Select(x => x.Text));
        Assert.Equal(new[] { "c" }, log.Tail(0, pageLoadId: "p2").Select(x => x.Text));
    }

    [Fact]
    public void EventLog_grep_matches_text_and_stack_case_insensitively()
    {
        var log = new ConsoleEventLog(0);
        Add(log, "log", "hello world");
        Add(log, "log", "nothing", stack: "at FooBar (app.ts:1:1)");

        Assert.Equal(new[] { "hello world" }, log.Tail(0, grep: "WORLD").Select(x => x.Text));
        Assert.Equal(new[] { "nothing" }, log.Tail(0, grep: "foobar").Select(x => x.Text)); // stack hit
    }

    [Fact]
    public void EventLog_trims_oldest_past_capacity_and_counts_drops()
    {
        var log = new ConsoleEventLog(3);
        for (var i = 1; i <= 5; i++) Add(log, "log", "e" + i);

        Assert.Equal(3, log.Count);
        Assert.Equal(2, log.Dropped);
        Assert.Equal(new[] { "e3", "e4", "e5" }, log.Tail(0).Select(x => x.Text)); // last 3 survive
    }

    [Fact]
    public void EventLog_count_takes_trailing_slice()
    {
        var log = new ConsoleEventLog(0);
        for (var i = 1; i <= 5; i++) Add(log, "log", "e" + i);
        Assert.Equal(new[] { "e4", "e5" }, log.Tail(2).Select(x => x.Text));
    }

    [Fact]
    public void EventLog_dropTransportNoise_drops_signalr_and_vite_churn_keeps_app_logs()
    {
        var log = new ConsoleEventLog(0);
        Add(log, "error", "Error: Failed to complete negotiation with the server");
        Add(log, "info", "[vite] connected.");
        Add(log, "warn", "WebSocket connection to 'wss://localhost/hub' failed");
        Add(log, "log", "wire energized");                                  // genuine app log

        Assert.Equal(new[] { "wire energized" }, log.Tail(0, dropTransportNoise: true).Select(x => x.Text));
        Assert.Equal(4, log.Tail(0).Count);                                  // off by default — nothing dropped
    }

    // ── compact projection (ConsoleCollector.TailJson compact:true) ──

    [Fact]
    public void TailJson_compact_drops_args_when_text_present_and_truncates_stack()
    {
        var c = new ConsoleCollector(100, () => 0);
        var stack = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"  at f{i} (app.ts:{i}:1)"));
        c.Ingest(new ConsoleIngestBatch(c.Token, "p", new[]
        {
            new RawConsoleEvent("error", new[] { "boom", "extra" }, "boom extra", "app.ts", 1, 1, stack, null),
        }, null));

        var json = c.TailJson("browser", true, 0, null, 0, 0, null, null, compact: true, appOnly: false);
        using var doc = JsonDocument.Parse(json);
        var ev = doc.RootElement.GetProperty("events")[0];

        Assert.False(ev.TryGetProperty("args", out _));            // args dropped — text carries them
        Assert.Equal("boom extra", ev.GetProperty("text").GetString());
        Assert.Contains("+6 frames", ev.GetProperty("stack").GetString());   // 12 frames → keep 6, note the rest
        Assert.False(ev.TryGetProperty("receivedAt", out _));      // omitted in compact
    }

    // ── ConsoleCollector ──

    private static ConsoleIngestBatch Batch(string token, string page, long? droppedClient, params RawConsoleEvent[] events)
        => new(token, page, events, droppedClient);

    private static RawConsoleEvent Raw(string level, params string[] args)
        => new(level, args, null, null, null, null, null, null);

    [Fact]
    public void Collector_ingest_stamps_seq_tags_buildseq_and_pageload()
    {
        long buildSeq = 7;
        var c = new ConsoleCollector(100, () => buildSeq);
        var stored = c.Ingest(Batch(c.Token, "page-A", null, Raw("ERROR", "boom"), Raw("log", "x", "y")));

        Assert.Equal(2, stored);
        var events = c.Tail(0);
        Assert.Equal(new[] { 1L, 2L }, events.Select(e => e.Seq));
        Assert.All(events, e => Assert.Equal(7, e.BuildSeq));
        Assert.All(events, e => Assert.Equal("page-A", e.PageLoadId));
        Assert.Equal("error", events[0].Level);           // normalized to lower-case
        Assert.Equal("x y", events[1].Text);              // text falls back to joined args
    }

    [Fact]
    public void Collector_rejects_token_mismatch()
    {
        var c = new ConsoleCollector(100, () => 0);
        Assert.Equal(0, c.Ingest(Batch("not-the-token", "p", null, Raw("log", "a"))));
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void Collector_caps_per_batch_and_counts_overflow()
    {
        var c = new ConsoleCollector(10_000, () => 0);
        var many = Enumerable.Range(0, 600).Select(i => Raw("log", "e" + i)).ToArray();
        var stored = c.Ingest(Batch(c.Token, "p", null, many));

        Assert.Equal(500, stored);
        Assert.Equal(500, c.Count);
        Assert.Equal(100, c.Dropped);
    }

    [Fact]
    public void Collector_counts_client_reported_drops()
    {
        var c = new ConsoleCollector(100, () => 0);
        c.Ingest(Batch(c.Token, "p", droppedClient: 7, Raw("log", "a")));
        Assert.Equal(1, c.Count);
        Assert.Equal(7, c.Dropped);
    }

    [Fact]
    public void Collector_buildseq_segments_across_a_rebuild()
    {
        long buildSeq = 1;
        var c = new ConsoleCollector(100, () => buildSeq);
        c.Ingest(Batch(c.Token, "p", null, Raw("log", "before")));
        buildSeq = 2; // a rebuild happened
        c.Ingest(Batch(c.Token, "p", null, Raw("log", "after")));

        Assert.Equal(new[] { "after" }, c.Tail(0, sinceBuildSeq: 2).Select(e => e.Text));
    }

    [Fact]
    public void Collector_clear_empties_but_keeps_seq_monotonic()
    {
        var c = new ConsoleCollector(100, () => 0);
        c.Ingest(Batch(c.Token, "p", null, Raw("log", "a"), Raw("log", "b")));
        c.Clear();
        Assert.Equal(0, c.Count);

        c.Ingest(Batch(c.Token, "p", null, Raw("log", "c")));
        Assert.True(c.Tail(0).Single().Seq > 2); // seq does not reset across a clear
    }
}
