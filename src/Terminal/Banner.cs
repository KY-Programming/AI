using System.Text;

namespace KY.AI.Terminal;

// Renders the in-console notices for the suggest-mode flow. These are written into the output
// stream as styled lines (reverse video for the label) — simple and always visible, at the cost
// of scrolling with content rather than being a fixed status bar. The agent never sees these
// (they are not fed to the screen model). prefixName is the human-readable approve chord.
internal static class Banner
{
    private const string Esc = "";
    private static readonly string Rev = Esc + "[7m";
    private static readonly string Dim = Esc + "[2m";
    private static readonly string Off = Esc + "[0m";

    public static byte[] Proposal(string cmd, string prefixName) => Line(
        $"{Rev} AI proposes {Off} {cmd}  {Dim}({prefixName} Enter = run · {prefixName} Esc = dismiss){Off}");

    public static byte[] Approved(string cmd) => Line($"{Rev} approved {Off} {cmd}");
    public static byte[] Dismissed() => Line($"{Dim}(proposal dismissed){Off}");
    public static byte[] ModeChanged(string mode) => Line($"{Rev} mode {Off} {mode}");
    public static byte[] Note(string text) => Line($"{Dim}{text}{Off}");

    private static byte[] Line(string s) => Encoding.UTF8.GetBytes("\r\n" + s + "\r\n");
}
