using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KY.AI.Updater;

// ky-ai-updater — one command to update the whole KY.AI tool suite.
//
//   ky-ai-updater            update ky-ai-updater itself first, then every other installed ky-ai-* tool
//   ky-ai-updater --all      update only the OTHER installed ky-ai-* tools (skip self) — the 2nd phase
//   ky-ai-updater list       show which ky-ai-* tools are installed (and how)
//
// Why two phases?  A running process can't replace its own files on Windows (and shouldn't assume it
// can elsewhere), so a tool can't cleanly self-update in place. ky-ai-updater sidesteps that the same
// way each tool's own `update` does: it hands the self-update to a SEPARATE process that waits for
// THIS one to exit, runs `dotnet tool update` on the updater, and then re-invokes the freshly
// installed `ky-ai-updater --all` to do the rest. On Windows that separate process is a new console
// window (the file lock is real there); on POSIX the binary can be replaced in place, so it runs
// inline and you watch it happen.
//
// Each remaining tool is updated INLINE, in this single window. We deliberately do NOT delegate to
// each tool's own `update`: that command runs the tool's own binary, which locks the tool's files,
// so on Windows it has to hand the real work off to yet another window — giving one stray window per
// tool that the user must close by hand. The updater is a *different* binary, so it doesn't lock any
// target tool's files; once we stop that tool's running instances, we can run its package-manager
// update right here. Discovery captures both the package id and how it was installed (dotnet vs npm),
// so the per-tool detection still lives with the data it describes. The installed set is discovered
// dynamically (dotnet global tools + npm globals), so a tool added later is picked up with no change.
internal static class Program
{
    private const string ToolCommand = "ky-ai-updater";
    private const string SelfPackageId = "KY.AI.Updater";
    private const string SelfNpmPackage = "@ky-ai/updater";

    // How long to wait for a tool's instances to exit on their own after asking them to shut down,
    // before force-terminating the stragglers that are still holding the files we're about to replace.
    private const int GraceSeconds = 5;

    private static int Main(string[] args)
    {
        TrySetUtf8Console();

        // --skip-self: skip the self-update step and go straight to updating the other tools, inline
        // in this window. Mainly for testing a locally-built updater — the normal run self-updates
        // first, which would pull the *published* version over your local build. (`--all` is the same
        // path under its phase-2 name.) Strip it out before reading the positional command.
        var skipSelf = args.Any(a => a.Equals("--skip-self", StringComparison.OrdinalIgnoreCase));
        var rest = args.Where(a => !a.Equals("--skip-self", StringComparison.OrdinalIgnoreCase)).ToArray();

        var cmd = rest.Length == 0 ? "" : rest[0].ToLowerInvariant();
        return cmd switch
        {
            "-h" or "--help" or "/?" => Help(),
            "list" or "--list" or "ls" => ListInstalled(),
            "all" or "-all" or "--all" => UpdateOthers(),
            "" or "update" => skipSelf ? UpdateOthers() : UpdateSelfThenOthers(),
            _ => Unknown(rest[0]),
        };
    }

    // ── phase 1: update ky-ai-updater itself, then chain into phase 2 for everything else ──────────
    private static int UpdateSelfThenOthers()
    {
        var self = (Environment.ProcessPath ?? "").Replace('\\', '/');
        var viaNpm = self.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"{ToolCommand}: updating the KY.AI tool suite.");

        // If the updater itself is already current there's nothing to self-update — and the updater
        // doesn't lock the OTHER tools' files, so we can update the whole rest inline, in THIS window,
        // with no detached process at all. The detached window is only needed when the updater really
        // must be replaced (a running process can't overwrite its own files on Windows).
        var me = Discover().FirstOrDefault(t => t.Command.Equals(ToolCommand, StringComparison.OrdinalIgnoreCase));
        if (me is not null)
        {
            var latest = GetLatestVersion(me);
            if (latest != "?" && VersionsMatch(me.CurrentVersion, latest))
            {
                Console.WriteLine($"{ToolCommand}: {ToolCommand} is already up to date ({me.CurrentVersion}) — updating the other tools here.");
                Console.WriteLine();
                return UpdateOthers();
            }
        }

        Console.WriteLine($"{ToolCommand}: step 1 — update {ToolCommand} itself; step 2 — update every other installed ky-ai-* tool.");
        Console.WriteLine();

        // Windows can't overwrite the running exe/DLLs, so the self-update runs from a new window that
        // waits for us to exit first. Self-update, then re-invoke the *updated* `ky-ai-updater --all`.
        if (OperatingSystem.IsWindows())
            return LaunchDetachedWindows(viaNpm);

        // POSIX: replacing a running binary's file is fine — self-update inline, then run the rest.
        Console.WriteLine($"{ToolCommand}: updating {ToolCommand} …");
        var selfCode = viaNpm
            ? RunInline("npm", ["install", "--global", $"{SelfNpmPackage}@latest"])
            : RunInline("dotnet", ["tool", "update", "--global", SelfPackageId, "--no-cache"]);
        if (selfCode != 0)
            Console.WriteLine($"{ToolCommand}: self-update exited with code {selfCode} — continuing to the other tools anyway.");

        Console.WriteLine();
        // Re-invoke the freshly installed updater so the NEWEST logic updates the rest; if the shim
        // isn't resolvable on PATH for some reason, fall back to doing it in-process.
        var rest = RunInline(ToolCommand, ["--all"]);
        return rest >= 0 ? rest : UpdateOthers();
    }

    // ── phase 2: update every OTHER installed ky-ai-* tool — inline, in this one window ────────────
    private static int UpdateOthers()
    {
        // Discover everything (incl. self, already updated in this window) and look up each latest
        // version once. The same lookups drive both the overview and the "needs updating?" decision.
        Console.WriteLine($"{ToolCommand}: checking installed tools against the latest published versions…");
        var before = Discover();
        var latest = FetchLatest(before);

        // Show the overview up front — at this point self has already been updated, and this is the
        // state about to be acted on, so the table doubles as "here's what's getting updated below".
        Console.WriteLine();
        Console.WriteLine($"{ToolCommand}: tool suite overview");
        RenderOverview(before, latest);
        Console.WriteLine();

        var others = before.Where(t => !t.Command.Equals(ToolCommand, StringComparison.OrdinalIgnoreCase)).ToList();

        var failed = false;
        foreach (var t in others)
        {
            var newest = latest.GetValueOrDefault(t.Command, "?");

            // Already current — nothing to do. (Only skip when we actually know the latest; an
            // unknown "?" falls through and we try the update rather than assume it's up to date.)
            if (newest != "?" && VersionsMatch(t.CurrentVersion, newest))
            {
                Console.WriteLine($"    ✓ {t.Command,-16} already up to date ({t.CurrentVersion})");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"──── {t.Command} ────────────────────────────────");
            var target = newest == "?" ? "latest" : newest;
            Console.WriteLine($"    updating from {t.CurrentVersion} to {target} via {t.ManagerLabel}…");

            // A running instance of the tool keeps its binaries locked; we (the updater) are a
            // different binary, so once those instances are gone we can replace the files in place.
            StopInstances(t.Command);

            var code = RunPackageUpdate(t.UpdateExe, t.UpdateArgs);
            if (code != 0)
            {
                failed = true;
                Console.WriteLine($"    ✗ {t.Command} update exited with code {code} — see its output above.");
            }
        }

        return failed ? 1 : 0;
    }

    // Stop every running instance of a tool so the package manager can overwrite its files. The tool's
    // executable is the same as its command name (ky-ai-ng.exe -> process name "ky-ai-ng"), so we can
    // match instances by image name. We first ask the tool to shut its stack down gracefully (the
    // hub-based tools cascade to their supervisors), give them a moment, then hard-kill the stragglers.
    private static void StopInstances(string command)
    {
        var running = SafeGetByName(command);
        if (running.Length == 0)
        {
            Console.WriteLine($"    no running {command} instances — nothing to stop.");
            return;
        }

        Console.WriteLine($"    {running.Length} running {command} instance(s) would lock the files — stopping them…");

        // Graceful first: best-effort `<command> shutdown`. Unknown-command/non-zero exits are fine —
        // tools without a shutdown subcommand just fall through to the kill below. Output is captured
        // (not inherited) so it doesn't clutter this window.
        TryCapture(command, ["shutdown"]);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(GraceSeconds) && SafeGetByName(command).Length > 0)
            Thread.Sleep(250);

        var stubborn = SafeGetByName(command);
        foreach (var p in stubborn)
        {
            try { p.Kill(entireProcessTree: true); Console.WriteLine($"    • killed pid {p.Id}"); }
            catch (Exception ex) { Console.WriteLine($"    • could not kill pid {p.Id}: {ex.Message}"); }
        }
    }

    // Process.GetProcessesByName can throw if a process exits mid-enumeration; treat that as "none".
    private static Process[] SafeGetByName(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return Array.Empty<Process>(); }
    }

    // Run a package-manager update with inherited console so the user watches it happen. On Windows we
    // go through `cmd /c` so the .cmd shim (npm) resolves the same way the .exe (dotnet) does.
    private static int RunPackageUpdate(string exe, string[] args)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(exe);
            }
            else
            {
                psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            }
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) { Console.Error.WriteLine($"    could not start {exe}"); return -1; }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"    '{exe}' could not be launched — is it on PATH?");
            return -1;
        }
    }

    // ── discovery: every installed ky-ai-* tool, from the dotnet global-tool store and npm globals ──
    // PackageId is the package-manager identifier (dotnet: "ky.ai.ng"; npm: "@ky-ai/ng"); ViaNpm says
    // which manager installed it; CurrentVersion is the installed version read during discovery. The
    // update command and the "latest version" query are both derived from those, so phase 2 can update
    // the tool directly and `list` can show current-vs-latest.
    private sealed record DiscoveredTool(
        string Command, string Source, string CurrentVersion, string PackageId, bool ViaNpm)
    {
        public string UpdateExe => ViaNpm ? "npm" : "dotnet";

        public string[] UpdateArgs => ViaNpm
            ? ["install", "--global", $"{PackageId}@latest"]
            : ["tool", "update", "--global", PackageId, "--no-cache"];

        // Short, human-friendly name of the package manager for status lines.
        public string ManagerLabel => ViaNpm ? "npm" : "dotnet tool";
    }

    private static List<DiscoveredTool> Discover()
    {
        var byCommand = new Dictionary<string, DiscoveredTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in DiscoverDotnet().Concat(DiscoverNpm()))
            if (!byCommand.ContainsKey(t.Command))
                byCommand[t.Command] = t;
        return byCommand.Values.OrderBy(t => t.Command, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Parse `dotnet tool list --global`. Columns are separated by 2+ spaces:
    //   Package Id      Version   Commands
    //   ky.ai.ng        22.1.0    ky-ai-ng
    private static IEnumerable<DiscoveredTool> DiscoverDotnet()
    {
        var (ok, output) = TryCapture("dotnet", ["tool", "list", "--global"]);
        if (!ok) yield break;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var cols = Regex.Split(line, @"\s{2,}");
            if (cols.Length < 3) continue;
            var packageId = cols[0].Trim();
            if (!packageId.StartsWith("ky.ai.", StringComparison.OrdinalIgnoreCase)) continue;  // skips the header too

            var version = cols[1].Trim();
            // The Commands column can in theory list several; the first is the one we invoke.
            var command = cols[2].Split(',')[0].Trim();
            if (command.Length > 0)
                yield return new DiscoveredTool(command, "dotnet global tool", version, packageId, ViaNpm: false);
        }
    }

    // Parse `npm ls --global --depth=0 --json` for any @ky-ai/* package. The bin command follows the
    // suite convention `@ky-ai/<name>` -> `ky-ai-<name>` (e.g. @ky-ai/ng -> ky-ai-ng).
    private static IEnumerable<DiscoveredTool> DiscoverNpm()
    {
        var (ok, output) = TryCapture("npm", ["ls", "--global", "--depth=0", "--json"]);
        if (!ok || output.Trim().Length == 0) yield break;

        var found = new List<(string Command, string Package, string Version)>();
        // npm can exit non-zero (peer-dep warnings) yet still print valid JSON, so parse defensively.
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("dependencies", out var deps) &&
                deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in deps.EnumerateObject())
                {
                    var name = dep.Name;
                    if (!name.StartsWith("@ky-ai/", StringComparison.OrdinalIgnoreCase)) continue;
                    var shortName = name["@ky-ai/".Length..];
                    if (shortName.Length == 0) continue;
                    var version = dep.Value.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?";
                    found.Add(($"ky-ai-{shortName}", name, version));
                }
            }
        }
        catch (JsonException) { yield break; }

        foreach (var (command, package, version) in found)
            yield return new DiscoveredTool(command, "npm global", version, package, ViaNpm: true);
    }

    private static int ListInstalled()
    {
        var tools = Discover();
        if (tools.Count == 0)
        {
            Console.WriteLine($"{ToolCommand}: no ky-ai-* tools found (looked in dotnet global tools and npm globals).");
            return 0;
        }

        Console.WriteLine($"{ToolCommand}: installed ky-ai-* tools (querying nuget.org / npm for the latest versions)…");
        Console.WriteLine();
        RenderOverview(tools, FetchLatest(tools));
        return 0;
    }

    // Look up the latest published version of each tool, keyed by command. The lookups are independent
    // network calls, so run them concurrently — the whole set resolves in about one round-trip instead
    // of one per tool.
    private static Dictionary<string, string> FetchLatest(IReadOnlyList<DiscoveredTool> tools)
    {
        var tasks = tools.Select(t => Task.Run(() => GetLatestVersion(t))).ToArray();
        Task.WaitAll(tasks);
        var byCommand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tools.Count; i++)
            byCommand[tools[i].Command] = tasks[i].Result;
        return byCommand;
    }

    // Print the installed-vs-latest table: one row per tool with its current version, the latest
    // published version, an at-a-glance status, and how it was installed.
    private static void RenderOverview(IReadOnlyList<DiscoveredTool> tools, IReadOnlyDictionary<string, string> latest)
    {
        foreach (var t in tools)
        {
            var newest = latest.GetValueOrDefault(t.Command, "?");
            var self = t.Command.Equals(ToolCommand, StringComparison.OrdinalIgnoreCase) ? "  (self)" : "";

            // "?" means the feed query failed (offline, not indexed yet) — don't claim anything then.
            var status =
                newest == "?" ? "latest unknown" :
                VersionsMatch(t.CurrentVersion, newest) ? "up to date" :
                "update available";

            Console.WriteLine($"    • {t.Command,-16} {t.CurrentVersion,-12} → {newest,-12} {status,-16} {t.Source}{self}");
        }
    }

    // The latest published version of a tool, or "?" if the feed can't be reached / doesn't list it.
    //   * npm:    `npm view <pkg> version` prints just the latest version string.
    //   * dotnet: `dotnet tool search <id>` returns a table whose "Latest Version" column (cols[1]) is
    //             what we want; match the row whose Package ID equals ours (search is substring-based).
    private static string GetLatestVersion(DiscoveredTool t)
    {
        if (t.ViaNpm)
        {
            var (ok, output) = TryCapture("npm", ["view", t.PackageId, "version"]);
            var v = output.Trim();
            return ok && v.Length > 0 ? v : "?";
        }

        var (found, table) = TryCapture("dotnet", ["tool", "search", t.PackageId]);
        if (!found) return "?";
        foreach (var raw in table.Split('\n'))
        {
            var cols = Regex.Split(raw.Trim(), @"\s{2,}");
            if (cols.Length >= 2 && cols[0].Trim().Equals(t.PackageId, StringComparison.OrdinalIgnoreCase))
                return cols[1].Trim();
        }
        return "?";
    }

    // Compare two version strings for "is the installed one current". A plain ordinal compare is
    // enough for the suite's simple numeric versions and treats "?" (unknown current) as not-current.
    private static bool VersionsMatch(string current, string latest)
        => current.Equals(latest, StringComparison.OrdinalIgnoreCase);

    // ── windows self-update: a new window that runs after we (the file lock) are gone ─────────────
    // `Wait-Process -Id <pid>` returns the instant we exit (when the OS releases our file handles);
    // -Timeout + SilentlyContinue is a safety valve so it can't hang forever. Then: self-update, then
    // re-invoke the freshly installed `ky-ai-updater --all` for the rest, then stay open to show it.
    private static int LaunchDetachedWindows(bool viaNpm)
    {
        var selfUpdate = viaNpm
            ? $"npm install --global {SelfNpmPackage}@latest"
            : $"dotnet tool update --global {SelfPackageId} --no-cache";
        try
        {
            var pid = Environment.ProcessId;
            var script =
                $"Wait-Process -Id {pid} -Timeout 30 -ErrorAction SilentlyContinue; " +
                "Start-Sleep -Milliseconds 300; " +
                $"Write-Host 'Updating {ToolCommand} itself…' -ForegroundColor Cyan; " +
                $"{selfUpdate}; " +
                "Write-Host ''; " +
                "Write-Host 'Updating the other KY.AI tools…' -ForegroundColor Cyan; " +
                $"{ToolCommand} --all; " +
                "Write-Host ''; Read-Host 'Press Enter to close'";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,   // its own window
                Arguments = $"-NoProfile -Command \"{script}\"",
            };
            Process.Start(psi);
            Console.WriteLine($"{ToolCommand}: the update runs in a new window once this process (pid {pid}) exits.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ToolCommand}: could not launch the updater ({ex.Message}). Run it by hand:");
            Console.Error.WriteLine($"  {selfUpdate}");
            Console.Error.WriteLine($"  {ToolCommand} --all");
            return 1;
        }
    }

    // ── small process helpers ─────────────────────────────────────────────────────────────────────

    // Run a command with inherited console, returning its exit code — or -1 if the executable itself
    // couldn't be started (not on PATH), so the caller can fall back.
    private static int RunInline(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    // Capture a command's stdout. On Windows route through `cmd /c` so .cmd shims (npm) resolve too.
    // Returns (started, stdout): `started` is false only when the executable couldn't be launched.
    private static (bool Ok, string Output) TryCapture(string exe, string[] args)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(exe);
                foreach (var a in args) psi.ArgumentList.Add(a);
            }
            else
            {
                psi = new ProcessStartInfo(exe);
                foreach (var a in args) psi.ArgumentList.Add(a);
            }
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var p = Process.Start(psi);
            if (p is null) return (false, "");
            var stdout = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (true, stdout);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "");   // tool (dotnet / npm) not installed — just contributes nothing
        }
    }

    private static void TrySetUtf8Console()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected console */ }
    }

    private static int Unknown(string arg)
    {
        Console.Error.WriteLine($"{ToolCommand}: unknown command '{arg}'.");
        Console.Error.WriteLine();
        Help();
        return 1;
    }

    private static int Help()
    {
        Console.WriteLine($$"""
        {{ToolCommand}} — update the whole KY.AI tool suite with one command.

        USAGE
          {{ToolCommand}}              Update {{ToolCommand}} itself first, then every other installed ky-ai-* tool.
          {{ToolCommand}} --skip-self  Skip the self-update; update only the other tools, inline in this window.
                                  Handy for testing a locally-built updater (a normal run would pull the
                                  published version over it). Same as `--all`.
          {{ToolCommand}} --all        Update only the OTHER installed ky-ai-* tools (skip self). This is the
                                  second phase the self-update chains into; run it directly to skip self.
          {{ToolCommand}} list         Show which ky-ai-* tools are installed (dotnet global tools + npm globals),
                                  with each tool's installed version and the latest published version.

        HOW IT WORKS
          A process can't replace its own files while running, so the self-update is handed to a
          separate step that waits for this one to exit, runs `dotnet tool update` on the updater,
          then re-invokes `{{ToolCommand}} --all` for the rest. On Windows that step is a new window;
          on POSIX it runs inline. Every OTHER tool is then updated in that same window: the updater
          stops the tool's running instances (so its files aren't locked) and runs the right package
          manager directly — no extra window per tool to close by hand.
        """);
        return 0;
    }
}
