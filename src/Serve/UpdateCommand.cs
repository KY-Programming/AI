using System.Diagnostics;

namespace KY.AI.Serve;

// `<tool> update` — update THIS tool to the latest version using whichever package manager it was
// installed with. The install route is detected from the running executable's path:
//   * under a node_modules path  -> npm     (npm install --global <pkg>@latest)
//   * otherwise (a .NET global tool) -> dotnet (dotnet tool update --global <pkg> --no-cache)
//
// `--no-cache` is essential: without it `dotnet tool update` consults NuGet's local HTTP cache and
// often reports the tool is already up to date for a while after a new version is published, so it
// silently does nothing. Disabling the cache forces a fresh feed query and the real latest version.
//
// Before updating we stop every OTHER instance of this tool (the hub, supervisors, stray one-shots):
// a running instance keeps the installed binaries/DLLs locked, so the package manager can't replace
// them. The escalation is gentle — list them, give the user a chance to close them, ask the hub to
// shut the stack down, then hard-kill whatever is still alive — with a line printed at each step.
//
// A running process can't overwrite its own files on Windows, so a direct self-update would fail
// with a file lock; there the updater is launched in a new window that waits for THIS process (by
// PID) to exit before it runs the package manager. On POSIX the running binary can be replaced in
// place, so it runs inline.
public static class UpdateCommand
{
    // How long to wait for a graceful shutdown before forcibly terminating the stragglers.
    private const int GraceSeconds = 5;

    public static async Task<int> RunAsync(
        string toolName, string dotnetPackageId, string? npmPackageId, int defaultHubPort, string[] rest)
    {
        Cli.TrySetUtf8Console();

        var self = (Environment.ProcessPath ?? "").Replace('\\', '/');
        var viaNpm = npmPackageId is not null &&
                     self.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase);

        var command = viaNpm
            ? $"npm install --global {npmPackageId}@latest"
            : $"dotnet tool update --global {dotnetPackageId} --no-cache";

        Console.WriteLine($"{toolName}: updating via  {command}");

        // Free the locked files first: any other running instance would block the package manager.
        await StopOtherInstancesAsync(toolName, defaultHubPort);

        // Windows can't replace the running exe/DLLs in place — run the updater after we exit.
        if (OperatingSystem.IsWindows())
            return LaunchDetachedWindows(toolName, command);

        // POSIX: replacing a running binary's file is fine — run inline and show output.
        return RunInline(viaNpm ? "npm" : "dotnet",
            viaNpm ? ["install", "--global", $"{npmPackageId}@latest"]
                   : ["tool", "update", "--global", dotnetPackageId, "--no-cache"]);
    }

    // Make sure no other instance of this tool is holding its files open before we update. Matches
    // siblings by image name (the hub, `serve` supervisors, other one-shots — all run as the same
    // executable) and excludes the current process, which exits on its own right after this.
    private static async Task StopOtherInstancesAsync(string toolName, int defaultHubPort)
    {
        using var me = Process.GetCurrentProcess();
        var myName = me.ProcessName;
        var myId = me.Id;

        // Re-enumerated each step: processes come and go as they shut down.
        Process[] Others() =>
            SafeGetByName(myName).Where(p => p.Id != myId).ToArray();

        var running = Others();
        if (running.Length == 0)
        {
            Console.WriteLine($"{toolName}: no other instances running — nothing to stop.");
            return;
        }

        Console.WriteLine($"{toolName}: {running.Length} other instance(s) are running and would lock the files being updated:");
        foreach (var p in running)
            Console.WriteLine($"    • pid {p.Id}  {myName}");

        // 1) Give a real user a chance to close them cleanly. Skipped when input is redirected
        //    (an agent/CI run can't press Enter) — there we go straight to the automatic teardown.
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine($"{toolName}: close them now if you can, then press Enter to continue (they'll be stopped automatically otherwise)…");
            try { Console.ReadLine(); } catch { /* no console attached */ }
            running = Others();
            if (running.Length == 0)
            {
                Console.WriteLine($"{toolName}: all instances closed — continuing.");
                return;
            }
        }

        // 2) Ask the hub to shut the whole stack down gracefully (cascades to the supervisors).
        Console.WriteLine($"{toolName}: sending shutdown command…");
        await ShutdownCommand.RunAsync(toolName, defaultHubPort, Array.Empty<string>());

        // 3) Give them a few seconds to exit on their own.
        Console.WriteLine($"{toolName}: waiting up to {GraceSeconds}s for processes to exit…");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(GraceSeconds) && Others().Length > 0)
            await Task.Delay(250);

        // 4) Hard-terminate anything still alive (with its child dev-server tree).
        var stubborn = Others();
        if (stubborn.Length == 0)
        {
            Console.WriteLine($"{toolName}: all instances stopped — continuing.");
            return;
        }

        Console.WriteLine($"{toolName}: force-terminating {stubborn.Length} process(es) that didn't exit…");
        foreach (var p in stubborn)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                Console.WriteLine($"    • killed pid {p.Id}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    • could not kill pid {p.Id}: {ex.Message}");
            }
        }

        // Give the kills a moment to settle, then report the outcome.
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromSeconds(3) && Others().Length > 0)
            await Task.Delay(150);

        var left = Others().Length;
        Console.WriteLine(left == 0
            ? $"{toolName}: all instances stopped — continuing."
            : $"{toolName}: {left} process(es) still running — the update may fail if they keep the files locked.");
    }

    // Process.GetProcessesByName can throw if a process exits mid-enumeration; treat that as "none".
    private static Process[] SafeGetByName(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return Array.Empty<Process>(); }
    }

    private static int RunInline(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) { Console.Error.WriteLine($"could not start {exe}"); return 1; }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"'{exe}' not found on PATH — is it installed?");
            return 1;
        }
    }

    // Open a new console that waits for THIS process to exit — it holds the tool's files open, so
    // the update must run after it's gone — then runs the update and stays open so the result is
    // visible. `Wait-Process -Id <pid>` is a deterministic wait on our PID (no blind sleep / race):
    // it returns the instant we exit, which is when the OS releases our file handles. The `-Timeout`
    // + `SilentlyContinue` is just a safety valve so the window can't hang forever if we don't exit;
    // a process-already-gone error is likewise swallowed and the update proceeds immediately.
    private static int LaunchDetachedWindows(string toolName, string command)
    {
        try
        {
            var pid = Environment.ProcessId;
            var script =
                $"Wait-Process -Id {pid} -Timeout 30 -ErrorAction SilentlyContinue; " +
                "Start-Sleep -Milliseconds 300; " +
                $"{command}; " +
                "Write-Host ''; Read-Host 'Press Enter to close'";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,   // its own window
                Arguments = $"-NoProfile -Command \"{script}\"",
            };
            Process.Start(psi);
            Console.WriteLine($"{toolName}: the update opens in a new window and runs once this one (pid {pid}) exits.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{toolName}: could not launch the updater ({ex.Message}). Run it yourself:");
            Console.Error.WriteLine($"  {command}");
            return 1;
        }
    }
}
