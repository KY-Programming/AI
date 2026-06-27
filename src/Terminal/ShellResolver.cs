namespace KY.AI.Terminal;

// Resolves the user's --shell choice (plus any trailing `-- <args>`) into a full executable path
// and a single CreateProcess command line. Default order prefers a modern PowerShell, falling
// back to Windows PowerShell then cmd.
internal static class ShellResolver
{
    public sealed record Resolved(string ExePath, string CommandLine, string Display);

    public static Resolved Resolve(string? shell, IReadOnlyList<string> args)
    {
        var (exe, display) = ResolveExe(shell);
        // Skip PowerShell's startup banner ("Windows PowerShell / Install the latest…") when we
        // launch it ourselves with no extra args — the TUI shows its own chrome instead.
        if (args.Count == 0 && display is "powershell" or "pwsh")
            args = new[] { "-NoLogo" };
        var sb = new List<string> { Quote(exe) };
        foreach (var a in args) sb.Add(NeedsQuote(a) ? Quote(a) : a);
        return new Resolved(exe, string.Join(' ', sb), display);
    }

    private static (string Exe, string Display) ResolveExe(string? shell)
    {
        if (string.IsNullOrWhiteSpace(shell))
        {
            var def = Find("pwsh.exe") ?? Find("powershell.exe") ?? Find("cmd.exe") ?? "cmd.exe";
            return (def, Path.GetFileNameWithoutExtension(def));
        }

        switch (shell.Trim().ToLowerInvariant())
        {
            case "pwsh":
                return (Find("pwsh.exe") ?? "pwsh.exe", "pwsh");
            case "powershell":
                return (Find("powershell.exe") ?? "powershell.exe", "powershell");
            case "cmd":
                return (Find("cmd.exe") ?? "cmd.exe", "cmd");
            case "bash":
            case "git-bash":
            case "gitbash":
                return (FindGitBash() ?? Find("bash.exe") ?? "bash.exe", "bash");
            case "ssh":
                return (Find("ssh.exe") ?? FindOpenSsh() ?? "ssh.exe", "ssh");
            default:
                // Treat the value as an explicit executable name or full path.
                var resolved = File.Exists(shell) ? shell : (Find(shell) ?? Find(shell + ".exe") ?? shell);
                return (resolved, Path.GetFileNameWithoutExtension(resolved));
        }
    }

    // Look up an executable on PATH (and the current directory).
    private static string? Find(string exe)
    {
        try
        {
            if (Path.IsPathRooted(exe) && File.Exists(exe)) return exe;
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), exe); } catch { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* fall through */ }
        return null;
    }

    private static string? FindGitBash()
    {
        foreach (var p in new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        })
            if (File.Exists(p)) return p;
        return null;
    }

    private static string? FindOpenSsh()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var p = Path.Combine(sys, "OpenSSH", "ssh.exe");
        return File.Exists(p) ? p : null;
    }

    private static bool NeedsQuote(string s) => s.Length == 0 || s.Contains(' ') || s.Contains('\t');
    private static string Quote(string s) => s.Contains('"') ? s : $"\"{s}\"";
}
