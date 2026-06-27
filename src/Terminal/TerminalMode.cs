namespace KY.AI.Terminal;

// How much the agent may do in a session.
//   ReadOnly — agent can read the screen/scrollback only.
//   Suggest  — agent stages a command; the human approves it (M5).
//   Auto     — agent injects commands directly, subject to the idle gate (M4).
internal enum TerminalMode
{
    ReadOnly,
    Suggest,
    Auto,
}

internal static class TerminalModeExtensions
{
    public static string Wire(this TerminalMode m) => m switch
    {
        TerminalMode.ReadOnly => "read",
        TerminalMode.Suggest => "suggest",
        TerminalMode.Auto => "auto",
        _ => "read",
    };

    public static bool TryParse(string? s, out TerminalMode mode)
    {
        switch (s?.Trim().ToLowerInvariant())
        {
            case "read" or "readonly" or "read-only" or "ro": mode = TerminalMode.ReadOnly; return true;
            case "suggest" or "propose": mode = TerminalMode.Suggest; return true;
            case "auto" or "rw": mode = TerminalMode.Auto; return true;
            default: mode = TerminalMode.ReadOnly; return false;
        }
    }
}
