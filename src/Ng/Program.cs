using KY.AI.Serve;

namespace KY.AI.Ng;

// ky-ai-ng — run the Angular CLI with output mirrored for agents.
//   serve    : a frontend's dev-server supervisor (rolling log + REST control + auto-register)
//   run      : supervise an npm script (`npm run <script>`) the same way as serve
//   nx       : supervise an nx target (`nx <target>`, e.g. `nx run app:serve:dev`) the same way
//   shutdown : stop the hub and every supervisor it manages
//   hub      : the control plane (auto-managed — started on demand, not run by hand)
//   other    : one-shot tee of any `ng` command (build, version, ...)
// The hub + supervisor + shutdown machinery lives in the shared KY.AI.Serve library; this file is
// just the Angular-specific seam: CLI resolution, the build matcher, names and ports.
internal static class Program
{
    private const int DefaultHubPort = 5101;

    private static readonly SupervisorConfig Supervisor = new()
    {
        ToolName = "ky-ai-ng",
        Noun = "frontend",
        DefaultHubPort = DefaultHubPort,
        Matcher = new NgBuildMatcher(),
        SourceExtensions = new[] { ".ts", ".html", ".scss", ".css", ".sass", ".less" },
        // Templates/styles hot-swap in place; a .ts change can leave existing instances on old code,
        // so a build touching .ts is flagged mayHaveStaleInstances (→ reload_page).
        HotReloadSafeExtensions = new[] { ".html", ".scss", ".css", ".sass", ".less" },
        WatchExcludeSegments = new[] { "/node_modules/", "/.angular/", "/dist/" },
        // Prefer the `src` subtree (avoids node_modules churn); fall back to the working dir.
        WatchRoot = wd => { var src = Path.Combine(wd, "src"); return Directory.Exists(src) ? src : wd; },
        DefaultTimeoutMs = 60000,
        DefaultQuietMs = 400,
        // The file ky-ai tool's reversible inject targets: the app's index.html, resolved from
        // angular.json's build `index` option (falling back to src/index.html).
        ResolveInjectTarget = NgIndexResolver.Resolve,
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
        if (string.Equals(args[0], "connect", StringComparison.OrdinalIgnoreCase))
            return await StdioBridge.RunAsync(HubCfg, args[1..]);
        if (string.Equals(args[0], "shutdown", StringComparison.OrdinalIgnoreCase))
            return await ShutdownCommand.RunAsync("ky-ai-ng", DefaultHubPort, args[1..]);
        if (string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
            return InitCommand.Run("ky-ai-ng", DefaultHubPort, args[1..]);
        if (string.Equals(args[0], "update", StringComparison.OrdinalIgnoreCase))
            return await UpdateCommand.RunAsync("ky-ai-ng", "KY.AI.Ng", "@ky-ai/ng", DefaultHubPort, args[1..]);
        if (string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
            return await RunServeAsync(args[1..]);
        if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
            return await RunNpmScriptAsync(args[1..]);
        if (string.Equals(args[0], "nx", StringComparison.OrdinalIgnoreCase))
            return await RunNxAsync(args[1..]);
        return RunOneShot(args);
    }

    // ── serve: supervisor with rolling log + REST control + hub registration ──
    private static async Task<int> RunServeAsync(string[] rest)
    {
        var o = ServeCommandLine.Parse(rest, DefaultHubPort);
        var cwd = Environment.CurrentDirectory;

        string fileName;
        IReadOnlyList<string> prefix;
        string workDir;
        try { (fileName, prefix, workDir) = ResolveNgCli(cwd); }
        catch (Exception ex) { Console.Error.WriteLine($"ky-ai-ng: {ex.Message}"); return 1; }

        var serveArgs = new List<string> { "serve" };
        serveArgs.AddRange(o.Extra);

        var childArgs = new List<string>(prefix);
        childArgs.AddRange(serveArgs);

        var options = new SupervisorOptions
        {
            Name = o.Name ?? DeriveName(cwd),
            WorkingDir = workDir,
            ChildFileName = fileName,
            ChildArgs = childArgs,
            BannerCommand = "ng " + string.Join(' ', serveArgs),
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

    // ── run: supervise an npm script (`npm run <script>`) exactly like `serve` ──
    // Same supervisor machinery (rolling log + REST control + hub registration + build tracking via
    // NgBuildMatcher), but the child is `npm run <script>` instead of `ng serve`. For scripts that
    // wrap `ng serve` (e.g. start:debug) the agent watches builds just as it does for serve.
    private static async Task<int> RunNpmScriptAsync(string[] rest)
    {
        var o = ServeCommandLine.Parse(rest, DefaultHubPort);
        if (o.Extra.Count == 0)
        {
            Console.Error.WriteLine("ky-ai-ng: `run` needs a script name (e.g. `ky-ai-ng run start:debug`). Use --help.");
            return 1;
        }

        var script = o.Extra[0];
        var forwarded = o.Extra.Skip(1).ToList();   // forwarded to the script via `npm run <s> -- ...`

        string workDir;
        try { workDir = ResolveNpmWorkingDir(Environment.CurrentDirectory); }
        catch (Exception ex) { Console.Error.WriteLine($"ky-ai-ng: {ex.Message}"); return 1; }

        var (fileName, childArgs) = BuildNpmCommand(script, forwarded);
        var banner = "npm run " + script + (forwarded.Count > 0 ? " -- " + string.Join(' ', forwarded) : "");

        var options = new SupervisorOptions
        {
            Name = o.Name ?? DeriveName(workDir),
            WorkingDir = workDir,
            ChildFileName = fileName,
            ChildArgs = childArgs,
            BannerCommand = banner,
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

    // ── nx: supervise an nx target (`nx <target>`) exactly like `serve` ──
    // Same supervisor machinery as serve/run, but the child is `nx <args>` (e.g.
    // `nx run dashboard:serve:development`). nx targets backed by the Angular builder emit the
    // same esbuild output, so the agent watches builds via NgBuildMatcher just as it does for serve.
    private static async Task<int> RunNxAsync(string[] rest)
    {
        var o = ServeCommandLine.Parse(rest, DefaultHubPort);
        if (o.Extra.Count == 0)
        {
            Console.Error.WriteLine("ky-ai-ng: `nx` needs a target (e.g. `ky-ai-ng nx run dashboard:serve:development`). Use --help.");
            return 1;
        }

        string fileName;
        IReadOnlyList<string> prefix;
        string workDir;
        try { (fileName, prefix, workDir) = ResolveNxCli(Environment.CurrentDirectory); }
        catch (Exception ex) { Console.Error.WriteLine($"ky-ai-ng: {ex.Message}"); return 1; }

        var childArgs = new List<string>(prefix);
        childArgs.AddRange(o.Extra);   // forwarded verbatim to nx (e.g. run dashboard:serve:development)

        // The nx project (e.g. "dashboard") drives both the default hub name and — since an nx repo
        // keeps each app's index.html under its own folder — the inject target resolution.
        var project = DeriveNxName(o.Extra);

        var options = new SupervisorOptions
        {
            Name = o.Name ?? project ?? DeriveName(workDir),
            WorkingDir = workDir,
            ChildFileName = fileName,
            ChildArgs = childArgs,
            BannerCommand = "nx " + string.Join(' ', o.Extra),
            LogPath = o.LogArg is null ? null : Path.GetFullPath(o.LogArg),
            LogLines = o.LogLines,
            ControlPort = o.ControlPort,
            HubUrl = o.HubUrl,
            UseHub = o.UseHub,
            AutostartHub = o.AutostartHub,
            AfterStart = o.AfterStart.Count > 0 ? o.AfterStart : null,
            // Resolve index.html under the nx project, not the workspace root (where NgIndexResolver looks).
            ResolveInjectTarget = wd => NxIndexResolver.Resolve(wd, project),
        };
        return await SupervisorHost.RunAsync(options, Supervisor);
    }

    // Default name for an nx invocation: the project from the target id (the first token containing
    // a colon), e.g. `dashboard:serve:development` → "dashboard". Null if no such token (e.g.
    // `nx serve dashboard`), so the caller falls back to the folder-based DeriveName.
    private static string? DeriveNxName(IReadOnlyList<string> extra)
    {
        foreach (var a in extra)
        {
            var i = a.IndexOf(':');
            if (i > 0) return a[..i];
        }
        return null;
    }

    // Build the child command for `npm run <script> [-- <forwarded>]`, cross-platform.
    // On Windows `npm` is `npm.cmd` (a batch file) which CreateProcess can't exec directly with
    // UseShellExecute=false, so route through the command interpreter; elsewhere `npm` is on PATH.
    private static (string FileName, IReadOnlyList<string> Args) BuildNpmCommand(string script, IReadOnlyList<string> forwarded)
    {
        var npmArgs = new List<string> { "run", script };
        if (forwarded.Count > 0) { npmArgs.Add("--"); npmArgs.AddRange(forwarded); }

        if (OperatingSystem.IsWindows())
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            var shell = string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec;
            var args = new List<string> { "/c", "npm" };
            args.AddRange(npmArgs);
            return (shell, args);
        }
        return ("npm", npmArgs);
    }

    // The directory to run npm in: the nearest package.json walking up from cwd, then a ./ClientApp
    // subfolder (the full-stack-repo convention, mirroring how ResolveNgCli locates the workspace).
    private static string ResolveNpmWorkingDir(string cwd)
    {
        static bool HasPkg(string d) => File.Exists(Path.Combine(d, "package.json"));

        var dir = cwd;
        while (true)
        {
            if (HasPkg(dir)) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }

        var clientApp = Path.Combine(cwd, "ClientApp");
        if (HasPkg(clientApp)) return clientApp;

        throw new InvalidOperationException(
            $"no package.json found. Looked in '{cwd}' and its parents, and in '{clientApp}'. " +
            "Run `ky-ai-ng run` from your Angular workspace (or its parent with a ClientApp subfolder), " +
            "and make sure dependencies are installed (npm install).");
    }

    // Default project name: the parent folder of ClientApp (e.g. MyApp), else the cwd name.
    private static string DeriveName(string cwd)
    {
        var dir = new DirectoryInfo(cwd);
        if (string.Equals(dir.Name, "ClientApp", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
            return dir.Parent.Name;
        return dir.Name;
    }

    // ── one-shot: tee any ng command to console (+ a log file only when --log-file is given) ──
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
            Console.Error.WriteLine("ky-ai-ng: no command given (e.g. `ky-ai-ng build`). Use --help for usage.");
            return 1;
        }

        string fileName;
        IReadOnlyList<string> prefix;
        string workDir;
        try { (fileName, prefix, workDir) = ResolveNgCli(Environment.CurrentDirectory); }
        catch (Exception ex) { Console.Error.WriteLine($"ky-ai-ng: {ex.Message}"); return 1; }

        var logPath = logFile is null ? null : Path.GetFullPath(logFile);
        return OneShot.Run("ky-ai-ng", fileName, prefix.Concat(list), logPath, logLines, workingDir: workDir);
    }

    // Resolve the project-local Angular CLI (its ng.js, run via node) and the directory to run it in.
    // First walk up from cwd; if nothing is found, descend into a `ClientApp` subfolder (the
    // convention the name-derivation assumes — so `serve` works from a full-stack repo root too).
    // Throws if neither yields node_modules\@angular\cli — no silent global-`ng` fallback.
    private static (string FileName, IReadOnlyList<string> PrefixArgs, string WorkingDir) ResolveNgCli(string cwd)
    {
        static string NgJs(string root) => Path.Combine(root, "node_modules", "@angular", "cli", "bin", "ng.js");

        // 1) the nearest CLI walking up from cwd — ng itself finds angular.json from here.
        var dir = cwd;
        while (true)
        {
            if (File.Exists(NgJs(dir))) return ("node", new[] { NgJs(dir) }, cwd);
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }

        // 2) a `ClientApp` workspace one level down — run ng there, where angular.json lives.
        var clientApp = Path.Combine(cwd, "ClientApp");
        if (File.Exists(NgJs(clientApp))) return ("node", new[] { NgJs(clientApp) }, clientApp);

        throw new InvalidOperationException(
            $"no Angular CLI found. Looked for node_modules\\@angular\\cli in '{cwd}' and its parents, " +
            $"and in '{clientApp}'. Run ky-ai-ng from your Angular workspace (where angular.json is) or " +
            "its parent (with a ClientApp subfolder), and make sure dependencies are installed (npm install).");
    }

    // Resolve the project-local nx CLI (its nx.js, run via node) and the workspace root to run it in.
    // Mirrors ResolveNgCli: walk up from cwd for node_modules\nx\bin\nx.js (the dir holding it is the
    // workspace root, where nx.json lives), then fall back to a ./ClientApp subfolder. Running node
    // against nx.js sidesteps the Windows nx.cmd batch-file (which CreateProcess can't exec directly).
    // Throws if neither yields nx — no silent global-`nx` fallback.
    private static (string FileName, IReadOnlyList<string> PrefixArgs, string WorkingDir) ResolveNxCli(string cwd)
    {
        static string NxJs(string root) => Path.Combine(root, "node_modules", "nx", "bin", "nx.js");

        // 1) the nearest nx walking up from cwd — its directory is the workspace root.
        var dir = cwd;
        while (true)
        {
            if (File.Exists(NxJs(dir))) return ("node", new[] { NxJs(dir) }, dir);
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }

        // 2) a `ClientApp` workspace one level down — run nx there, where nx.json lives.
        var clientApp = Path.Combine(cwd, "ClientApp");
        if (File.Exists(NxJs(clientApp))) return ("node", new[] { NxJs(clientApp) }, clientApp);

        throw new InvalidOperationException(
            $"no nx CLI found. Looked for node_modules\\nx in '{cwd}' and its parents, and in " +
            $"'{clientApp}'. Run `ky-ai-ng nx` from your nx workspace (where nx.json is) or its parent " +
            "(with a ClientApp subfolder), and make sure dependencies are installed (npm install).");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        ky-ai-ng — run the Angular CLI with output mirrored for agents.

        SERVE — a frontend's dev server (run one per app; registers with the hub for the agent):
          ky-ai-ng serve [options]
            --name <id>         Project name in the hub (default: parent folder of ClientApp)
            --log-lines <N>     Lines kept in the in-memory log buffer (default: 200; 0 = unlimited)
            --log-file <file>   Also mirror the buffer to a file (default: off — MCP serves logs)
            --rest-port <N>     Local REST control port (default: OS-assigned)
            --hub-port <N>      Hub port to register with (default: 5101; rarely needed — does not start a hub)
            --no-hub            Standalone: buffer + local REST only; no hub, no agent access
            --after-start <cmd...>  Run <cmd> once the first build settles (the dev server is up).
                                    Greedy — everything after it is the command, so put it last.
                                    Replaces the PowerShell-unfriendly `serve & sleep 1 && cmd`.
                                    The command shares this console and is killed when serve stops.
          Anything else after `serve` is forwarded to `ng serve` (e.g. --port 4015).
          Stopping (Ctrl+C or a hard kill) reaps the whole ng tree and deregisters from the hub.
          Example: ky-ai-ng serve --port 4015 --after-start ky-ai-browser -y

        RUN — supervise an npm script (package.json) the same way as serve:
          ky-ai-ng run <script> [options] [-- <args forwarded to the script>]
            Runs `npm run <script>` under the supervisor — same rolling log, REST control, hub
            registration and build tracking as serve, so the agent watches its builds the same way.
            Options are the same as serve (--name, --log-lines, --log-file, --rest-port,
            --hub-port, --no-hub, --after-start). The script runs in the nearest package.json dir
            (searching up, then ./ClientApp).
          Examples:
            ky-ai-ng run start:debug
            ky-ai-ng run start -- --port 4201
            ky-ai-ng run start:dev --after-start ky-ai-browser -y
          Note: `run` is reserved for npm scripts here, so a raw `ng run <target>` is not proxied.

        NX — supervise an nx target (nx workspace) the same way as serve:
          ky-ai-ng nx <target...> [options]
            Runs `nx <target...>` under the supervisor — same rolling log, REST control, hub
            registration and build tracking as serve (nx's Angular builder emits the same esbuild
            output). Everything after `nx` is forwarded verbatim to the local nx CLI. The default
            project name is parsed from the target id (the part before the first colon), e.g.
            `dashboard:serve:development` → "dashboard"; override with --name. Other options are the
            same as serve (--log-lines, --log-file, --rest-port, --hub-port, --no-hub, --after-start).
            nx runs in the nearest nx workspace (node_modules\nx searching up, then ./ClientApp).
          Examples:
            ky-ai-ng nx run dashboard:serve:development
            ky-ai-ng nx serve dashboard --port 4201
            ky-ai-ng nx run dashboard:serve --after-start ky-ai-browser -y

        INIT — wire ky-ai-ng into a Claude Code workspace (.mcp.json + allow-list):
          ky-ai-ng init [-y] [--dir <path>]
          Finds the nearest .mcp.json and .claude/, then (each step confirmed) adds the MCP
          server and allows its commands. Merges into existing files; safe to re-run.

        UPDATE — update this tool to the latest version (via the package manager it came from):
          ky-ai-ng update
          npm install -> `npm install --global @ky-ai/ng@latest`; .NET tool -> `dotnet tool update
          --global KY.AI.Ng --no-cache`. Stops other running instances first (they lock the files):
          asks you to close them, sends shutdown, then hard-kills any leftovers. On Windows it opens
          a new window to update after this exits.

        SHUTDOWN — stop the hub and every frontend it supervises:
          ky-ai-ng shutdown
          To stop a single app, stop its process in your IDE instead.

        ONE-SHOT — tee any other ng command to the console (and a log file with --log-file):
          ky-ai-ng build --configuration production --log-file prod-build.log
          ky-ai-ng version
          Runs once and exits; not supervised and invisible to the agent (prints a reminder).

        The Angular CLI (node_modules\@angular\cli) is found by searching up from the current
        directory, then in a ./ClientApp subfolder; ky-ai-ng errors if neither has it (no global
        `ng` fallback). An MCP hub auto-starts on demand and self-exits when idle — you never run
        it yourself. All HTTP is loopback-only.
        """);
    }
}
