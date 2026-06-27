using KY.AI.Serve;

namespace KY.AI.Terminal;

// ky-ai-terminal — a shell the human drives normally while an AI agent joins the same live
// session over MCP (read-only / suggest / auto).
//   hub  : control plane — one MCP server + a registry of running sessions
//   run  : host a shell in a pseudoconsole and relay it through this console
// The hub + registration + forwarding machinery lives in the shared KY.AI.Serve library; this
// exe adds the ConPTY session, the terminal-specific MCP tools, names and port.
internal static class Program
{
    private const int DefaultHubPort = 5103;

    private static readonly HubConfig HubCfg = new()
    {
        ToolName = "ky-ai-terminal",
        Noun = "session",
        NounPlural = "sessions",
        DefaultPort = DefaultHubPort,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        // Sub-commands. Everything else (including no args at all) starts a session — `run` is
        // accepted as an optional alias so old command lines keep working.
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "hub", StringComparison.OrdinalIgnoreCase))
            {
                Cli.TrySetUtf8Console();
                // Scan THIS exe's assembly so the hub exposes only the terminal tools (TerminalTools),
                // not the ng/net build tools that also live in the Serve assembly.
                return await HubHost.RunAsync(HubCfg, args[1..], typeof(Program).Assembly);
            }

            if (string.Equals(args[0], "shutdown", StringComparison.OrdinalIgnoreCase))
                return await ShutdownCommand.RunAsync("ky-ai-terminal", DefaultHubPort, args[1..]);

            // Reflect this exe's assembly (TerminalTools) for the allow-list — not Serve's HubTools.
            if (string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
                return InitCommand.Run("ky-ai-terminal", DefaultHubPort, args[1..], typeof(Program).Assembly);
        }

        var rest = args.Length > 0 && string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase)
            ? args[1..]
            : args;
        return await RunSessionAsync(rest);
    }

    private static async Task<int> RunSessionAsync(string[] rest)
    {
        var o = ServeCommandLine.Parse(rest, DefaultHubPort);

        // Peel the terminal-specific flags out of the common parser's leftovers. Everything after
        // a literal `--` (or any non-flag leftover) is the shell's own argv.
        string? shell = null;
        var scrollback = 5000;
        var mode = TerminalMode.Suggest;   // default: agent can propose, you approve
        byte prefixByte = 0x02;
        var prefixName = "Ctrl+B";
        string? auditPath = null;
        var useTui = true;
        var shellArgs = new List<string>();
        var afterSep = false;
        for (var i = 0; i < o.Extra.Count; i++)
        {
            var a = o.Extra[i];
            if (afterSep) { shellArgs.Add(a); continue; }
            switch (a)
            {
                case "--": afterSep = true; break;
                case "--shell": if (++i < o.Extra.Count) shell = o.Extra[i]; break;
                case "--scrollback": if (++i < o.Extra.Count && int.TryParse(o.Extra[i], out var sb)) scrollback = Math.Max(100, sb); break;
                case "--mode": if (++i < o.Extra.Count) TerminalModeExtensions.TryParse(o.Extra[i], out mode); break;
                case "--prefix":
                    if (++i < o.Extra.Count)
                    {
                        var pb = Keys.Translate(o.Extra[i]);
                        if (pb is { Length: 1 }) { prefixByte = pb[0]; prefixName = o.Extra[i]; }
                    }
                    break;
                case "--keys": if (++i < o.Extra.Count) { /* win32 Ctrl+Enter — deferred */ } break;
                case "--audit": if (++i < o.Extra.Count) auditPath = Path.GetFullPath(o.Extra[i]); break;
                case "--no-tui": useTui = false; break;   // undocumented: plain transparent passthrough
                default: shellArgs.Add(a); break;
            }
        }

        var cwd = Environment.CurrentDirectory;
        var resolved = ShellResolver.Resolve(shell, shellArgs);

        // Plain-passthrough welcome line (the TUI draws its own chrome instead).
        if (!useTui)
            Console.WriteLine($"ky-ai-terminal: starting {resolved.Display} session ({mode.Wire()} mode). " +
                              "Type \"exit\" to quit. Run \"ky-ai-terminal --help\" for options.");

        var options = new TerminalSessionOptions
        {
            Name = o.Name ?? new DirectoryInfo(cwd).Name,
            ShellDisplay = resolved.Display,
            ExePath = resolved.ExePath,
            CommandLine = resolved.CommandLine,
            WorkingDir = cwd,
            Scrollback = o.LogLines != 200 ? o.LogLines : scrollback,
            InitialMode = mode,
            PrefixByte = prefixByte,
            PrefixName = prefixName,
            AuditPath = auditPath,
            Tui = useTui,
        };

        try
        {
            // Always host the loopback control API; --no-hub only skips hub registration/auto-start.
            var session = new TerminalSession(options);
            var hostOpt = new TerminalHost.Options(HubCfg.ToolName, o.ControlPort, o.HubUrl, o.UseHub, o.AutostartHub, DefaultHubPort);
            return await TerminalHost.RunAsync(session, hostOpt);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ky-ai-terminal: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        ky-ai-terminal — a shell you drive normally while an AI agent joins over MCP.

        Just run `ky-ai-terminal` to start a session; the `run` word is optional.

        RUN (default) — host a shell in a pseudoconsole and relay it through this terminal:
          ky-ai-terminal [run] [options] [-- <shell args>]
            --shell <name>      cmd | powershell | pwsh | bash | ssh | <exe path> (default: pwsh)
            --mode <m>          read | suggest | auto  (default: suggest)
                                  read    — agent can only read the screen/scrollback
                                  suggest — agent proposes; you approve with the chord below
                                  auto    — agent types directly (when idle at a prompt)
            --name <id>         Session name in the hub (default: folder name)
            --scrollback <N>    Scrollback lines kept for the agent (default: 5000)
            --prefix <chord>    Multiplexer prefix chord (default: Ctrl-B). Then:
                                  <prefix> Enter = approve · <prefix> Esc = dismiss · <prefix> m = cycle mode
            --audit <file>      Mirror the agent-injection audit log to a file
            --hub-port <N>      Hub port to register with (default: 5103; rarely needed — does not start a hub)
            --no-hub            Standalone: local control API only; no hub, no agent access
          Examples:
            ky-ai-terminal
            ky-ai-terminal --shell pwsh --mode suggest
            ky-ai-terminal --shell ssh -- user@host
            ky-ai-terminal --shell ssh -- -p 2222 user@host

        INIT — wire ky-ai-terminal into a Claude Code workspace (.mcp.json + allow-list):
          ky-ai-terminal init [-y] [--dir <path>]
          Finds the nearest .mcp.json and .claude/, then (each step confirmed) adds the MCP
          server and allows its commands. Merges into existing files; safe to re-run.

        SHUTDOWN — stop the hub and every session it supervises:
          ky-ai-terminal shutdown

        Credentials never reach the agent: you authenticate (e.g. ssh) yourself, and agent input
        is blocked while a password prompt is active. An MCP hub auto-starts on demand and self-exits
        when idle — you never run it yourself. All HTTP is loopback-only. Requires Windows 10 1809+ (ConPTY).
        """);
    }
}
