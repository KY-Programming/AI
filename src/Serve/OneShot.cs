using System.Diagnostics;
using System.Text;

namespace KY.AI.Serve;

// One-shot tee: run a single command (e.g. `ng build`, `dotnet test`) to completion, mirroring
// its output live to the console and — when --log-file is given — to a rolling log file (last N
// lines, default 200, 0 = unlimited). The child is put under a kill-on-close Job Object so Ctrl+C
// (or a hard kill) reaps the whole tree. Returns the child's exit code.
public static class OneShot
{
    public static int Run(string toolName, string fileName, IEnumerable<string> args,
        string? logPath, int logLines, string? serveHint = null, string? workingDir = null)
    {
        Warn(toolName, serveHint);

        if (logPath is not null) Cli.EnsureDir(logPath);
        var buffer = new RollingLog(null, logLines);  // in-memory; capped to logLines (0 = unlimited)

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sync = new object();
        void Tee(string? line, TextWriter console)
        {
            if (line is null) return;
            lock (sync) { console.WriteLine(line); buffer.Add(Ansi.Strip(line)); } // console raw, file ANSI-stripped
        }

        using var job = new JobObject();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => Tee(e.Data, Console.Out);
        proc.ErrorDataReceived += (_, e) => Tee(e.Data, Console.Error);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{toolName}: failed to start '{fileName}': {ex.Message}");
            return 1;
        }

        job.Assign(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        // Write the (capped) log once at the end — the live console already showed everything.
        if (logPath is not null) WriteLog(toolName, logPath, buffer.Tail(0));
        return proc.ExitCode;
    }

    // Yellow heads-up that this is an ephemeral, unsupervised run — with an optional `serve` nudge
    // up top for someone who reached one-shot by typing `run`/`watch`.
    private static void Warn(string toolName, string? serveHint)
    {
        try { Console.ForegroundColor = ConsoleColor.Yellow; } catch { }
        try
        {
            if (serveHint is not null) Console.Error.WriteLine(serveHint);
            Console.Error.WriteLine(
                $"⚠ {toolName}: one-shot — runs this command once and exits. It is not supervised and " +
                "never registers with the hub, so the agent can't see or control it; an auto-started " +
                "hub shuts itself down once nothing is left to supervise.");
        }
        finally { try { Console.ResetColor(); } catch { } }
    }

    private static void WriteLog(string toolName, string logPath, IReadOnlyList<string> lines)
    {
        try
        {
            var sb = new StringBuilder(lines.Count * 80);
            foreach (var l in lines) sb.Append(l).Append("\r\n");
            File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(true)); // UTF-8 BOM for Windows tools
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{toolName}: cannot write log file '{logPath}': {ex.Message}");
        }
    }
}
