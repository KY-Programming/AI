using System.Text.Json;

namespace KY.AI.Browser;

// Owns the browser-console buffer for one supervised app and turns raw posted events into
// enriched ConsoleEvents. The collector — not the browser snippet — does build correlation,
// timestamping, size clamping and drop accounting, so the client side stays dumb.
//
// Lifetime matches the supervisor's DevServer (which outlives child restarts), so the buffer is
// preserved across `restart`/hot reloads — the default is "segment, don't clear": each new page
// load gets a fresh PageLoadId the agent can filter on, rather than the buffer being wiped.
public sealed class ConsoleCollector
{
    // Matches Serve's BuildTracker.IsoFormat (offset-aware) so console + build timestamps read the
    // same; kept local so this library has no dependency back into Serve.
    private const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Compact mode: same camelCase, but null fields are omitted so a slim event has no empty keys.
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private const int CompactStackFrames = 6;   // frames kept per stack in compact mode

    // Defensive server-side caps (the snippet caps too; this is belt-and-braces against a flood or
    // a hand-crafted post). A console.log in a render loop must never OOM the supervisor.
    private const int MaxEventsPerBatch = 500;
    private const int MaxArgs = 50;
    private const int MaxArgLen = 8_192;
    private const int MaxTextLen = 16_384;
    private const int MaxStackLen = 32_768;
    private const int MaxFieldLen = 2_048;   // source / pageLoadId / timestamp / level

    private readonly ConsoleEventLog _log;
    private readonly Func<long> _buildSeq;

    // buildSeq supplies the current build cycle at ingest time (the supervisor passes
    // () => tracker.CurrentBuildSeq); this is the only thing the collector needs from build tracking.
    public ConsoleCollector(int capacity, Func<long> buildSeq)
    {
        _log = new ConsoleEventLog(capacity);
        _buildSeq = buildSeq;
        // A short random tag baked into the injected snippet and checked on ingest. NOT a security
        // control (client-readable JS can't keep a secret) — just a guard so a stray post from an
        // unrelated local tab can't pollute this app's buffer. The real boundary is the loopback bind.
        Token = Guid.NewGuid().ToString("N")[..16];
    }

    public string Token { get; }
    public int Count => _log.Count;
    public long Dropped => _log.Dropped;

    // The page-load id of the most recently ingested batch — i.e. the live page. Reliable because the
    // snippet posts an "[kyai] console capture attached" event on every page load, so the newest event
    // is always from the current page. Null until the first event. Powers console_tail currentPageOnly
    // (scope to the page you're looking at now) and is surfaced in every TailJson response.
    public string? CurrentPageLoadId { get; private set; }

    // Ingest a posted batch. Returns the number of events stored. A token mismatch rejects the
    // whole batch (foreign data, not ours — not counted as a drop). Overflow past the per-batch
    // cap is dropped and counted.
    public int Ingest(ConsoleIngestBatch batch)
    {
        if (!string.Equals(batch.Token, Token, StringComparison.Ordinal)) return 0;

        if (batch.DroppedClient is > 0) _log.NoteDropped(batch.DroppedClient.Value);

        var events = batch.Events;
        if (events is null || events.Length == 0) return 0;

        var pageLoadId = Clamp(batch.PageLoadId, MaxFieldLen) ?? "unknown";
        var receivedAt = DateTimeOffset.Now.ToString(IsoFormat);
        var buildSeq = _buildSeq();

        var take = events.Length;
        if (take > MaxEventsPerBatch)
        {
            _log.NoteDropped(take - MaxEventsPerBatch);
            take = MaxEventsPerBatch;
        }

        var stored = 0;
        for (var i = 0; i < take; i++)
        {
            var raw = events[i];
            if (raw is null) continue;
            _log.Append(seq => Enrich(seq, raw, pageLoadId, buildSeq, receivedAt));
            stored++;
        }
        if (stored > 0) CurrentPageLoadId = pageLoadId;
        return stored;
    }

    public IReadOnlyList<ConsoleEvent> Tail(
        int count, string? minLevel = null, long sinceSeq = 0, long sinceBuildSeq = 0,
        string? grep = null, string? pageLoadId = null, bool dropTransportNoise = false,
        bool dropFrameworkNoise = false) =>
        _log.Tail(count, minLevel, sinceSeq, sinceBuildSeq, grep, pageLoadId, dropTransportNoise, dropFrameworkNoise);

    public void Clear() => _log.Clear();

    private static ConsoleEvent Enrich(long seq, RawConsoleEvent raw, string pageLoadId, long buildSeq, string receivedAt)
    {
        var level = (Clamp(raw.Level, MaxFieldLen) ?? "log").ToLowerInvariant();

        IReadOnlyList<string> args = raw.Args is null
            ? Array.Empty<string>()
            : raw.Args.Take(MaxArgs).Select(a => Clamp(a, MaxArgLen) ?? "").ToList();

        var text = Clamp(raw.Text, MaxTextLen);
        if (string.IsNullOrEmpty(text) && args.Count > 0) text = string.Join(' ', args);

        var timestamp = Clamp(raw.Timestamp, MaxFieldLen);
        if (string.IsNullOrEmpty(timestamp)) timestamp = receivedAt;

        return new ConsoleEvent(
            seq,
            level,
            args,
            text,
            Clamp(raw.Source, MaxFieldLen),
            raw.Line,
            raw.Col,
            Clamp(raw.Stack, MaxStackLen),
            timestamp!,
            buildSeq,
            pageLoadId,
            receivedAt);
    }

    // Truncate over-long strings with a marker so a flooded field can't blow the buffer; null/empty
    // pass through unchanged.
    private static string? Clamp(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..max] + "…[truncated]";
    }

    // JSON the MCP console_tail tool returns. `dropped` is surfaced inline so the agent sees a flood
    // without a second call, and `currentPageLoadId` so it can tell which page load is live without a
    // second call. compact slims each event (drops args when text carries them, truncates stacks, omits
    // null fields); appOnly drops dev-transport churn (SignalR/WebSocket/[vite]); frameworkNoise drops
    // known-benign framework banners; currentPageOnly scopes to the live page load (the one-call "did my
    // reload clear it?" check) unless an explicit pageLoadId was given (that wins).
    public string TailJson(string name, bool enabled,
        int count, string? minLevel, long sinceSeq, long sinceBuildSeq, string? grep, string? pageLoadId,
        bool compact = false, bool appOnly = false, bool frameworkNoise = false, bool currentPageOnly = false)
    {
        if (string.IsNullOrEmpty(pageLoadId) && currentPageOnly) pageLoadId = CurrentPageLoadId;
        var events = Tail(count, minLevel, sinceSeq, sinceBuildSeq, grep, pageLoadId, appOnly, frameworkNoise);
        if (compact)
            return JsonSerializer.Serialize(new
            {
                name,
                enabled,
                compact = true,
                returned = events.Count,
                total = Count,
                dropped = Dropped,
                currentPageLoadId = CurrentPageLoadId,
                events = events.Select(ToCompact),
            }, CompactJson);

        return JsonSerializer.Serialize(new
        {
            name,
            enabled,
            returned = events.Count,
            total = Count,
            dropped = Dropped,
            currentPageLoadId = CurrentPageLoadId,
            events,
        }, Json);
    }

    // Slim view of one event: keep args only when there is no `text` to carry them, clip the stack to
    // the top frames, and let CompactJson drop every null field.
    private static object ToCompact(ConsoleEvent e) => new
    {
        e.Seq,
        e.Level,
        args = string.IsNullOrEmpty(e.Text) && e.Args.Count > 0 ? e.Args : null,
        e.Text,
        e.Source,
        e.Line,
        e.Col,
        stack = TruncateStack(e.Stack, CompactStackFrames),
        e.Timestamp,
        e.BuildSeq,
        e.PageLoadId,
    };

    // Keep the top `frames` lines of a stack, noting how many were dropped. Null/short stacks pass through.
    private static string? TruncateStack(string? stack, int frames)
    {
        if (string.IsNullOrEmpty(stack)) return stack;
        var lines = stack.Split('\n');
        if (lines.Length <= frames) return stack;
        return string.Join('\n', lines.Take(frames)) + $"\n…(+{lines.Length - frames} frames)";
    }

    public string ClearJson(string name)
    {
        Clear();
        return JsonSerializer.Serialize(new { name, action = "console_clear", ok = true }, Json);
    }
}
