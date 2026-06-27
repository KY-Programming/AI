namespace KY.AI.Serve;

// Renders a tool's start-up status as a rounded box with the tool name in the top border — so the
// agent-facing lines read as one framed block instead of a stack of "tool · …" lines cobbled in
// with the dev server's own output.
//
//   ╭── ky-ai-ng ─────────────────────────────╮
//   │                                         │
//   │   frontend 'Angular'                     │
//   │   control      http://127.0.0.1:34166    │
//   │   …                                      │
//   │                                         │
//   ╰─────────────────────────────────────────╯
//
// When one tool launches another that shares the same console (ky-ai-ng --after-start ky-ai-browser),
// the box is split across the two processes: the launcher draws RenderOpen (top + content, no bottom)
// and the launched tool — told who started it via --started-by — draws RenderContinuation, a
// ├── name ──┤ divider that continues the same frame and closes it:
//
//   ╭── ky-ai-ng ─────────────────────────────...──╮
//   │   …                                          │
//   ├── ky-ai-browser ────────────────────────...──┤
//   │   …                                          │
//   ╰──────────────────────────────────────────...─╯
//
// The two processes can't negotiate a width, so the handoff pair uses a fixed HandoffWidth and the
// bars line up across the boundary. Borders are dimmed and the title tinted; colour is best-effort
// (a redirected console silently ignores it). Over-long lines are truncated rather than wrapping.
public static class BannerBox
{
    // Fixed inner width for the split handoff box (RenderOpen + RenderContinuation), so two separate
    // processes draw borders that line up without agreeing a width over the wire.
    public const int HandoffWidth = 64;

    private const int LeftPad = 3;
    private const int RightPad = 2;
    private const ConsoleColor BorderColor = ConsoleColor.DarkGray;
    private const ConsoleColor TitleColor = ConsoleColor.Cyan;

    // A box row with the value aligned past a fixed-width label column (widest label: "after-start").
    public static string Row(string label, string value) => label.PadRight(12) + value;

    // A complete box (sizes to its widest line).
    public static void Render(string title, IReadOnlyList<string> lines) =>
        Draw(title, lines, width: null, junctionTop: false, drawBottom: true);

    // Top + content but no bottom: this tool is launching another (--started-by) that will continue
    // the frame with RenderContinuation. Fixed width so the two processes' bars align.
    public static void RenderOpen(string title, IReadOnlyList<string> lines) =>
        Draw(title, lines, width: HandoffWidth, junctionTop: false, drawBottom: false);

    // Continue a frame the launching tool left open: a ├── title ──┤ divider, the content, then the
    // closing bottom. Same fixed width as RenderOpen.
    public static void RenderContinuation(string title, IReadOnlyList<string> lines) =>
        Draw(title, lines, width: HandoffWidth, junctionTop: true, drawBottom: true);

    private static void Draw(string title, IReadOnlyList<string> lines, int? width, bool junctionTop, bool drawBottom)
    {
        var content = lines.Count == 0 ? 0 : lines.Max(l => l.Length);
        var inner = width ?? LeftPad + content + RightPad;

        // Keep the whole box (corners included) inside the console so it never wraps.
        var max = ConsoleWidth() - 2;
        if (max > LeftPad + RightPad + 4 && inner > max) inner = max;

        if (!junctionTop) Console.WriteLine();    // a continuation connects straight onto the open box above it
        WriteHeader(title, inner, junctionTop);
        WriteRow("", inner);                       // top padding row
        foreach (var line in lines) WriteRow(line, inner);
        WriteRow("", inner);                       // bottom padding row
        if (drawBottom)
        {
            Tint("╰" + new string('─', inner) + "╯", BorderColor);
            Console.WriteLine();
        }
    }

    // ╭── title ──…──╮  (or ├── title ──…──┤ for a continuation) — title tinted, the rest dimmed.
    private static void WriteHeader(string title, int inner, bool junction)
    {
        var (left, right) = junction ? ("├", "┤") : ("╭", "╮");
        var lead = $"── {title} ";
        var dashes = Math.Max(0, inner - lead.Length);
        Tint(left + "── ", BorderColor);
        Tint(title, TitleColor);
        Tint(" " + new string('─', dashes) + right, BorderColor);
        Console.WriteLine();
    }

    // │   content        │  (bars dimmed, content in the default colour)
    private static void WriteRow(string content, int inner)
    {
        var avail = inner - LeftPad - RightPad;
        if (content.Length > avail) content = avail <= 1 ? "" : content[..(avail - 1)] + "…";
        var trail = inner - LeftPad - content.Length;

        Tint("│", BorderColor);
        Console.Write(new string(' ', LeftPad) + content + new string(' ', Math.Max(0, trail)));
        Tint("│", BorderColor);
        Console.WriteLine();
    }

    private static void Tint(string s, ConsoleColor c)
    {
        try { Console.ForegroundColor = c; } catch { /* redirected */ }
        Console.Write(s);
        try { Console.ResetColor(); } catch { /* redirected */ }
    }

    private static int ConsoleWidth()
    {
        try { var w = Console.WindowWidth; return w > 0 ? w : 80; } catch { return 80; }
    }
}
