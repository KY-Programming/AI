using System.Diagnostics;
using System.Text;

namespace KY.AI.Serve;

// One-shot tee: run a single command (e.g. `ng build`, `dotnet test`) to completion, mirroring
// its output to the console (raw) and to a full log file, and return its exit code. The child
// is put under a kill-on-close Job Object so Ctrl+C (or a hard kill) reaps the whole tree.
public static class OneShot
{
    public static int Run(string toolName, string fileName, IEnumerable<string> args, string logPath)
    {
        Cli.EnsureDir(logPath);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        StreamWriter log;
        try
        {
            log = new StreamWriter(
                new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                new UTF8Encoding(true)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{toolName}: cannot open log file '{logPath}': {ex.Message}");
            return 1;
        }

        var sync = new object();
        void Tee(string? line, TextWriter console)
        {
            if (line is null) return;
            lock (sync) { console.WriteLine(line); log.WriteLine(line); }
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
            var msg = $"{toolName}: failed to start '{fileName}': {ex.Message}";
            Console.Error.WriteLine(msg);
            log.WriteLine(msg);
            log.Dispose();
            return 1;
        }

        job.Assign(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        log.Dispose();
        return proc.ExitCode;
    }
}
