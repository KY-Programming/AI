using System.Text;

namespace KY.AI.Serve;

// Thread-safe rolling line buffer that optionally mirrors the last N lines to a file.
// When the line count exceeds the capacity the oldest lines are dropped; a capacity of 0 means
// unlimited (no trimming). A file, when used, is kept open with shared read/write access so it
// can be tailed while running.
internal sealed class RollingLog : IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<string> _lines = new();
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

    public void Add(string line)
    {
        lock (_sync) { _lines.AddLast(line); Trim(); Flush(); }
    }

    public void SetCapacity(int capacity)
    {
        lock (_sync) { _capacity = Math.Max(0, capacity); Trim(); Flush(); }
    }

    public IReadOnlyList<string> Tail(int count)
    {
        lock (_sync)
        {
            if (count <= 0 || count >= _lines.Count) return _lines.ToList();
            return _lines.Skip(_lines.Count - count).ToList();
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
            foreach (var l in _lines) sb.Append(l).Append("\r\n");
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
