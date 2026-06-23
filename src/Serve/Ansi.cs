using System.Text.RegularExpressions;

namespace KY.AI.Serve;

// Strips ANSI/VT escape sequences (colours, cursor moves) so the stored log and the
// build error lines are plain text. The live console keeps the raw, coloured output.
internal static class Ansi
{
    private const char Esc = (char)27;

    // ESC [ ... <final byte> — CSI sequences (SGR colours, cursor moves, etc.)
    private static readonly Regex Csi = new(Esc + @"\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static string Strip(string s) => s.IndexOf(Esc) < 0 ? s : Csi.Replace(s, "");
}
