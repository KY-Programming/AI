using System.Text;

namespace KY.AI.Terminal;

// The bottom input-line editor (Claude-CLI style). Pure logic, no console I/O: a text buffer + a
// caret, basic line editing, and a stateful decoder for keystroke bytes (printable + the common
// control/escape sequences, which may split across reads). On Enter it yields the submitted line;
// on Ctrl-C it signals an interrupt. The caller renders from RenderWindow().
internal sealed class Composer
{
    private readonly StringBuilder _buf = new();
    private int _caret;

    private readonly List<string> _history = new();
    private int _histIdx;   // == _history.Count when not navigating

    // Escape-sequence decode state (an arrow key can arrive split across two reads).
    private enum E { Ground, Esc, Csi }
    private E _state = E.Ground;
    private readonly StringBuilder _seq = new();

    public string Text => _buf.ToString();
    public int Caret => _caret;
    public bool IsEmpty => _buf.Length == 0;

    public void Clear() { _buf.Clear(); _caret = 0; _state = E.Ground; _seq.Clear(); }

    // Decode a chunk of host keystrokes, applying edits. Calls submit(line) on Enter (the buffer is
    // cleared first), interrupt() on Ctrl-C, and backTab() on Shift+Tab (CSI Z — the Claude-Code
    // mode-cycle key). Returns true if the display should be redrawn.
    public bool Feed(ReadOnlySpan<byte> bytes, Action<string> submit, Action interrupt, Action backTab)
    {
        var changed = false;
        foreach (var b in bytes)
        {
            switch (_state)
            {
                case E.Esc:
                    if (b == (byte)'[') { _state = E.Csi; _seq.Clear(); }
                    else _state = E.Ground;            // ESC O…, plain ESC, etc. → ignore
                    break;
                case E.Csi:
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        if (b == (byte)'Z') { backTab(); changed = true; }   // Shift+Tab → cycle mode
                        else changed |= ApplyCsi((char)b, _seq.ToString());
                        _state = E.Ground;
                    }
                    else if (_seq.Length < 16) _seq.Append((char)b);
                    break;
                default:
                    changed |= Ground(b, submit, interrupt);
                    break;
            }
        }
        return changed;
    }

    private bool Ground(byte b, Action<string> submit, Action interrupt)
    {
        switch (b)
        {
            case 0x1B: _state = E.Esc; return false;          // ESC — start a sequence
            case 0x0D or 0x0A:                                // Enter → submit
                var line = _buf.ToString();
                _buf.Clear(); _caret = 0;
                if (line.Length > 0 && (_history.Count == 0 || _history[^1] != line)) _history.Add(line);
                _histIdx = _history.Count;
                submit(line);
                return true;
            case 0x7F or 0x08:                                // Backspace
                if (_caret > 0) { _buf.Remove(_caret - 1, 1); _caret--; }
                return true;
            case 0x03:                                        // Ctrl-C → interrupt the shell + clear
                _buf.Clear(); _caret = 0;
                interrupt();
                return true;
            case 0x15:                                        // Ctrl-U → kill to start of line
                if (_caret > 0) { _buf.Remove(0, _caret); _caret = 0; }
                return true;
            case 0x01: _caret = 0; return true;               // Ctrl-A → home
            case 0x05: _caret = _buf.Length; return true;     // Ctrl-E → end
            default:
                if (b >= 0x20) { _buf.Insert(_caret, (char)b); _caret++; return true; }
                return false;                                 // other control bytes ignored
        }
    }

    private bool ApplyCsi(char final, string body)
    {
        switch (final)
        {
            case 'A': return HistoryPrev();                                          // Up
            case 'B': return HistoryNext();                                          // Down
            case 'C': if (_caret < _buf.Length) { _caret++; return true; } break;   // Right
            case 'D': if (_caret > 0) { _caret--; return true; } break;             // Left
            case 'H': _caret = 0; return true;                                       // Home
            case 'F': _caret = _buf.Length; return true;                             // End
            case '~':
                if (body == "3" && _caret < _buf.Length) { _buf.Remove(_caret, 1); return true; }  // Delete
                if (body == "1") { _caret = 0; return true; }                                       // Home
                if (body == "4") { _caret = _buf.Length; return true; }                             // End
                break;
        }
        return false;
    }

    private bool HistoryPrev()
    {
        if (_history.Count == 0 || _histIdx == 0) return false;
        _histIdx--;
        SetBuffer(_history[_histIdx]);
        return true;
    }

    private bool HistoryNext()
    {
        if (_histIdx >= _history.Count) return false;
        _histIdx++;
        SetBuffer(_histIdx < _history.Count ? _history[_histIdx] : "");
        return true;
    }

    private void SetBuffer(string s) { _buf.Clear(); _buf.Append(s); _caret = _buf.Length; }

    // The visible window of the input line for a given width/prompt, scrolled horizontally so the
    // caret stays visible. Returns the display text (prompt + slice) and the caret's 0-based column.
    public (string Display, int CaretCol) RenderWindow(int width, string prompt)
    {
        var text = _buf.ToString();
        var avail = Math.Max(1, width - prompt.Length);
        var start = Math.Max(0, _caret - (avail - 1));
        if (start > text.Length) start = text.Length;
        var slice = text.Substring(start);
        if (slice.Length > avail) slice = slice.Substring(0, avail);
        return (prompt + slice, prompt.Length + (_caret - start));
    }
}
