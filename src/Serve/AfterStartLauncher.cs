using System.Diagnostics;

namespace KY.AI.Serve;

// Launches a one-off command once the dev server has finished its first build (i.e. is "up").
// Fills the gap left by PowerShell having no `serve & sleep 1 && other` idiom — but instead of a
// fixed sleep it waits on the real ready signal (the first settled build), so the follow-up command
// (e.g. `ky-ai-browser -y`) only runs after the server is actually serving.
//
// The launched process inherits this console (its output is visible inline) and is reaped — whole
// tree — when the supervisor stops, so it never outlives the server it was paired with. It's put in
// a KILL_ON_JOB_CLOSE JobObject (like the dev-server child), so the OS terminates it whenever the
// supervisor dies — graceful Ctrl+C / shutdown OR a hard kill (Rider Stop) that runs no handlers.
// Without the job a hard-killed supervisor would orphan the started tool (e.g. ky-ai-browser).
internal sealed class AfterStartLauncher : IDisposable
{
    private readonly object _sync = new();
    private readonly JobObject _job = new();
    private Process? _proc;
    private bool _disposed;

    public async Task LaunchAfterBuildAsync(SupervisorConfig cfg, DevServer server, IReadOnlyList<string> command, CancellationToken ct)
    {
        // Wait for the first build to settle — success or failure both mean the dev server is up and
        // serving (it keeps the page served even with compile errors). A timeout still launches: the
        // server is usually listening by then, just slow or noisy.
        BuildResult build;
        try { build = await server.WaitForBuildAsync(cfg.DefaultTimeoutMs, cfg.DefaultQuietMs); }
        catch { return; }
        if (ct.IsCancellationRequested) return;
        lock (_sync) if (_disposed) return;

        var status = build.TimedOut ? "first build did not settle in time" : $"first build {build.Status}";
        Console.WriteLine($"{cfg.ToolName} · --after-start: {status} — launching: {string.Join(' ', command)}");

        try
        {
            var (fileName, args) = BuildCommand(command);
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,           // inherit this console so the command's output is visible
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            lock (_sync)
            {
                // Bail if it didn't start, or the supervisor shut down between the await and here.
                if (_disposed || p is null) { TryKill(p); return; }
                _proc = p;
                // Assign the cmd wrapper to the job before it spawns the tool, so the tool inherits the
                // job too — the OS then reaps the whole tree on supervisor death, however it dies.
                _job.Assign(p);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{cfg.ToolName} · --after-start failed to launch '{string.Join(' ', command)}': {ex.Message}");
        }
    }

    // Resolve the command cross-platform. On Windows route through cmd so PATH lookup finds the
    // `.cmd`/`.exe` shims that npm- and dotnet-tool-installed commands (ky-ai-browser, ...) use —
    // CreateProcess can't exec a .cmd directly. On POSIX the command is on PATH as-is.
    private static (string FileName, IReadOnlyList<string> Args) BuildCommand(IReadOnlyList<string> command)
    {
        if (OperatingSystem.IsWindows())
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            var shell = string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec;
            var args = new List<string> { "/c" };
            args.AddRange(command);
            return (shell, args);
        }
        return (command[0], command.Skip(1).ToList());
    }

    public void Dispose()
    {
        Process? p;
        bool already;
        lock (_sync) { already = _disposed; _disposed = true; p = _proc; _proc = null; }
        if (already) return;        // ApplicationStopping disposes us, then `using` would again
        TryKill(p);                 // immediate graceful kill (and the only reaper on POSIX)
        _job.Dispose();             // closing the job handle KILL_ON_JOB_CLOSE-reaps any survivor
    }

    private static void TryKill(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { p.Dispose(); } catch { /* ignore */ }
    }
}
