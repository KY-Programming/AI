using System.ComponentModel;
using KY.AI.Serve;
using ModelContextProtocol.Server;

namespace KY.AI.Terminal;

// MCP tools exposed by the terminal hub. Each (except list) takes a `session` name (from `list`)
// and is forwarded to that session's REST control API. Lives in the exe assembly so the terminal
// hub scans only these tools, not the ng/net build tools in the Serve assembly. Allow-list as
// mcp__ky-ai-terminal__<name>.
[McpServerToolType]
internal static class TerminalTools
{
    [McpServerTool(Name = "list"), Description(
        "List the terminal sessions registered with the hub, each with its shell, mode and status. " +
        "Call this first to learn the session names the other tools expect.")]
    public static Task<string> List() => Hub.ListAsync();

    [McpServerTool(Name = "status"), Description(
        "Status of one session (pass session) or all of them (omit session): name, shell, pid, " +
        "running, mode (read/suggest/auto), screen size, cursor position, and scrollback size.")]
    public static Task<string> Status([Description("Session name; omit for all")] string? session = null)
        => string.IsNullOrWhiteSpace(session) ? Hub.ListAsync() : Hub.ForwardAsync(session!, HttpMethod.Get, "/status", 5);

    [McpServerTool(Name = "read_screen"), Description(
        "Return the session's current visible screen — the rendered character grid plus the cursor " +
        "position. Use this for full-screen / TUI output (editors, pagers, REPLs).")]
    public static Task<string> ReadScreen([Description("Session name (see list)")] string session)
        => Hub.ForwardAsync(session, HttpMethod.Get, "/screen", 5);

    [McpServerTool(Name = "read_scrollback"), Description(
        "Return the last N lines of the session's scrollback transcript (0 = everything kept). Use " +
        "this for ordinary command output that has scrolled past the visible screen.")]
    public static Task<string> ReadScrollback(
        [Description("Session name (see list)")] string session,
        [Description("Trailing lines; 0 = all kept")] int lines = 0)
        => Hub.ForwardAsync(session, HttpMethod.Get, $"/scrollback?lines={lines}", 5);

    [McpServerTool(Name = "send_text"), Description(
        "Type a command line at the shell prompt (literal text; it does NOT press Enter — follow " +
        "with send_keys \"Enter\"). Allowed only in auto mode, and only when the shell is idle at a " +
        "prompt and the human isn't typing; otherwise it waits briefly then returns ok:false.")]
    public static Task<string> SendText(
        [Description("Session name (see list)")] string session,
        [Description("Text to type")] string text)
        => Hub.ForwardAsync(session, HttpMethod.Post, $"/send-text?text={Uri.EscapeDataString(text)}", 8);

    [McpServerTool(Name = "send_keys"), Description(
        "Send named keys to the session: Enter, Tab, Esc, Up/Down/Left/Right, Home/End, Backspace, " +
        "Ctrl-C, Ctrl-D, or a sequence like \"Up Up Enter\". Allowed in auto mode. Not idle-gated, so " +
        "it can interrupt a running command (Ctrl-C) or drive a TUI.")]
    public static Task<string> SendKeys(
        [Description("Session name (see list)")] string session,
        [Description("Key or space/comma-separated keys")] string keys)
        => Hub.ForwardAsync(session, HttpMethod.Post, $"/send-keys?keys={Uri.EscapeDataString(keys)}", 5);

    [McpServerTool(Name = "resize"), Description(
        "Request a screen resize (columns × rows). Advisory: a console-attached session may re-sync " +
        "to the host window size.")]
    public static Task<string> Resize(
        [Description("Session name (see list)")] string session,
        [Description("Columns")] int cols,
        [Description("Rows")] int rows)
        => Hub.ForwardAsync(session, HttpMethod.Post, $"/resize?cols={cols}&rows={rows}", 5);

    [McpServerTool(Name = "propose_command"), Description(
        "Stage a command for the human to approve (suggest mode). It is shown in their terminal as a " +
        "banner; the human runs it with the approve chord (default Ctrl+B Enter) or dismisses it with " +
        "Ctrl+B Esc. Returns the proposal id. You cannot approve your own proposal.")]
    public static Task<string> ProposeCommand(
        [Description("Session name (see list)")] string session,
        [Description("The command line to stage")] string text)
        => Hub.ForwardAsync(session, HttpMethod.Post, $"/propose?text={Uri.EscapeDataString(text)}", 5);

    [McpServerTool(Name = "get_mode"), Description(
        "Get the session's permission mode (read/suggest/auto), whether it is idle, and any pending proposal.")]
    public static Task<string> GetMode([Description("Session name (see list)")] string session)
        => Hub.ForwardAsync(session, HttpMethod.Get, "/mode", 5);

    [McpServerTool(Name = "set_mode"), Description(
        "Set the session's permission mode: read | suggest | auto.")]
    public static Task<string> SetMode(
        [Description("Session name (see list)")] string session,
        [Description("read | suggest | auto")] string mode)
        => Hub.ForwardAsync(session, HttpMethod.Post, $"/mode?mode={Uri.EscapeDataString(mode)}", 5);
}
