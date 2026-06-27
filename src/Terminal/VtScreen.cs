using KY.AI.Serve;

namespace KY.AI.Terminal;

// A small terminal screen model that consumes the shell's raw VT byte stream and maintains a
// character grid + cursor (the visible screen, for TUIs) plus a linear scrollback of lines that
// have scrolled off the top (the transcript, for ordinary command output). It is deliberately
// minimal — enough VT coverage for line-oriented shells and basic full-screen redraws — and sits
// behind this API as an adapter seam, so a fuller VT engine (e.g. VtNetCore) could replace the
// internals without touching the session or the MCP tools.
//
// Bytes are treated as Latin-1 (one byte = one cell); ASCII is exact, other encodings are
// approximated. Thread-safe: Feed runs on the output pump while the REST/MCP layer reads.
internal sealed class VtScreen
{
    private readonly object _sync = new();
    private readonly RollingLog _scrollback;

    private char[][] _grid = Array.Empty<char[]>();
    private int _rows;
    private int _cols;
    private int _cr;   // cursor row (0-based)
    private int _cc;   // cursor col (0-based)
    private int _savedRow;
    private int _savedCol;
    private bool _altScreen;   // DEC ?1049/?47/?1047 — a full-screen app owns the screen

    // Parser state, persisted across Feed calls (chunks split sequences arbitrarily).
    private enum P { Ground, Esc, Csi, Osc, OscEsc, EscInter }
    private P _state = P.Ground;
    private readonly System.Text.StringBuilder _params = new();

    public VtScreen(int cols, int rows, int scrollbackLines)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _scrollback = new RollingLog(null, Math.Max(100, scrollbackLines));
        AllocGrid();
    }

    private void AllocGrid()
    {
        _grid = new char[_rows][];
        for (var r = 0; r < _rows; r++) { _grid[r] = new char[_cols]; Array.Fill(_grid[r], ' '); }
        _cr = _cc = 0;
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            foreach (var b in bytes) Step(b);
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_sync)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == _cols && rows == _rows) return;

            var old = _grid;
            var oldRows = _rows;
            var oldCols = _cols;
            _cols = cols; _rows = rows;
            _grid = new char[_rows][];
            for (var r = 0; r < _rows; r++)
            {
                _grid[r] = new char[_cols];
                Array.Fill(_grid[r], ' ');
                if (r < oldRows)
                    Array.Copy(old[r], _grid[r], Math.Min(oldCols, _cols));
            }
            _cr = Math.Clamp(_cr, 0, _rows - 1);
            _cc = Math.Clamp(_cc, 0, _cols - 1);
        }
    }

    public (int Row, int Col) Cursor { get { lock (_sync) return (_cr, _cc); } }
    public (int Cols, int Rows) Size { get { lock (_sync) return (_cols, _rows); } }

    // True while a full-screen app (vim/htop/less) is on the alternate screen buffer.
    public bool AltScreenActive { get { lock (_sync) return _altScreen; } }

    // Visible grid as lines, trailing spaces and trailing blank lines trimmed.
    public IReadOnlyList<string> ScreenLines()
    {
        lock (_sync)
        {
            var lines = new List<string>(_rows);
            foreach (var row in _grid) lines.Add(new string(row).TrimEnd(' '));
            var last = lines.Count - 1;
            while (last >= 0 && lines[last].Length == 0) last--;
            return lines.GetRange(0, last + 1);
        }
    }

    public IReadOnlyList<string> ScrollbackTail(int count) => _scrollback.Tail(count);

    // The text on the cursor's row up to the cursor (i.e. the current prompt), trailing spaces
    // trimmed. Used to detect password prompts robustly (the grid already resolved CR/CUP/erases).
    public string CursorLineText()
    {
        lock (_sync)
        {
            if (_cr < 0 || _cr >= _rows) return "";
            var row = _grid[_cr];
            var end = Math.Clamp(_cc, 0, row.Length);
            return new string(row, 0, end).TrimEnd(' ');
        }
    }

    // ── parser ───────────────────────────────────────────────────────────────
    private void Step(byte b)
    {
        switch (_state)
        {
            case P.Ground: Ground(b); break;
            case P.Esc: Esc(b); break;
            case P.Csi: Csi(b); break;
            case P.Osc: Osc(b); break;
            case P.OscEsc: OscEsc(b); break;
            case P.EscInter: _state = P.Ground; break; // consume the charset final byte
        }
    }

    private void Ground(byte b)
    {
        switch (b)
        {
            case 0x1B: _state = P.Esc; break;
            case 0x0D: _cc = 0; break;                                  // CR
            case 0x0A: LineFeed(); break;                              // LF
            case 0x08: _cc = Math.Max(0, _cc - 1); break;             // BS
            case 0x09: _cc = Math.Min(_cols - 1, (_cc / 8 + 1) * 8); break; // TAB
            case 0x07: break;                                          // BEL
            default:
                if (b >= 0x20) Put((char)b);
                break;
        }
    }

    private void Esc(byte b)
    {
        switch ((char)b)
        {
            case '[': _params.Clear(); _state = P.Csi; break;
            case ']': _params.Clear(); _state = P.Osc; break;
            case '7': _savedRow = _cr; _savedCol = _cc; _state = P.Ground; break;
            case '8': _cr = _savedRow; _cc = _savedCol; _state = P.Ground; break;
            case 'M': ReverseIndex(); _state = P.Ground; break;
            case '(' or ')' or '*' or '+': _state = P.EscInter; break; // charset designate
            default: _state = P.Ground; break;                          // =, >, c, etc. — ignore
        }
    }

    private void Csi(byte b)
    {
        // Parameter / intermediate bytes accumulate until a final byte (0x40–0x7E).
        if (b >= 0x40 && b <= 0x7E) { Dispatch((char)b, _params.ToString()); _state = P.Ground; return; }
        if (_params.Length < 64) _params.Append((char)b);
    }

    private void Osc(byte b)
    {
        if (b == 0x07) { _state = P.Ground; return; }       // BEL terminator
        if (b == 0x1B) { _state = P.OscEsc; return; }       // maybe ST (ESC \)
        if (_params.Length < 256) _params.Append((char)b);
    }

    private void OscEsc(byte b)
    {
        _state = P.Ground; // ESC \ ends the OSC; anything else: bail out of the OSC too
    }

    private void Dispatch(char final, string raw)
    {
        var priv = raw.StartsWith('?');
        var body = priv ? raw[1..] : raw;
        var ps = body.Split(';');
        int P0(int def) => ps.Length > 0 && int.TryParse(ps[0], out var v) && v > 0 ? v : def;
        int Pn(int idx, int def) => ps.Length > idx && int.TryParse(ps[idx], out var v) && v > 0 ? v : def;

        switch (final)
        {
            case 'A': _cr = Math.Max(0, _cr - P0(1)); break;
            case 'B': _cr = Math.Min(_rows - 1, _cr + P0(1)); break;
            case 'C': _cc = Math.Min(_cols - 1, _cc + P0(1)); break;
            case 'D': _cc = Math.Max(0, _cc - P0(1)); break;
            case 'E': _cr = Math.Min(_rows - 1, _cr + P0(1)); _cc = 0; break;
            case 'F': _cr = Math.Max(0, _cr - P0(1)); _cc = 0; break;
            case 'G': _cc = Math.Clamp(P0(1) - 1, 0, _cols - 1); break;
            case 'd': _cr = Math.Clamp(P0(1) - 1, 0, _rows - 1); break;
            case 'H' or 'f':
                _cr = Math.Clamp((ps.Length > 0 ? P0(1) : 1) - 1, 0, _rows - 1);
                _cc = Math.Clamp(Pn(1, 1) - 1, 0, _cols - 1);
                break;
            case 'J': EraseDisplay(ps.Length > 0 && int.TryParse(ps[0], out var j) ? j : 0); break;
            case 'K': EraseLine(ps.Length > 0 && int.TryParse(ps[0], out var k) ? k : 0); break;
            case 's': _savedRow = _cr; _savedCol = _cc; break;
            case 'u': _cr = _savedRow; _cc = _savedCol; break;
            case 'h': if (priv) SetPrivateMode(body, true); break;   // DECSET (alt-screen tracking)
            case 'l': if (priv) SetPrivateMode(body, false); break;  // DECRST
            // 'm' (SGR colors), 'r' (scroll region): ignored for text extraction.
            default: break;
        }
    }

    private void SetPrivateMode(string body, bool on)
    {
        foreach (var part in body.Split(';'))
            if (part is "1049" or "47" or "1047") _altScreen = on;
    }

    // ── grid operations ────────────────────────────────────────────────────────
    private void Put(char ch)
    {
        if (_cc >= _cols) { _cc = 0; LineFeed(); }
        _grid[_cr][_cc] = ch;
        _cc++;
    }

    private void LineFeed()
    {
        if (_cr >= _rows - 1) ScrollUp();
        else _cr++;
    }

    private void ReverseIndex()
    {
        if (_cr == 0)
        {
            // scroll down: drop bottom line, blank the top
            for (var r = _rows - 1; r > 0; r--) _grid[r] = _grid[r - 1];
            _grid[0] = new char[_cols];
            Array.Fill(_grid[0], ' ');
        }
        else _cr--;
    }

    private void ScrollUp()
    {
        var top = _grid[0];
        _scrollback.Add(new string(top).TrimEnd(' '));
        for (var r = 1; r < _rows; r++) _grid[r - 1] = _grid[r];
        Array.Fill(top, ' ');
        _grid[_rows - 1] = top;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // cursor → end
                for (var c = _cc; c < _cols; c++) _grid[_cr][c] = ' ';
                for (var r = _cr + 1; r < _rows; r++) Array.Fill(_grid[r], ' ');
                break;
            case 1: // start → cursor
                for (var r = 0; r < _cr; r++) Array.Fill(_grid[r], ' ');
                for (var c = 0; c <= _cc && c < _cols; c++) _grid[_cr][c] = ' ';
                break;
            default: // 2 / 3: whole screen
                foreach (var row in _grid) Array.Fill(row, ' ');
                break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: for (var c = _cc; c < _cols; c++) _grid[_cr][c] = ' '; break;
            case 1: for (var c = 0; c <= _cc && c < _cols; c++) _grid[_cr][c] = ' '; break;
            default: Array.Fill(_grid[_cr], ' '); break;
        }
    }
}
