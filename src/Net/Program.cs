using KY.AI.Serve;

namespace KY.AI.Net;

// ky-ai-dotnet — run a .NET backend with output mirrored for agents.
//   serve    : a backend's dev supervisor (dotnet watch run; rolling log + REST control + auto-register)
//   shutdown : stop the hub and every supervisor it manages
//   hub      : the control plane (auto-managed — started on demand, not run by hand)
//   other    : one-shot tee of any `dotnet` command (build, test, ...)
// The hub + supervisor + shutdown machinery lives in the shared KY.AI.Serve library; this file is
// just the .NET-specific seam: the dotnet command, the build matcher, names and ports.
internal static class Program
{
    private const int DefaultHubPort = 5102;

    private static readonly SupervisorConfig Supervisor = new()
    {
        ToolName = "ky-ai-dotnet",
        Noun = "backend",
        DefaultHubPort = DefaultHubPort,
        Matcher = new DotnetBuildMatcher(),
        SourceExtensions = new[] { ".cs", ".razor", ".cshtml", ".csproj" },
        WatchExcludeSegments = new[] { "/bin/", "/obj/", "/.git/", "/node_modules/" },
        WatchRoot = wd => wd,
        DefaultTimeoutMs = 90000,  // backend builds can be slow; raise for a cold first build
        DefaultQuietMs = 500,
    };

    private static readonly HubConfig HubCfg = new()
    {
        ToolName = "ky-ai-dotnet",
        Noun = "backend",
        NounPlural = "backends",
        DefaultPort = DefaultHubPort,
    };

    private static async Task<int> Main(string[] args)
    {
        Cli.TrySetUtf8Console();

        if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "/?"))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        if (string.Equals(args[0], "hub", StringComparison.OrdinalIgnoreCase))
            return await HubHost.RunAsync(HubCfg, args[1..]);
        if (string.Equals(args[0], "shutdown", StringComparison.OrdinalIgnoreCase))
            return await ShutdownCommand.RunAsync("ky-ai-dotnet", DefaultHubPort, args[1..]);
        if (string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
            return InitCommand.Run("ky-ai-dotnet", DefaultHubPort, args[1..]);
        if (string.Equals(args[0], "update", StringComparison.OrdinalIgnoreCase))
            return await UpdateCommand.RunAsync("ky-ai-dotnet", "KY.AI.Net", npmPackageId: null, DefaultHubPort, args[1..]);
        if (string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
            return await RunServeAsync(args[1..]);
        return RunOneShot(args);   // `run`/`watch` land here and get nudged toward `serve`
    }

    // ── serve: supervisor (dotnet watch run) with rolling log + REST control + hub registration ──
    private static async Task<int> RunServeAsync(string[] rest)
    {
        var o = ServeCommandLine.Parse(rest, DefaultHubPort);

        // ky-ai-dotnet's only extra flag beyond the common set: --no-watch.
        var watch = true;
        var extra = new List<string>();
        foreach (var a in o.Extra)
        {
            if (a == "--no-watch") watch = false;
            else extra.Add(a);
        }

        var childArgs = new List<string>();
        if (watch) { childArgs.Add("watch"); childArgs.Add("run"); }
        else childArgs.Add("run");
        childArgs.AddRange(extra);

        var cwd = Environment.CurrentDirectory;
        var options = new SupervisorOptions
        {
            Name = o.Name ?? DeriveName(cwd),
            WorkingDir = cwd,
            ChildFileName = "dotnet",
            ChildArgs = childArgs,
            BannerCommand = "dotnet " + string.Join(' ', childArgs),
            LogPath = o.LogArg is null ? null : Path.GetFullPath(o.LogArg),
            LogLines = o.LogLines,
            ControlPort = o.ControlPort,
            HubUrl = o.HubUrl,
            UseHub = o.UseHub,
            AutostartHub = o.AutostartHub,
            AfterStart = o.AfterStart.Count > 0 ? o.AfterStart : null,
        };
        return await SupervisorHost.RunAsync(options, Supervisor);
    }

    // Default project name: the .csproj name in the current directory, else the folder name.
    private static string DeriveName(string cwd)
    {
        try
        {
            var csproj = Directory.GetFiles(cwd, "*.csproj").FirstOrDefault();
            if (csproj is not null) return Path.GetFileNameWithoutExtension(csproj);
        }
        catch { /* fall through */ }
        return new DirectoryInfo(cwd).Name;
    }

    // ── one-shot: tee any dotnet command to console (+ a log file only when --log-file is given) ──
    private static int RunOneShot(string[] args)
    {
        var list = args.ToList();

        string? logFile = null;
        var logLines = 200;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == "--log-file" && i + 1 < list.Count)
            { logFile = list[i + 1]; list.RemoveAt(i + 1); list.RemoveAt(i); i--; }
            else if (list[i] == "--log-lines" && i + 1 < list.Count && int.TryParse(list[i + 1], out var n))
            { logLines = Math.Max(0, n); list.RemoveAt(i + 1); list.RemoveAt(i); i--; }
        }

        if (list.Count == 0)
        {
            Console.Error.WriteLine("ky-ai-dotnet: no command given (e.g. `ky-ai-dotnet build`). Use --help for usage.");
            return 1;
        }

        // Someone who typed `run`/`watch` lands here — nudge them to the supervised `serve`.
        var verb = list[0].ToLowerInvariant();
        var hint = verb is "run" or "watch"
            ? $"Note: `{verb}` is not a supervised command — use `ky-ai-dotnet serve` for an agent-controllable dev server (add --no-watch for stable debugging)."
            : null;

        var logPath = logFile is null ? null : Path.GetFullPath(logFile);
        return OneShot.Run("ky-ai-dotnet", "dotnet", list, logPath, logLines, hint);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        ky-ai-dotnet — run a .NET backend with output mirrored for agents.

        SERVE — a backend's dev server (run one per app; registers with the hub for the agent):
          ky-ai-dotnet serve [options]
            --name <id>         Project name in the hub (default: the .csproj / folder name)
            --log-lines <N>     Lines kept in the rolling log (default: 200; 0 = unlimited)
            --log-file <file>   Also mirror the rolling log to a file (default: off — MCP serves logs)
            --rest-port <N>     Local REST control port (default: OS-assigned)
            --no-watch          Use `dotnet run` instead of `dotnet watch run` (stable for debugging)
            --hub-port <N>      Hub port to register with (default: 5102; rarely needed — does not start a hub)
            --no-hub            Standalone: tee + rolling log + local REST only; no hub, no agent access
            --after-start <cmd...>  Run <cmd> once the first build settles (the backend is up).
                                    Greedy — everything after it is the command, so put it last.
                                    Replaces the PowerShell-unfriendly `serve & sleep 1 && cmd`.
                                    The command shares this console and is killed when serve stops.
          Anything else after `serve` is forwarded to dotnet (e.g. --project ./Api.csproj).
          `dotnet watch run` hot-reloads; the agent verifies builds via the hub's wait_for_build.

        INIT — wire ky-ai-dotnet into a Claude Code workspace (.mcp.json + allow-list):
          ky-ai-dotnet init [-y] [--dir <path>]
          Finds the nearest .mcp.json and .claude/, then (each step confirmed) adds the MCP
          server and allows its commands. Merges into existing files; safe to re-run.

        UPDATE — update this tool to the latest version (via the package manager it came from):
          ky-ai-dotnet update
          Runs `dotnet tool update --global KY.AI.Net --no-cache`. Stops other running instances
          first (they lock the files): asks you to close them, sends shutdown, then hard-kills any
          leftovers. On Windows it opens a new window so the update can replace this tool after exit.

        SHUTDOWN — stop the hub and every backend it supervises:
          ky-ai-dotnet shutdown
          To stop a single app, stop its process in your IDE instead.

        ONE-SHOT — tee any other dotnet command to the console (and a log file with --log-file):
          ky-ai-dotnet build -c Release --log-file build.log
          ky-ai-dotnet test
          Runs once and exits; not supervised and invisible to the agent (prints a reminder).

        An MCP hub auto-starts on demand and self-exits when idle — you never run it yourself.
        Spawns `dotnet` from PATH. All HTTP is loopback-only.
        """);
    }
}
