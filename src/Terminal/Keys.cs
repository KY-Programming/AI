using System.Text;

namespace KY.AI.Terminal;

// Translates agent-friendly key names into the VT byte sequences a shell expects. Accepts a
// single key or a space/comma-separated sequence, e.g. "Enter", "Ctrl-C", "Up Up Enter".
internal static class Keys
{
    public static byte[]? Translate(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return Array.Empty<byte>();
        var parts = spec.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new List<byte>();
        foreach (var p in parts)
        {
            var b = One(p);
            if (b is null) return null;   // unknown key — caller reports the error
            bytes.AddRange(b);
        }
        return bytes.ToArray();
    }

    private static byte[]? One(string k)
    {
        switch (k.ToLowerInvariant())
        {
            case "enter" or "return" or "cr": return new byte[] { 0x0D };
            case "lf" or "newline": return new byte[] { 0x0A };
            case "tab": return new byte[] { 0x09 };
            case "esc" or "escape": return new byte[] { 0x1B };
            case "space": return new byte[] { 0x20 };
            case "backspace" or "bs": return new byte[] { 0x7F };
            case "delete" or "del": return Esc("[3~");
            case "up": return Esc("[A");
            case "down": return Esc("[B");
            case "right": return Esc("[C");
            case "left": return Esc("[D");
            case "home": return Esc("[H");
            case "end": return Esc("[F");
            case "pageup" or "pgup": return Esc("[5~");
            case "pagedown" or "pgdn": return Esc("[6~");
            default:
                // Ctrl-<letter> / ^<letter> → control byte (Ctrl-A = 0x01 … Ctrl-Z = 0x1A).
                var ctrl = TryCtrl(k);
                if (ctrl is not null) return ctrl;
                // A bare single character is sent literally.
                if (k.Length == 1) return new[] { (byte)k[0] };
                return null;
        }
    }

    private static byte[]? TryCtrl(string k)
    {
        k = k.ToLowerInvariant();
        char letter;
        if (k.StartsWith("ctrl-") && k.Length == 6) letter = k[5];
        else if (k.StartsWith("^") && k.Length == 2) letter = k[1];
        else return null;
        letter = char.ToUpperInvariant(letter);
        if (letter < 'A' || letter > 'Z') return null;
        return new[] { (byte)(letter - 'A' + 1) };
    }

    private static byte[] Esc(string tail) => Encoding.ASCII.GetBytes("" + tail);
}
