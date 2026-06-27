using System.Text;

namespace KY.AI.Terminal;

// Windows Terminal, once ConPTY requests it (CSI ? 9001 h), reports each keypress as a
// "win32-input-mode" record — CSI Vk ; Sc ; Uc ; Kd ; Cs ; Rc _ — instead of a plain character.
// Our composer/router expect plain VT bytes, so this translates those records back into ordinary
// keystrokes (the Unicode char on key-down, special keys mapped to VT sequences) and passes every
// other byte / VT sequence through unchanged. It is a no-op when win32-input-mode is off.
internal sealed class Win32InputDecoder
{
    private enum S { Ground, Esc, Csi }
    private S _state = S.Ground;
    private readonly StringBuilder _params = new();   // bytes between '[' and the final byte

    private const int ShiftPressed = 0x0010;

    public byte[] Decode(ReadOnlySpan<byte> input)
    {
        var outBuf = new List<byte>(input.Length);
        foreach (var b in input)
        {
            switch (_state)
            {
                case S.Esc:
                    if (b == (byte)'[') { _state = S.Csi; _params.Clear(); }
                    else { outBuf.Add(0x1B); outBuf.Add(b); _state = S.Ground; }   // ESC O…, etc.
                    break;

                case S.Csi:
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        if (b == (byte)'_') EmitWin32(_params.ToString(), outBuf);   // win32 key event
                        else PassCsi((char)b, outBuf);                               // normal CSI → verbatim
                        _state = S.Ground;
                    }
                    else if (_params.Length < 64) _params.Append((char)b);
                    break;

                default:
                    if (b == 0x1B) _state = S.Esc;
                    else outBuf.Add(b);
                    break;
            }
        }
        return outBuf.ToArray();
    }

    private void PassCsi(char final, List<byte> outBuf)
    {
        outBuf.Add(0x1B);
        outBuf.Add((byte)'[');
        foreach (var c in _params.ToString()) outBuf.Add((byte)c);
        outBuf.Add((byte)final);
    }

    // body = Vk ; Sc ; Uc ; Kd ; Cs ; Rc
    private static void EmitWin32(string body, List<byte> outBuf)
    {
        var p = body.Split(';');
        int G(int i) => i < p.Length && int.TryParse(p[i], out var v) ? v : 0;
        var vk = G(0); var uc = G(2); var kd = G(3); var cs = G(4); var rc = G(5);

        if (kd == 0) return;                       // ignore key-up
        var repeat = Math.Max(1, rc);

        // Shift+Tab → CSI Z (the composer's mode-cycle key).
        if (vk == 0x09 && (cs & ShiftPressed) != 0) { for (var r = 0; r < repeat; r++) Esc("[Z", outBuf); return; }

        if (uc > 0)
        {
            // Real character (letters/digits, and control chars: Enter=13, BS=8, Tab=9, Esc=27,
            // Ctrl-combos like Ctrl-B=2). Emit as UTF-8.
            var bytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(uc));
            for (var r = 0; r < repeat; r++) outBuf.AddRange(bytes);
            return;
        }

        // Navigation / editing keys (Uc == 0) → VT sequences.
        var seq = vk switch
        {
            0x25 => "[D", 0x26 => "[A", 0x27 => "[C", 0x28 => "[B",   // Left/Up/Right/Down
            0x24 => "[H", 0x23 => "[F",                               // Home/End
            0x2D => "[2~", 0x2E => "[3~",                             // Insert/Delete
            0x21 => "[5~", 0x22 => "[6~",                             // PageUp/PageDown
            _ => null,
        };
        if (seq is not null) for (var r = 0; r < repeat; r++) Esc(seq, outBuf);
    }

    private static void Esc(string tail, List<byte> outBuf)
    {
        outBuf.Add(0x1B);
        foreach (var c in tail) outBuf.Add((byte)c);
    }
}
