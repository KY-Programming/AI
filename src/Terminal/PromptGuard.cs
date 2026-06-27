using System.Text.RegularExpressions;

namespace KY.AI.Terminal;

// Best-effort detection of an interactive password/passphrase prompt. On Windows ConPTY the
// parent cannot observe the child turning off echo, so we recognise the prompt from the rendered
// current line instead (fed from VtScreen, which has already resolved CR / cursor moves / erases).
// While a password prompt is awaiting input we refuse agent injection (so the agent can never type
// into a credential prompt) and surface the state in status. Note: with echo off the typed
// password never appears in the output stream, so it is never captured by the screen model either.
internal sealed class PromptGuard
{
    private volatile bool _passwordActive;

    private static readonly Regex PasswordRx = new(
        @"(password|passphrase)[^:]*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool PasswordActive => _passwordActive;

    // Called after each output batch with the shell's current prompt line (see VtScreen.CursorLineText).
    public void Update(string currentLine) => _passwordActive = PasswordRx.IsMatch(currentLine);
}
