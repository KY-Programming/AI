using System.Text;

namespace KY.AI.Serve;

// Thread-safe rolling line buffer that optionally mirrors the last N lines to a file.
// When the line count exceeds the capacity the oldest lines are dropped; a capacity of 0 means
// unlimited (no trimming). A file, when used, is kept open with shared read/write access so it
// can be tailed while running.
//
// Each line carries the build seq it belongs to and its matcher classification (Kind), so the
// same buffer can serve the raw human view, a noise-free "summary" (classified lines only), and
// since-rebuild / grep filters. Callers that don't track builds (e.g. the terminal audit log)
// use the bare Add(string)/Tail(int) overloads, which default the metadata.
internal sealed class RollingLog : IDisposable
{
    private readonly record struct Entry(long Seq, LineKind Kind, string Text);

    private readonly object _sync = new();
    private readonly LinkedList<Entry> _lines = new();
    private readonly FileStream? _fs;
    private int _capacity;

    // path == null/empty → in-memory buffer only (MCP serves the log; nothing on disk).
    // capacity 0 → unlimited (keep every line).
    public RollingLog(string? path, int capacity)
    {
        Path = string.IsNullOrEmpty(path) ? null : path;
        _capacity = Math.Max(0, capacity);
        _fs = Path is null
            ? null
            : new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    public string? Path { get; }
    public int Capacity { get { lock (_sync) return _capacity; } }
    public int Count { get { lock (_sync) return _lines.Count; } }

    // Untracked add (seq 0, unclassified) — for callers that don't observe builds.
    public void Add(string line) => Add(line, 0, LineKind.None);

    public void Add(string text, long seq, LineKind kind)
    {
        lock (_sync) { _lines.AddLast(new Entry(seq, kind, text)); Trim(); Flush(); }
    }

    public void SetCapacity(int capacity)
    {
        lock (_sync) { _capacity = Math.Max(0, capacity); Trim(); Flush(); }
    }

    // Plain trailing tail (0 = whole buffer).
    public IReadOnlyList<string> Tail(int count)
    {
        lock (_sync)
        {
            if (count <= 0 || count >= _lines.Count) return _lines.Select(e => e.Text).ToList();
            return _lines.Skip(_lines.Count - count).Select(e => e.Text).ToList();
        }
    }

    // Filtered tail: optionally only build-relevant (classified) lines, only lines from builds at
    // or after `sinceSeq`, and/or lines containing `grep` (case-insensitive). Filters are applied
    // first, then the trailing `count` is taken (0 = all matching).
    public IReadOnlyList<string> Tail(int count, bool summaryOnly, long sinceSeq = 0, string? grep = null)
    {
        lock (_sync)
        {
            IEnumerable<Entry> q = _lines;
            if (summaryOnly) q = q.Where(e => e.Kind != LineKind.None);
            if (sinceSeq > 0) q = q.Where(e => e.Seq >= sinceSeq);
            if (!string.IsNullOrEmpty(grep))
                q = q.Where(e => e.Text.Contains(grep, StringComparison.OrdinalIgnoreCase));

            var list = q.Select(e => e.Text).ToList();
            if (count > 0 && count < list.Count) list = list.Skip(list.Count - count).ToList();
            return list;
        }
    }

    private void Trim()
    {
        if (_capacity <= 0) return;  // unlimited
        while (_lines.Count > _capacity) _lines.RemoveFirst();
    }

    // Rewrites the whole file from the buffer (dev-server volume is low, so the cost
    // is negligible). Writes a UTF-8 BOM each time so Windows tools detect the encoding.
    private void Flush()
    {
        if (_fs is null) return;
        try
        {
            var sb = new StringBuilder(_lines.Count * 80);
            foreach (var e in _lines) sb.Append(e.Text).Append("\r\n");
            var bom = Encoding.UTF8.GetPreamble();
            var body = new UTF8Encoding(false).GetBytes(sb.ToString());
            _fs.SetLength(0);
            _fs.Position = 0;
            _fs.Write(bom, 0, bom.Length);
            _fs.Write(body, 0, body.Length);
            _fs.Flush();
        }
        catch { /* best-effort log mirror */ }
    }

    public void Dispose()
    {
        lock (_sync) { try { Flush(); _fs?.Dispose(); } catch { } }
    }
}
