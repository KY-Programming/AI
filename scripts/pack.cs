// pack.cs — pack the KY.AI suite into NuGet packages.
//
//   dotnet run scripts/pack.cs        (or:  scripts\pack.cmd)
//
// Runs `dotnet pack -c Release` for Serve, Ng and Net, writing .nupkg files
// into artifacts\. Pack compiles in Release itself, so there's no separate
// build step. The two leaf projects pack as .NET global tools (PackAsTool);
// Serve packs as a library. Stale packages are cleared first so artifacts\
// only ever holds the current versions. Exits non-zero if any pack fails.
//
// Push the resulting packages to NuGet with scripts\publish.cmd; build a
// runnable local copy for testing with scripts\dist.cmd.

using System.Diagnostics;
using System.Runtime.CompilerServices;

var root = RepoRoot();
var outDir = Path.Combine(root, "artifacts");

ClearPackages(outDir);

string[] projects =
[
    Path.Combine(root, "src", "Serve", "KY.AI.Serve.csproj"),
    Path.Combine(root, "src", "Ng", "KY.AI.Ng.csproj"),
    Path.Combine(root, "src", "Net", "KY.AI.Net.csproj"),
];

foreach (var proj in projects)
{
    Console.WriteLine($"==> dotnet pack {Path.GetFileName(proj)} -c Release -o artifacts");
    int code = Run(root, "dotnet", "pack", proj, "-c", "Release", "-o", outDir, "--nologo");
    if (code != 0)
    {
        Console.Error.WriteLine($"pack FAILED for {Path.GetFileName(proj)} (exit {code})");
        return code;
    }
}

var packages = Directory.Exists(outDir)
    ? Directory.GetFiles(outDir, "*.nupkg").OrderBy(f => f).ToArray()
    : [];

// dotnet pack only warns (exit 0) when a project has packaging disabled, so
// confirm each project actually produced a package rather than trusting it.
string[] expected = ["KY.AI.Serve", "KY.AI.Ng", "KY.AI.Net"];
var missing = expected
    .Where(id => !packages.Any(p => Path.GetFileName(p).StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)))
    .ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"no package produced for: {string.Join(", ", missing)} " +
                            "(is <IsPackable> set? the Web SDK disables it by default)");
    return 1;
}

Console.WriteLine($"Packed {packages.Length} package(s) into {outDir}");
foreach (var p in packages) Console.WriteLine($"  + {Path.GetFileName(p)}");
return 0;

// Remove previously packed packages so a removed/renamed package can't linger
// and get pushed later. Leaves any other build intermediates alone.
static void ClearPackages(string outDir)
{
    if (!Directory.Exists(outDir)) return;
    foreach (var f in Directory.GetFiles(outDir, "*.nupkg")) File.Delete(f);
    foreach (var f in Directory.GetFiles(outDir, "*.snupkg")) File.Delete(f);
}

// Run a child process, inheriting our console streams so output appears live.
static int Run(string cwd, string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
    p.WaitForExit();
    return p.ExitCode;
}

// The repo root is this script's parent folder (scripts\..). CallerFilePath is
// the .cs path at compile time — the file-based-app equivalent of $PSScriptRoot.
static string RepoRoot([CallerFilePath] string scriptPath = "")
{
    var dir = Path.GetDirectoryName(scriptPath)!;
    var root = Path.GetFullPath(Path.Combine(dir, ".."));
    if (!Directory.Exists(Path.Combine(root, "src")))
        throw new DirectoryNotFoundException($"repo root not found above '{scriptPath}' (no src\\ folder)");
    return root;
}
