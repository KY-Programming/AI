using KY.AI.Serve;

namespace KY.AI.Net;

// ky-ai-dotnet — run a .NET backend with output mirrored for agents.
//   hub  : control plane — one MCP server + a registry of running supervisors
//   run  : a backend's dev supervisor (rolling log + REST control + auto-register)
//   other: one-shot tee of any `dotnet` command (build, test, ...)
// The hub + supervisor + shutdown machinery lives in the shared KY.AI.Serve library; this file is
// just the .NET-specific seam: the dotnet command, the build matcher, names and ports.
internal static class Program
{
    private const string DefaultOneShotLog = "dotnet.log";
    private const int DefaultHubPort = 5102;

    private static readonly SupervisorConfig Supervisor = new()
    {
        ToolName = "ky-ai-dotnet",
        Noun = "backend",
        DefaultHubPort = DefaultHubPort,
        Matcher = new DotnetBuildMatcher(),
        SourceExtensions = new[] { ".cs", ".razor", ".cshtml", ".csproj" },
        WatchExcludeSegments = new[] { "\\bin\\", "\\obj\\", "\\.git\\", "\\node_modules\\" },
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
        if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
            return await RunSupervisorAsync(args[1..]);
        return RunOneShot(args);
    }

    // ── run: supervisor with rolling log + REST control + hub registration ────
    private static async Task<int> RunSupervisorAsync(string[] rest)
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

    // ── one-shot: tee any dotnet command to console + a full log file ─────────
    private static int RunOneShot(string[] args)
    {
        var list = args.ToList();

        string? logArg = null;
        var li = list.FindIndex(a => a is "--log" or "-l");
        if (li >= 0 && li + 1 < list.Count)
        {
            logArg = list[li + 1];
            list.RemoveAt(li + 1);
            list.RemoveAt(li);
        }
        else if (list.Count > 0 && list[^1].EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            logArg = list[^1];
            list.RemoveAt(list.Count - 1);
        }

        if (list.Count == 0)
        {
            Console.Error.WriteLine("ky-ai-dotnet: no command given (e.g. `ky-ai-dotnet build`). Use --help for usage.");
            return 1;
        }

        var logPath = Path.GetFullPath(logArg ?? DefaultOneShotLog);
        return OneShot.Run("ky-ai-dotnet", "dotnet", list, logPath);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        ky-ai-dotnet — run a .NET backend with output mirrored for agents.

        HUB — the control plane agents talk to (one MCP server + supervisor registry).
          A `run` auto-starts it on demand; you never run it yourself.
          MCP tools (each takes a `project`): list · status · wait_for_build · restart · stop · start · tail · set_log_lines · shutdown
          shutdown (MCP) or POST/GET /shutdown stops the hub itself (frees the published binary).

        RUN — a backend's dev server (run one per app, registers with the hub):
          ky-ai-dotnet run [options]
            --name <id>         Project name in the hub (default: the .csproj / folder name)
            --hub <url>         Hub URL (default: http://127.0.0.1:5102)
            --log-lines <N>     Lines kept in the rolling log (default: 200)
            --log-file <file>   Also mirror the rolling log to a file (default: off — MCP serves logs)
            --control-port <N>  Local REST control port (default: OS-assigned)
            --no-watch          Use `dotnet run` instead of `dotnet watch run`
            --no-hub            Tee + rolling log only; do not register with a hub
            --no-hub-autostart  Use a hub if up, but don't auto-start one
          Anything else after `run` is forwarded to dotnet (e.g. --project ./Api.csproj).

        ONE-SHOT — tee any dotnet command to console + a full log file:
          ky-ai-dotnet build -c Release build.log
          ky-ai-dotnet test

        Spawns `dotnet` from PATH. All HTTP is loopback-only.
        """);
    }
}
