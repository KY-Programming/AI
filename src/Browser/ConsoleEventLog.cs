namespace KY.AI.Browser;

// Thread-safe, capacity-trimmed ring of browser console events. Structurally mirrors Serve's
// RollingLog (lock + LinkedList + capacity trim + per-entry seq) but stores structured
// ConsoleEvents instead of text lines and is in-memory only — console events are read over MCP,
// never mirrored to a file.
//
// When the buffer overflows capacity the oldest events are dropped; `Dropped` counts every event
// ever dropped (trim + per-batch overflow) so the agent can tell when a console flood lost data.
internal sealed class ConsoleEventLog
{
    private readonly object _sync = new();
    private readonly LinkedList<ConsoleEvent> _events = new();
    private int _capacity;
    private long _nextSeq = 1;
    private long _dropped;

    // capacity 0 → unlimited (keep every event).
    public ConsoleEventLog(int capacity) => _capacity = Math.Max(0, capacity);

    public int Count { get { lock (_sync) return _events.Count; } }
    public long Dropped { get { lock (_sync) return _dropped; } }

    // Assign the next seq and append. Returns the assigned seq. Trimming an old event to stay
    // within capacity counts as a drop.
    public long Append(Func<long, ConsoleEvent> build)
    {
        lock (_sync)
        {
            var seq = _nextSeq++;
            _events.AddLast(build(seq));
            Trim();
            return seq;
        }
    }

    // Record events that were dropped before ever entering the buffer (per-batch cap, client-side
    // overflow reported by the snippet, rejected batch, …).
    public void NoteDropped(long count)
    {
        if (count <= 0) return;
        lock (_sync) _dropped += count;
    }

    public void Clear()
    {
        lock (_sync) { _events.Clear(); /* keep _dropped + _nextSeq monotonic across a clear */ }
    }

    // Drop oldest events past capacity (counts as dropped). Caller holds _sync.
    private void Trim()
    {
        if (_capacity <= 0) return;  // unlimited
        while (_events.Count > _capacity) { _events.RemoveFirst(); _dropped++; }
    }

    // Filters compose, then the trailing `count` is taken (0 = all matching):
    //   minLevel     — keep events at or above this severity (debug<log<info<warn<error<exception)
    //   sinceSeq     — keep events with Seq >= sinceSeq (pass back a prior tail's max seq to page)
    //   sinceBuildSeq— keep events tagged with BuildSeq >= this (a wait_for_build verdict's seq)
    //   grep         — case-insensitive substring over Text + Stack
    //   pageLoadId   — keep only events from this page load (segment one reload/HMR boundary)
    //   dropTransportNoise — drop SignalR/WebSocket negotiation + [vite] HMR socket churn (see TransportNoise)
    //   dropFrameworkNoise — drop known-benign framework boilerplate (Inferno/Angular banners, …; see FrameworkNoise)
    public IReadOnlyList<ConsoleEvent> Tail(
        int count, string? minLevel = null, long sinceSeq = 0, long sinceBuildSeq = 0,
        string? grep = null, string? pageLoadId = null, bool dropTransportNoise = false,
        bool dropFrameworkNoise = false)
    {
        lock (_sync)
        {
            IEnumerable<ConsoleEvent> q = _events;

            var filterRank = ConsoleLevels.FilterRank(minLevel);
            if (filterRank > 0) q = q.Where(e => ConsoleLevels.RankOf(e.Level) >= filterRank);
            if (sinceSeq > 0) q = q.Where(e => e.Seq >= sinceSeq);
            if (sinceBuildSeq > 0) q = q.Where(e => e.BuildSeq >= sinceBuildSeq);
            if (!string.IsNullOrEmpty(pageLoadId)) q = q.Where(e => e.PageLoadId == pageLoadId);
            if (dropTransportNoise) q = q.Where(e => !TransportNoise.IsNoise(e));
            if (dropFrameworkNoise) q = q.Where(e => !FrameworkNoise.IsNoise(e));
            if (!string.IsNullOrEmpty(grep))
                q = q.Where(e =>
                    (e.Text?.Contains(grep, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.Stack?.Contains(grep, StringComparison.OrdinalIgnoreCase) ?? false));

            var list = q.ToList();
            if (count > 0 && count < list.Count) list = list.Skip(list.Count - count).ToList();
            return list;
        }
    }
}

// Recognizes the dev-time transport churn that dominates a console buffer but says nothing about the
// app: SignalR / WebSocket negotiation + close codes, and the [vite] HMR socket chatter. console_tail
// appOnly=true drops these so app-level logs and errors stand out. Deliberately narrow — it matches
// transport plumbing, not application code that merely mentions a socket.
internal static class TransportNoise
{
    private static readonly string[] Needles =
    {
        "signalr",
        "/negotiate",
        "websocket connection to",                                  // Chrome's "WebSocket connection to '…' failed"
        "websocket is closed before the connection is established",
        "websocket closed with status",
        "failed to start the transport",
        "failed to complete negotiation",
        "error: failed to start the connection",
        "[vite]",                                                   // [vite] connecting…/connected./server connection lost
        "ws-proxy",
    };

    public static bool IsNoise(ConsoleEvent e) => Match(e.Text) || Match(e.Stack);

    private static bool Match(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var n in Needles)
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

// Recognizes known-benign FRAMEWORK boilerplate that clutters the error/warn channel but says nothing
// about the app's own health: library dev-mode banners and a dev-only router hiccup. console_tail
// dropFrameworkNoise=true drops these. This is SEPARATE from appOnly (transport churn) — an agent
// composes a fully clean channel by setting both. Deliberately narrow and curated: each needle is a
// distinctive substring of a specific benign message, not a generic word that could hide a real bug.
internal static class FrameworkNoise
{
    private static readonly string[] Needles =
    {
        "production build of inferno",                  // DevExtreme/Inferno "you are using a production build of Inferno…" banner
        "transition was aborted",                       // dev-only router InvalidStateError during rapid navigation
        "angular is running in development mode",        // Angular dev-mode banner
    };

    public static bool IsNoise(ConsoleEvent e) => Match(e.Text) || Match(e.Stack);

    private static bool Match(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var n in Needles)
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
