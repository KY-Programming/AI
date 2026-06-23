using KY.AI.Serve;

namespace KY.AI.Ng;

// ky-ai-ng — run the Angular CLI with output mirrored for agents.
//   hub   : control plane — one MCP server + a registry of running supervisors
//   serve : a frontend's dev-server supervisor (rolling log + REST control + auto-register)
//   other : one-shot tee of any `ng` command (build, version, ...)
// The hub + supervisor + shutdown machinery lives in the shared KY.AI.Serve library; this file is
// just the Angular-specific seam: CLI resolution, the build matcher, names and ports.
internal static class Program
{
    private const string DefaultOneShotLog = "ng.log";
    private const int DefaultHubPort = 5101;

    private static readonly SupervisorConfig Supervisor = new()
    {
        ToolName = "ky-ai-ng",
        Noun = "frontend",
        DefaultHubPort = DefaultHubPort,
        Matcher = new NgBuildMatcher(),
        SourceExtensions = new[] { ".ts", ".html", ".scss", ".css", ".sass", ".less" },
        WatchExcludeSegments = new[] { "\\node_modules\\", "\\.angular\\", "\\dist\\" },
        // Prefer the `src` subtree (avoids node_modules churn); fall back to the working dir.
        WatchRoot = wd => { var src = Path.Combine(wd, "src"); return Directory.Exists(src) ? src : wd; },
        DefaultTimeoutMs = 60000,
        DefaultQuietMs = 400,
    };

    private static readonly HubConfig HubCfg = new()
    {
        ToolName = "ky-ai-ng",
        Noun = "frontend",
        NounPlural = "frontends",
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
        if (string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
            return await RunServeAsync(args[1..]);
        return RunOneShot(args);
    }

    // ── serve: supervisor with rolling log + REST control + hub registration ──
    private static async Task<int> RunServeAsync(string[] rest)
    {
        var o = ServeCommandLine.Parse(rest, DefaultHubPort);
        var cwd = Environment.CurrentDirectory;
        var (fileName, prefix) = ResolveCli(cwd);

        var serveArgs = new List<string> { "serve" };
        serveArgs.AddRange(o.Extra);

        var childArgs = new List<string>(prefix);
        childArgs.AddRange(serveArgs);

        var options = new SupervisorOptions
        {
            Name = o.Name ?? DeriveName(cwd),
            WorkingDir = cwd,
            ChildFileName = fileName,
            ChildArgs = childArgs,
            BannerCommand = "ng " + string.Join(' ', serveArgs),
            LogPath = o.LogArg is null ? null : Path.GetFullPath(o.LogArg),
            LogLines = o.LogLines,
            ControlPort = o.ControlPort,
            HubUrl = o.HubUrl,
            UseHub = o.UseHub,
            AutostartHub = o.AutostartHub,
        };
        return await SupervisorHost.RunAsync(options, Supervisor);
    }

    // Default project name: the parent folder of ClientApp (e.g. MyApp), else the cwd name.
    private static string DeriveName(string cwd)
    {
        var dir = new DirectoryInfo(cwd);
        if (string.Equals(dir.Name, "ClientApp", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
            return dir.Parent.Name;
        return dir.Name;
    }

    // ── one-shot: tee any ng command to console + a full log file ─────────────
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
            Console.Error.WriteLine("ky-ai-ng: no command given (e.g. `ky-ai-ng build`). Use --help for usage.");
            return 1;
        }

        var logPath = Path.GetFullPath(logArg ?? DefaultOneShotLog);
        var (fileName, prefix) = ResolveCli(Environment.CurrentDirectory);
        return OneShot.Run("ky-ai-ng", fileName, prefix.Concat(list), logPath);
    }

    // Prefer the project-local CLI (its ng.js via node); fall back to a global `ng`.
    private static (string FileName, IReadOnlyList<string> PrefixArgs) ResolveCli(string startDir)
    {
        var dir = startDir;
        while (true)
        {
            var ngJs = Path.Combine(dir, "node_modules", "@angular", "cli", "bin", "ng.js");
            if (File.Exists(ngJs)) return ("node", new[] { ngJs });

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return ("cmd.exe", new[] { "/c", "ng" });
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        ky-ai-ng — run the Angular CLI with output mirrored for agents.

        HUB — the control plane agents talk to (one MCP server + supervisor registry).
          A `serve` auto-starts it on demand; you never run it yourself.
          MCP tools (each takes a `project`): list · status · wait_for_build · restart · stop · start · tail · set_log_lines · shutdown
          shutdown (MCP) or POST/GET /shutdown stops the hub itself (frees the published binary).

        SERVE — a frontend's dev server (run one per app, registers with the hub):
          ky-ai-ng serve [options]
            --name <id>         Project name in the hub (default: parent folder of ClientApp)
            --hub <url>         Hub URL (default: http://127.0.0.1:5101)
            --log-lines <N>     Lines kept in the in-memory log buffer (default: 200)
            --log-file <file>   Also mirror the buffer to a file (default: off — MCP serves logs)
            --control-port <N>  Local REST control port (default: OS-assigned)
            --no-hub            Buffer-only; do not register with a hub
            --no-hub-autostart  Use a hub if up, but don't auto-start one
          Anything else after `serve` is forwarded to `ng serve` (e.g. --port 4015).
          If no hub is running, serve auto-starts one (detached) unless --no-hub-autostart.
          Ctrl+C stops the dev server (whole tree) and deregisters from the hub.

        ONE-SHOT — tee any ng command to console + a full log file:
          ky-ai-ng build --configuration production prod-build.log
          ky-ai-ng version

        The Angular CLI is resolved from the nearest node_modules\@angular\cli
        (run via node); if none is found, a global `ng` on PATH is used.
        """);
    }
}
