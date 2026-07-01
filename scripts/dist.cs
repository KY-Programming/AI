// dist.cs — publish the KY.AI tools into the shared dist\ folder for local testing.
//
//   dotnet run scripts/dist.cs              (or:  scripts\dist.cmd)
//   dotnet run scripts/dist.cs -- --force   (don't prompt; stop/kill running tools automatically)
//   dotnet run scripts/dist.cs ky-ai-ng     (build just one tool; leave the others running)
//
// Builds a runnable, framework-dependent copy of the tools into dist\ so you
// can put that one folder on PATH and exercise ky-ai-ng / ky-ai-dotnet /
// ky-ai-terminal / ky-ai-browser locally without installing them from NuGet.
// They share the output folder, so the Serve DLL lands once. dist\ is cleared
// first (publish never prunes). Needs the .NET 10 runtime to run the result.
//
// Pass a single tool name (ky-ai-ng / ky-ai-dotnet / ky-ai-terminal /
// ky-ai-browser) to stop and rebuild only that tool while the others keep
// running. dist\ is NOT cleared in that mode (clearing would delete the other
// tools and the shared Serve DLL is locked by them); publish overwrites just
// that tool's files in place. If the shared Serve DLL changed it is locked by
// the still-running tools and publish will fail — do a full run to rebuild all.
//
// A running tool locks its exe/DLLs in dist\, so before clearing we look for
// running instances and (with consent — default yes) send each a graceful
// shutdown, then recheck and offer to kill any survivor. --force answers yes
// to both. Shutdown is asynchronous (the endpoint replies, then exits a beat
// later), so we trust the OS process list, not the shutdown response.
//
// Shipping the suite to NuGet is a separate flow: scripts\pack.cmd then
// scripts\publish.cmd.

using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;

var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
// Optional positional arg: a single tool name to stop+rebuild (everything else is an option flag).
var only = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

var root = RepoRoot();
var dist = Path.Combine(root, "dist");

// The tool suite: process name (no .exe) → shutdown port (the hub port, where one POST tears down the
// hub + all its supervisors/instances) → project → output exe.
(string Proc, int Port, string Project, string Exe)[] allTools =
[
    ("ky-ai-ng",       5101, Path.Combine(root, "src", "Ng",       "KY.AI.Ng.csproj"),       "ky-ai-ng.exe"),
    ("ky-ai-dotnet",   5102, Path.Combine(root, "src", "Net",      "KY.AI.Net.csproj"),      "ky-ai-dotnet.exe"),
    ("ky-ai-terminal", 5103, Path.Combine(root, "src", "Terminal", "KY.AI.Terminal.csproj"), "ky-ai-terminal.exe"),
    ("ky-ai-browser",  5104, Path.Combine(root, "src", "Browser",  "KY.AI.Browser.csproj"),  "ky-ai-browser.exe"),
    ("ky-ai-updater",  5105, Path.Combine(root, "src", "Updater",  "KY.AI.Updater.csproj"),  "ky-ai-updater.exe"),
];

// Narrow to the requested tool, or the whole suite when none was named.
var tools = allTools;
if (only is not null)
{
    tools = allTools.Where(t => t.Proc.Equals(only, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (tools.Length == 0)
    {
        Console.Error.WriteLine($"unknown tool '{only}'. Valid: {string.Join(", ", allTools.Select(t => t.Proc))}");
        return 1;
    }
    Console.WriteLine($"Single-tool build: {tools[0].Proc} (others keep running; dist\\ not cleared)");
}

await EnsureToolsStoppedAsync(tools.Select(t => (t.Proc, t.Port)).ToArray(), force);

// Full run wipes dist\ so removed deps don't linger; a single-tool run must NOT (it would delete the
// other, still-running tools) — its publish overwrites just that tool's files in place.
if (only is null)
    ClearDist(dist);

foreach (var (_, _, proj, _) in tools)
{
    Console.WriteLine($"==> dotnet publish {Path.GetFileName(proj)} -c Release -o dist");
    int code = Run(root, "dotnet", "publish", proj, "-c", "Release", "-o", dist, "--nologo");
    if (code != 0)
    {
        Console.Error.WriteLine($"publish FAILED for {Path.GetFileName(proj)} (exit {code})");
        return code;
    }
}

// Verify the output is what PATH expects: the built exe(s), plus the shared Serve DLL on a full run
// (a single-tool run leaves the already-present Serve DLL untouched, so don't demand a fresh one).
var expected = tools.Select(t => t.Exe).Concat(only is null ? ["KY.AI.Serve.dll"] : Array.Empty<string>()).ToArray();
var missing = expected.Where(f => !File.Exists(Path.Combine(dist, f))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"published, but missing from dist\\: {string.Join(", ", missing)}");
    return 1;
}

Console.WriteLine($"Published to {dist}");
foreach (var f in expected) Console.WriteLine($"  + {f}");
return 0;

// Stop any running instance of the tools so they don't lock dist\. First a graceful shutdown
// (consent, default yes), then — since shutdown is async — recheck the OS and offer to kill
// survivors (consent, default yes). --force answers yes to both.
static async Task EnsureToolsStoppedAsync((string Proc, int Port)[] tools, bool force)
{
    var running = FindRunning(tools);
    if (running.Count == 0) return;

    Console.WriteLine("Running KY.AI tools detected (they lock files in dist\\):");
    foreach (var r in running) Console.WriteLine($"  - {r.Name} (pid {r.Pid})");

    if (!Confirm("Send each a graceful shutdown?", force))
    {
        Console.Error.WriteLine("Skipped shutdown — publish will likely fail on locked files.");
        return;
    }

    // POST /shutdown to each distinct port that has a process. Hub ports cascade to supervisors.
    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        foreach (var port in running.Select(r => r.Port).Distinct())
        {
            try
            {
                using var resp = await http.PostAsync($"http://127.0.0.1:{port}/shutdown", null);
                // 404/405 = the listener has no /shutdown (an older build) — say so plainly.
                var label = resp.IsSuccessStatusCode ? "accepted"
                    : resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed
                        ? "not-supported (older build)"
                        : $"HTTP {(int)resp.StatusCode}";
                Console.WriteLine($"  shutdown → :{port} {label}");
            }
            catch (Exception ex)
            {
                // No listener (e.g. a --no-hub supervisor, or a custom port) — the kill step covers it.
                Console.WriteLine($"  shutdown → :{port} no response ({ex.GetType().Name}); will rely on kill");
            }
        }

    // Shutdown only schedules the exit, so poll the process list for the real outcome.
    var remaining = await WaitForExitAsync(tools, TimeSpan.FromSeconds(8));
    if (remaining.Count == 0)
    {
        Console.WriteLine("All tools stopped.");
        return;
    }

    Console.WriteLine("Still running after shutdown:");
    foreach (var r in remaining) Console.WriteLine($"  - {r.Name} (pid {r.Pid})");

    if (!Confirm("Kill them (and their child trees)?", force))
    {
        Console.Error.WriteLine("Left running — publish will likely fail on locked files.");
        return;
    }

    foreach (var r in remaining) TryKill(r.Pid);

    var stubborn = await WaitForExitAsync(tools, TimeSpan.FromSeconds(3));
    if (stubborn.Count > 0)
        Console.Error.WriteLine($"warning: still running: {string.Join(", ", stubborn.Select(s => $"{s.Name}({s.Pid})"))}");
}

// Live processes matching the tool names, tagged with that tool's shutdown port.
static List<(string Name, int Pid, int Port)> FindRunning((string Proc, int Port)[] tools)
{
    var list = new List<(string, int, int)>();
    foreach (var (proc, port) in tools)
        foreach (var p in Process.GetProcessesByName(proc))
        {
            try { if (!p.HasExited) list.Add((proc, p.Id, port)); }
            catch { /* exited between enumerate and check */ }
            finally { p.Dispose(); }
        }
    return list;
}

// Poll until nothing matches or the timeout elapses; return whatever is still running.
static async Task<List<(string Name, int Pid, int Port)>> WaitForExitAsync((string Proc, int Port)[] tools, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        var running = FindRunning(tools);
        if (running.Count == 0 || DateTime.UtcNow >= deadline) return running;
        await Task.Delay(300);
    }
}

static void TryKill(int pid)
{
    try
    {
        using var p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: true);
        p.WaitForExit(3000);
        Console.WriteLine($"  killed pid {pid}");
    }
    catch (Exception ex) { Console.Error.WriteLine($"  kill pid {pid} failed: {ex.Message}"); }
}

// Yes/no prompt, default yes (empty/EOF = yes). --force answers yes without prompting.
static bool Confirm(string question, bool force)
{
    if (force) { Console.WriteLine($"{question} [--force: yes]"); return true; }
    Console.Write($"{question} [Y/n] ");
    var ans = Console.ReadLine()?.Trim();
    return string.IsNullOrEmpty(ans) || ans.StartsWith("y", StringComparison.OrdinalIgnoreCase);
}

// Delete dist\ so removed dependencies don't linger. A running tool locks its
// DLL; if so, warn and continue — the publish below surfaces a clear error.
static void ClearDist(string dist)
{
    if (!Directory.Exists(dist)) return;
    try
    {
        Directory.Delete(dist, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"warning: could not fully clear {dist}: {ex.Message}");
        Console.Error.WriteLine("a running tool may be locking a DLL — stop it (re-run, or --force) and retry.");
    }
}

static int Run(string cwd, string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
    p.WaitForExit();
    return p.ExitCode;
}

static string RepoRoot([CallerFilePath] string scriptPath = "")
{
    var dir = Path.GetDirectoryName(scriptPath)!;
    var root = Path.GetFullPath(Path.Combine(dir, ".."));
    if (!Directory.Exists(Path.Combine(root, "src")))
        throw new DirectoryNotFoundException($"repo root not found above '{scriptPath}' (no src\\ folder)");
    return root;
}
