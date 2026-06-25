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
        return stored;
    }

    public IReadOnlyList<ConsoleEvent> Tail(
        int count, string? minLevel = null, long sinceSeq = 0, long sinceBuildSeq = 0,
        string? grep = null, string? pageLoadId = null) =>
        _log.Tail(count, minLevel, sinceSeq, sinceBuildSeq, grep, pageLoadId);

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

    // JSON the control API returns for /console/tail (the hub forwards it verbatim). `dropped` is
    // surfaced inline so the agent sees a flood without a second call.
    public string TailJson(string name, bool enabled,
        int count, string? minLevel, long sinceSeq, long sinceBuildSeq, string? grep, string? pageLoadId)
    {
        var events = Tail(count, minLevel, sinceSeq, sinceBuildSeq, grep, pageLoadId);
        return JsonSerializer.Serialize(new
        {
            name,
            enabled,
            returned = events.Count,
            total = Count,
            dropped = Dropped,
            events,
        }, Json);
    }

    public string ClearJson(string name)
    {
        Clear();
        return JsonSerializer.Serialize(new { name, action = "console_clear", ok = true }, Json);
    }
}
