// dist.cs — publish the KY.AI tools into the shared dist\ folder for local testing.
//
//   dotnet run scripts/dist.cs        (or:  scripts\dist.cmd)
//
// Builds a runnable, framework-dependent copy of the tools into dist\ so you
// can put that one folder on PATH and exercise ky-ai-ng / ky-ai-dotnet /
// ky-ai-terminal locally without installing them from NuGet. They share the
// output folder, so the Serve DLL lands once. dist\ is cleared first (publish
// never prunes). Needs the .NET 10 runtime to run the result.
//
// Shipping the suite to NuGet is a separate flow: scripts\pack.cmd then
// scripts\publish.cmd.

using System.Diagnostics;
using System.Runtime.CompilerServices;

var root = RepoRoot();
var dist = Path.Combine(root, "dist");

ClearDist(dist);

string[] projects =
[
    Path.Combine(root, "src", "Ng", "KY.AI.Ng.csproj"),
    Path.Combine(root, "src", "Net", "KY.AI.Net.csproj"),
    Path.Combine(root, "src", "Browser", "KY.AI.Browser.csproj"),
    Path.Combine(root, "src", "Terminal", "KY.AI.Terminal.csproj"),
];

foreach (var proj in projects)
{
    Console.WriteLine($"==> dotnet publish {Path.GetFileName(proj)} -c Release -o dist");
    int code = Run(root, "dotnet", "publish", proj, "-c", "Release", "-o", dist, "--nologo");
    if (code != 0)
    {
        Console.Error.WriteLine($"publish FAILED for {Path.GetFileName(proj)} (exit {code})");
        return code;
    }
}

// Verify the aggregated output is what PATH expects.
string[] expected = ["ky-ai-ng.exe", "ky-ai-dotnet.exe", "ky-ai-terminal.exe", "ky-ai-browser.exe", "KY.AI.Serve.dll"];
var missing = expected.Where(f => !File.Exists(Path.Combine(dist, f))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"published, but missing from dist\\: {string.Join(", ", missing)}");
    return 1;
}

Console.WriteLine($"Published to {dist}");
foreach (var f in expected) Console.WriteLine($"  + {f}");
return 0;

// Delete dist\ so removed dependencies don't linger. A running hub locks its
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
        Console.Error.WriteLine("a running hub may be locking a DLL — stop it (MCP 'shutdown' or " +
                                "POST http://127.0.0.1:<port>/shutdown) and retry.");
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
