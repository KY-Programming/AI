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
    public IReadOnlyList<ConsoleEvent> Tail(
        int count, string? minLevel = null, long sinceSeq = 0, long sinceBuildSeq = 0,
        string? grep = null, string? pageLoadId = null)
    {
        lock (_sync)
        {
            IEnumerable<ConsoleEvent> q = _events;

            var filterRank = ConsoleLevels.FilterRank(minLevel);
            if (filterRank > 0) q = q.Where(e => ConsoleLevels.RankOf(e.Level) >= filterRank);
            if (sinceSeq > 0) q = q.Where(e => e.Seq >= sinceSeq);
            if (sinceBuildSeq > 0) q = q.Where(e => e.BuildSeq >= sinceBuildSeq);
            if (!string.IsNullOrEmpty(pageLoadId)) q = q.Where(e => e.PageLoadId == pageLoadId);
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
