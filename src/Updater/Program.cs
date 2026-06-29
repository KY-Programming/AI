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
// Each remaining tool is updated by delegating to its OWN `update` command, on purpose: that keeps
// the package-manager detection (dotnet vs npm) and the "stop running instances first so the files
// aren't locked" handling in one place — the tool itself — instead of being duplicated here. The
// installed set is discovered dynamically (dotnet global tools + npm globals), so a tool added later
// is picked up with no change to this one.
internal static class Program
{
    private const string ToolCommand = "ky-ai-updater";
    private const string SelfPackageId = "KY.AI.Updater";
    private const string SelfNpmPackage = "@ky-ai/updater";

    private static int Main(string[] args)
    {
        TrySetUtf8Console();

        var cmd = args.Length == 0 ? "" : args[0].ToLowerInvariant();
        return cmd switch
        {
            "-h" or "--help" or "/?" => Help(),
            "list" or "--list" or "ls" => ListInstalled(),
            "all" or "-all" or "--all" => UpdateOthers(),
            "" or "update" => UpdateSelfThenOthers(),
            _ => Unknown(args[0]),
        };
    }

    // ── phase 1: update ky-ai-updater itself, then chain into phase 2 for everything else ──────────
    private static int UpdateSelfThenOthers()
    {
        var self = (Environment.ProcessPath ?? "").Replace('\\', '/');
        var viaNpm = self.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"{ToolCommand}: updating the KY.AI tool suite.");
        Console.WriteLine($"{ToolCommand}: step 1 — update {ToolCommand} itself; step 2 — update every other installed ky-ai-* tool.");
        Console.WriteLine();

        // Windows can't overwrite the running exe/DLLs, so the whole thing runs from a new window that
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

    // ── phase 2: update every OTHER installed ky-ai-* tool by delegating to its own `update` ──────
    private static int UpdateOthers()
    {
        var tools = Discover()
            .Where(t => !t.Command.Equals(ToolCommand, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tools.Count == 0)
        {
            Console.WriteLine($"{ToolCommand}: no other ky-ai-* tools are installed — nothing to update.");
            return 0;
        }

        Console.WriteLine($"{ToolCommand}: updating {tools.Count} other tool(s): {string.Join(", ", tools.Select(t => t.Command))}");

        var results = new List<(string Command, int Code)>();
        foreach (var t in tools)
        {
            Console.WriteLine();
            Console.WriteLine($"──── {t.Command} update ────────────────────────────────");
            var code = RunToolUpdate(t.Command);
            results.Add((t.Command, code));
        }

        Console.WriteLine();
        Console.WriteLine($"{ToolCommand}: done.");
        foreach (var (command, code) in results)
            Console.WriteLine(code == 0
                ? $"    ✓ {command}  (update started)"
                : $"    ✗ {command}  (exit {code} — see its output above)");

        // On Windows each tool's `update` schedules its real work in its own window and returns 0
        // immediately, so a 0 here means "kicked off", not "finished". Make that explicit.
        if (OperatingSystem.IsWindows())
            Console.WriteLine($"{ToolCommand}: on Windows each tool finishes its update in its own window — let those windows complete.");

        return results.Any(r => r.Code != 0) ? 1 : 0;
    }

    // Run `<command> update` as a child. stdin is redirected (and closed) so the tool's interactive
    // "press Enter to close the running instances" prompt is skipped — it falls straight through to
    // the automatic teardown when it sees redirected input. stdout/stderr are inherited so the user
    // watches the tool's own output. On Windows we go through `cmd /c` so both the .exe shim (dotnet
    // global tool) and the .cmd shim (npm global) resolve the same way.
    private static int RunToolUpdate(string command)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardInput = true };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
                psi.ArgumentList.Add("update");
            }
            else
            {
                psi = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardInput = true };
                psi.ArgumentList.Add("update");
            }

            using var p = Process.Start(psi);
            if (p is null) { Console.Error.WriteLine($"    could not start {command}"); return -1; }
            try { p.StandardInput.Close(); } catch { /* already gone */ }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"    '{command}' could not be launched — is it still on PATH?");
            return -1;
        }
    }

    // ── discovery: every installed ky-ai-* tool, from the dotnet global-tool store and npm globals ──
    private sealed record DiscoveredTool(string Command, string Source);

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

            // The Commands column can in theory list several; the first is the one we invoke.
            var command = cols[2].Split(',')[0].Trim();
            if (command.Length > 0)
                yield return new DiscoveredTool(command, "dotnet global tool");
        }
    }

    // Parse `npm ls --global --depth=0 --json` for any @ky-ai/* package. The bin command follows the
    // suite convention `@ky-ai/<name>` -> `ky-ai-<name>` (e.g. @ky-ai/ng -> ky-ai-ng).
    private static IEnumerable<DiscoveredTool> DiscoverNpm()
    {
        var (ok, output) = TryCapture("npm", ["ls", "--global", "--depth=0", "--json"]);
        if (!ok || output.Trim().Length == 0) yield break;

        var commands = new List<string>();
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
                    if (shortName.Length > 0) commands.Add($"ky-ai-{shortName}");
                }
            }
        }
        catch (JsonException) { yield break; }

        foreach (var c in commands)
            yield return new DiscoveredTool(c, "npm global");
    }

    private static int ListInstalled()
    {
        var tools = Discover();
        if (tools.Count == 0)
        {
            Console.WriteLine($"{ToolCommand}: no ky-ai-* tools found (looked in dotnet global tools and npm globals).");
            return 0;
        }

        Console.WriteLine($"{ToolCommand}: installed ky-ai-* tools:");
        foreach (var t in tools)
        {
            var self = t.Command.Equals(ToolCommand, StringComparison.OrdinalIgnoreCase) ? "  (self)" : "";
            Console.WriteLine($"    • {t.Command,-16} {t.Source}{self}");
        }
        return 0;
    }

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
          {{ToolCommand}}            Update {{ToolCommand}} itself first, then every other installed ky-ai-* tool.
          {{ToolCommand}} --all      Update only the OTHER installed ky-ai-* tools (skip self). This is the
                                second phase the self-update chains into; run it directly to skip self.
          {{ToolCommand}} list       Show which ky-ai-* tools are installed (dotnet global tools + npm globals).

        HOW IT WORKS
          A process can't replace its own files while running, so the self-update is handed to a
          separate step that waits for this one to exit, runs `dotnet tool update` on the updater,
          then re-invokes `{{ToolCommand}} --all` for the rest. On Windows that step is a new window;
          on POSIX it runs inline. Each other tool is updated through its own `update` command, which
          already knows how it was installed (dotnet vs npm) and stops its running instances first.
        """);
        return 0;
    }
}
