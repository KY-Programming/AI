using KY.AI.Serve;

namespace KY.AI.Wpf;

// ky-ai-wpf — see and steer a running WPF (or WinForms/Win32) window from an AI agent over MCP,
// through UI Automation only: never through synthetic input.
//   hub      : control plane — one MCP server exposing the automation tools
//   connect  : the stdio bridge an agent spawns, proxying to the hub
//   list     : the same window list the `list` tool returns, for a human checking what is visible
//
// The other tools in the suite have a `serve` you run per project, because they supervise a process.
// This one deliberately has none: the user starts their desktop app the way they always do — from
// the IDE, from the Explorer — and the tool attaches to a window that already exists. It never
// starts a process and never stops one. So there is nothing to register and no supervisor: the hub
// itself drives UI Automation, and stays up while an agent's bridge is attached (the shared
// idle-shutdown in HubHost counts bridges as well as supervisors).
internal static class Program
{
    private const int DefaultHubPort = 5106;

    private static readonly HubConfig HubCfg = new()
    {
        ToolName = "ky-ai-wpf",
        Noun = "window",
        NounPlural = "windows",
        DefaultPort = DefaultHubPort,
    };

    private static async Task<int> Main(string[] args)
    {
        // Only a bare help flag prints THIS help — a flag after a subcommand belongs to that
        // subcommand, so `ky-ai-wpf init --help` reaches InitCommand's own (per-agent paths) help
        // instead of being swallowed here.
        if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
        {
            PrintHelp();
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ky-ai-wpf: UI Automation is a Windows API — this tool only runs on Windows.");
            return 1;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "hub":
                Cli.TrySetUtf8Console();
                // Scan THIS exe's assembly so the hub exposes only the automation tools (WpfTools),
                // not the ng/net build tools that also live in the Serve assembly.
                return await HubHost.RunAsync(HubCfg, args[1..], typeof(Program).Assembly);

            case "connect":
                Cli.TrySetUtf8Console();
                return await StdioBridge.RunAsync(HubCfg, args[1..]);

            case "shutdown":
                return await ShutdownCommand.RunAsync("ky-ai-wpf", DefaultHubPort, args[1..]);

            case "init":
                // Reflect this exe's assembly (WpfTools) for the allow-list — not Serve's HubTools.
                // runHint is null: there is no `serve` to start, the agent just needs the wiring.
                return InitCommand.Run("ky-ai-wpf", DefaultHubPort, args[1..], typeof(Program).Assembly, runHint: null);

            case "update":
                return await UpdateCommand.RunAsync("ky-ai-wpf", "KY.AI.Wpf", npmPackageId: null, DefaultHubPort, args[1..]);

            case "list":
                // The `list` tool's own output, printed. Handy for checking what the agent will see
                // (and that a window is visible at all) without starting an MCP session.
                Cli.TrySetUtf8Console();
                Console.WriteLine(await Engine.ListWindows());
                return 0;

            default:
                Console.Error.WriteLine($"ky-ai-wpf: unknown command '{args[0]}'. Run `ky-ai-wpf --help`.");
                return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        ky-ai-wpf — let an AI agent see and steer a running WPF window over MCP, without ever
        taking your focus, mouse or keyboard.

        There is no `serve` here: you start your desktop app the way you always do, and the agent
        attaches to the window. This tool never starts or stops your process.

        LIST — the windows the agent can see (process, pid, title, handle):
          ky-ai-wpf list

        INIT — wire ky-ai-wpf into an AI agent's workspace (Claude Code, Cursor, or VS Code):
          ky-ai-wpf init [--agent <claude|cursor|vscode>] [-y] [--dir <path>]
          Walks up from the current directory for the agent's config folder, then (each step
          confirmed) adds the MCP server and, for Claude, allows its commands in
          .claude/settings.local.json. Without --agent the agent is auto-detected and confirmed
          via a picker; -y takes the detected one and accepts every prompt (non-interactive).
          Merges into existing files; safe to re-run.

        SHUTDOWN — stop the hub:
          ky-ai-wpf shutdown

        UPDATE — update to the latest release:
          ky-ai-wpf update

        Everything the agent does goes through UI Automation: it reads the automation tree and
        drives InvokePattern / ValuePattern / TogglePattern / ExpandCollapsePattern /
        SelectionItemPattern — the same path a screen reader takes. No keystrokes or mouse events
        are synthesised, no window is raised, the cursor never moves and the clipboard is never
        touched, so you can keep working while an agent exercises your app. Screenshots use
        PrintWindow, which captures a window sitting behind others.

        An MCP hub auto-starts on demand (127.0.0.1:5106) and self-exits when idle — you never run
        it yourself. All HTTP is loopback-only. Windows-only (UI Automation).
        """);
    }
}
