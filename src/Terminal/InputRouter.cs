namespace KY.AI.Terminal;

// Routes host keystrokes (already win32-decoded to plain VT) in TUI/composer mode: it intercepts
// the multiplexer's direct chords and passes everything else to the composer.
//   Ctrl+E  → approve the staged proposal
//   Esc     → dismiss the staged proposal
//   (mode is cycled by Shift+Tab, handled inside the composer as CSI Z)
// Esc is held one byte to disambiguate a lone Esc (dismiss) from a CSI sequence (ESC [ … → composer).
internal sealed class InputRouter
{
    public delegate void SpanWriter(ReadOnlySpan<byte> bytes);

    private readonly SpanWriter _toComposer;
    private readonly Action _approve;
    private readonly Action _dismiss;
    private bool _pendingEsc;

    public InputRouter(SpanWriter toComposer, Action approve, Action dismiss)
    {
        _toComposer = toComposer;
        _approve = approve;
        _dismiss = dismiss;
    }

    public void Feed(ReadOnlySpan<byte> b)
    {
        var buf = new List<byte>(b.Length);
        void Flush() { if (buf.Count > 0) { _toComposer(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf)); buf.Clear(); } }

        foreach (var c in b)
        {
            if (_pendingEsc)
            {
                _pendingEsc = false;
                if (c == (byte)'[') { buf.Add(0x1B); buf.Add(c); continue; }   // CSI → composer
                Flush(); _dismiss();                                           // lone Esc → dismiss
                // fall through to handle c normally
            }

            switch (c)
            {
                case 0x05: Flush(); _approve(); break;   // Ctrl+E
                case 0x1B: _pendingEsc = true; break;    // hold to disambiguate Esc vs CSI
                default: buf.Add(c); break;
            }
        }

        if (_pendingEsc) { _pendingEsc = false; Flush(); _dismiss(); }   // chunk ended on a lone Esc
        else Flush();
    }
}
