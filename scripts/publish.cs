// publish.cs — push the packed NuGet packages from artifacts\ to a NuGet feed.
//
//   scripts\publish.cmd                         push artifacts\*.nupkg to nuget.org
//   scripts\publish.cmd --dry-run               list what would be pushed; push nothing
//   scripts\publish.cmd --source <url> --api-key <key>
//
// Run scripts\pack.cmd first to produce the packages. The API key comes from
// --api-key, or the NUGET_API_KEY environment variable. --skip-duplicate makes
// re-pushing an already-published version a no-op instead of an error.

using System.Diagnostics;
using System.Runtime.CompilerServices;

const string DefaultSource = "https://api.nuget.org/v3/index.json";

var root = RepoRoot();
var outDir = Path.Combine(root, "artifacts");

// ---- args ----
string source = DefaultSource;
string? apiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY");
bool dryRun = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--source" or "-s": source = NextArg(args, ref i); break;
        case "--api-key" or "-k": apiKey = NextArg(args, ref i); break;
        case "--dry-run" or "-n": dryRun = true; break;
        case "--help" or "-h": PrintUsage(); return 0;
        default:
            Console.Error.WriteLine($"unknown option '{args[i]}'");
            PrintUsage();
            return 2;
    }
}

// ---- collect packages ----
if (!Directory.Exists(outDir))
{
    Console.Error.WriteLine("no artifacts\\ folder — run scripts\\pack.cmd first.");
    return 1;
}
var packages = Directory.GetFiles(outDir, "*.nupkg").OrderBy(f => f).ToArray();
if (packages.Length == 0)
{
    Console.Error.WriteLine("no .nupkg in artifacts\\ — run scripts\\pack.cmd first.");
    return 1;
}

Console.WriteLine($"{(dryRun ? "[dry run] would push" : "Pushing")} {packages.Length} package(s) to {source}:");
foreach (var p in packages) Console.WriteLine($"  - {Path.GetFileName(p)}");

if (dryRun)
{
    Console.WriteLine("dry run — nothing pushed.");
    return 0;
}

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("no API key — set NUGET_API_KEY or pass --api-key <key>. (Nothing pushed.)");
    return 1;
}

// ---- push ----
foreach (var pkg in packages)
{
    Console.WriteLine($"==> dotnet nuget push {Path.GetFileName(pkg)}");
    int code = Run(root, "dotnet", "nuget", "push", pkg,
                   "--source", source, "--api-key", apiKey, "--skip-duplicate");
    if (code != 0)
    {
        Console.Error.WriteLine($"push FAILED for {Path.GetFileName(pkg)} (exit {code})");
        return code;
    }
}

Console.WriteLine("Done.");
return 0;

static string NextArg(string[] args, ref int i)
    => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {args[i]}");

static int Run(string cwd, string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
    p.WaitForExit();
    return p.ExitCode;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          dotnet run scripts/publish.cs -- [options]      (or: scripts\publish.cmd [options])

        Pushes artifacts\*.nupkg (produced by scripts\build.cmd) to a NuGet feed.

        Options:
          --source <url>    feed to push to (default: nuget.org)
          --api-key <key>   API key (default: NUGET_API_KEY env var)
          --dry-run, -n     list packages that would be pushed; push nothing
        """);
}

static string RepoRoot([CallerFilePath] string scriptPath = "")
{
    var dir = Path.GetDirectoryName(scriptPath)!;
    var root = Path.GetFullPath(Path.Combine(dir, ".."));
    if (!Directory.Exists(Path.Combine(root, "src")))
        throw new DirectoryNotFoundException($"repo root not found above '{scriptPath}' (no src\\ folder)");
    return root;
}
