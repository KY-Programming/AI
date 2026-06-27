using System.Text;

namespace KY.AI.Terminal;

// Renders the TUI: the shell's raw passthrough stays in a top scroll region (so colors survive —
// VtScreen is text-only), and the bottom rows are our chrome: a notice/popout line, a rounded input
// box (3 rows), and a hint line. Layout (1-based rows), R = Bottom:
//   1 .. H-R          shell output (scroll region)
//   H-4               notice / suggestion popout (welcome at start; proposal in suggest mode)
//   H-3 / H-2 / H-1   input box: top border / input line / bottom border
//   H                 hint line
//
// A top header is intentionally NOT used: ConPTY positions the shell from row 1 (absolute), so any
// top header would be overwritten. Cursor: real one hidden, synthetic caret in the input line;
// after each redraw the cursor is parked at the shell snapshot (VtScreen.Cursor) — no DECSC/DECRC
// (ConPTY owns that slot). Caller holds the stdout lock.
internal sealed class Tui
{
    public const int Bottom = 5;          // popout + box(3) + hint
    private const string Esc = "";

    private readonly Stream _out;
    private short _w, _h;

    public Tui(Stream outStream, short w, short h) { _out = outStream; _w = w; _h = h; }

    public short Width => _w;
    public int TopRows => Math.Max(1, _h - Bottom);

    public void Resize(short w, short h) { _w = w; _h = h; }

    // [2J clears the viewport, then set the scroll region and home. NOTE: do NOT add [3J here to
    // clear the scrollback — in Windows Terminal it corrupts the viewport/scroll-region origin and
    // pushes the whole TUI down the screen. Clean scroll-up needs the alt-screen buffer instead.
    public void Enter() => Write($"{Esc}[?25l{Esc}[2J{Esc}[1;{TopRows}r{Esc}[1;1H");

    public void SetScrollRegion() => Write($"{Esc}[1;{TopRows}r");

    // Hand the whole screen to a full-screen app / password prompt.
    public void EnterRaw() => Write($"{Esc}[?25h{Esc}[r");

    // Reclaim the bottom rows for chrome (the caller then redraws it).
    public void ExitRaw() => Write($"{Esc}[?25l{Esc}[1;{TopRows}r");

    public void Leave() => Write($"{Esc}[r{Esc}[?25h{Esc}[{Math.Max(1, (int)_h)};1H");

    // Draw the bottom chrome and park the (hidden) cursor at the shell snapshot (0-based).
    public void Redraw(int screenRow, int screenCol, string popout, string boxTop, string input, string boxBottom, string hint)
    {
        var b = _h - Bottom;
        var sb = new StringBuilder(384);
        sb.Append($"{Esc}[?25l");
        Row(sb, b + 1, popout);
        Row(sb, b + 2, boxTop);
        Row(sb, b + 3, input);
        Row(sb, b + 4, boxBottom);
        Row(sb, b + 5, hint);
        var r = Math.Clamp(screenRow + 1, 1, TopRows);
        var c = Math.Clamp(screenCol + 1, 1, Math.Max(1, (int)_w));
        sb.Append($"{Esc}[{r};{c}H");
        Write(sb.ToString());
    }

    private static void Row(StringBuilder sb, int row, string content) =>
        sb.Append($"{Esc}[{row};1H{Esc}[2K").Append(content);

    private void Write(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        _out.Write(bytes, 0, bytes.Length);
        _out.Flush();
    }
}

// Pure builders for the chrome strings. Rounded box-drawing chars (UTF-8); blue is #254b7c.
internal static class Chrome
{
    private const string Esc = "";
    private const string Blue = Esc + "[38;2;37;75;124m";
    private const string White = Esc + "[38;2;255;255;255m";
    private const string BlueBg = Esc + "[48;2;37;75;124m";
    private const string Off = Esc + "[0m";
    private const string Dim = Esc + "[2m";
    private const char Rule = '─';        // horizontal separator above/below the input

    // Full-width horizontal rule framing the input line (no corners, no side borders).
    public static string BoxTop(int width) => Blue + new string(Rule, Math.Max(0, width)) + Off;
    public static string BoxBottom(int width) => Blue + new string(Rule, Math.Max(0, width)) + Off;

    // The input line: "❯ <text>" with a synthetic caret, no side borders (the row is cleared first).
    public static string InputRow(string display, int caretCol, bool empty, int width)
    {
        if (display.Length > width) display = display[..width];
        caretCol = Math.Clamp(caretCol, 0, Math.Max(0, width - 1));
        if (empty)
        {
            const string ph = "type a command";
            var head = display[..Math.Min(caretCol, display.Length)];
            var caret = caretCol < display.Length ? display[caretCol].ToString() : " ";
            return head + Esc + "[7m" + caret + Esc + "[27m" + Dim + ph + Off;
        }
        if (caretCol >= display.Length) display = display.PadRight(caretCol + 1);
        var h = display[..caretCol];
        var ch = display[caretCol];
        var t = display[(caretCol + 1)..];
        return h + Esc + "[7m" + ch + Esc + "[27m" + t;
    }

    public static string Hint(string mode, string shell, bool hasPending, int width)
    {
        // Mode sits next to the Shift+Tab hint that changes it; approve/dismiss only when staged.
        var s = $"  {shell} | {mode} | Shift+Tab: mode";
        if (hasPending) s += " | Ctrl+E: run | Esc: dismiss";
        if (s.Length > width) s = s[..width];
        return Dim + s + Off;
    }

    // The popout row: a pending proposal (blue), else the startup welcome (with connect status), else blank.
    public static string Popout(string? pending, bool welcome, bool agentConnected, string shell, string mode, int width)
    {
        if (!string.IsNullOrEmpty(pending))
            return Bar($"  AI suggests: {pending}", width);
        if (welcome)
        {
            var status = agentConnected ? " - agent connected" : " - waiting for agent to connect...";
            return Bar($"  ky-ai-terminal - you drive the shell; an AI agent rides along over MCP{status}", width);
        }
        return "";
    }

    private static string Bar(string s, int width)
    {
        if (s.Length > width) s = s[..width];
        var pad = s.Length < width ? s + new string(' ', width - s.Length) : s;
        return $"{BlueBg}{White}{pad}{Off}";
    }
}
