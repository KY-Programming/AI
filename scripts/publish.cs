// publish.cs — publish the packed artifacts to NuGet and npm.
//
//   scripts\publish.cmd                       push NuGet packages + npm packages
//   scripts\publish.cmd --dry-run             show/validate what would be pushed; push nothing
//   scripts\publish.cmd --skip-npm            NuGet only
//   scripts\publish.cmd --skip-nuget          npm only
//   scripts\publish.cmd --source <url> --api-key <key>
//
// Run scripts\pack.cmd first. NuGet: pushes artifacts\*.nupkg (key from --api-key, the
// NUGET_API_KEY env var, or an interactive hidden prompt; --skip-duplicate no-ops a re-push). npm: publishes each
// package under artifacts\npm\ with `npm publish --access public` (auth via your npm login /
// .npmrc), platform packages before the main @ky-ai/ng so its optional deps already exist.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

const string DefaultSource = "https://api.nuget.org/v3/index.json";

var root = RepoRoot();
var artifacts = Path.Combine(root, "artifacts");

string source = DefaultSource;
string? apiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY");
bool dryRun = false, skipNuget = false, skipNpm = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--source" or "-s": source = NextArg(args, ref i); break;
        case "--api-key" or "-k": apiKey = NextArg(args, ref i); break;
        case "--dry-run" or "-n": dryRun = true; break;
        case "--skip-nuget": skipNuget = true; break;
        case "--skip-npm": skipNpm = true; break;
        case "--help" or "-h": PrintUsage(); return 0;
        default:
            Console.Error.WriteLine($"unknown option '{args[i]}'");
            PrintUsage();
            return 2;
    }
}

if (skipNuget && skipNpm) { Console.Error.WriteLine("nothing to do: --skip-nuget and --skip-npm together."); return 2; }
if (!Directory.Exists(artifacts)) { Console.Error.WriteLine("no artifacts\\ folder — run scripts\\pack.cmd first."); return 1; }

if (!skipNuget) { var rc = PushNuget(root, artifacts, source, apiKey, dryRun); if (rc != 0) return rc; }
if (!skipNpm) { var rc = PublishNpm(root, artifacts, dryRun); if (rc != 0) return rc; }

Console.WriteLine(dryRun ? "Dry run complete — nothing published." : "Publish complete.");
return 0;

// ---- NuGet ------------------------------------------------------------------

static int PushNuget(string root, string artifacts, string source, string? apiKey, bool dryRun)
{
    var packages = Directory.GetFiles(artifacts, "*.nupkg").OrderBy(f => f).ToArray();
    if (packages.Length == 0) { Console.Error.WriteLine("no .nupkg in artifacts\\ — run scripts\\pack.cmd first."); return 1; }

    Console.WriteLine($"{(dryRun ? "[dry run] would push" : "Pushing")} {packages.Length} NuGet package(s) to {source}:");
    foreach (var p in packages) Console.WriteLine($"  - {Path.GetFileName(p)}");
    if (dryRun) return 0;

    // No key from --api-key or NUGET_API_KEY — prompt for it (hidden input).
    if (string.IsNullOrWhiteSpace(apiKey))
        apiKey = ReadSecret($"NuGet API key for {source} (input hidden): ");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.Error.WriteLine("no API key entered — nothing pushed.");
        return 1;
    }

    foreach (var pkg in packages)
    {
        Console.WriteLine($"==> dotnet nuget push {Path.GetFileName(pkg)}");
        int code = Run(root, "dotnet", "nuget", "push", pkg,
                       "--source", source, "--api-key", apiKey, "--skip-duplicate");
        if (code != 0) { Console.Error.WriteLine($"push FAILED for {Path.GetFileName(pkg)} (exit {code})"); return code; }
    }
    return 0;
}

// ---- npm --------------------------------------------------------------------

static int PublishNpm(string root, string artifacts, bool dryRun)
{
    var npmDir = Path.Combine(artifacts, "npm");
    if (!Directory.Exists(npmDir)) { Console.Error.WriteLine("no artifacts\\npm — run scripts\\pack.cmd first (without --skip-npm)."); return 1; }

    // Platform packages first, the main @ky-ai/ng last (so its optional deps already exist).
    var packages = Directory.GetDirectories(npmDir)
        .OrderBy(d => Path.GetFileName(d) == "ng" ? 1 : 0)
        .ThenBy(d => d, StringComparer.Ordinal)
        .ToArray();
    if (packages.Length == 0) { Console.Error.WriteLine("no packages under artifacts\\npm."); return 1; }

    Console.WriteLine($"{(dryRun ? "[dry run] would publish" : "Publishing")} {packages.Length} npm package(s):");
    foreach (var d in packages) Console.WriteLine($"  - {Path.GetFileName(d)}");

    foreach (var pkgDir in packages)
    {
        Console.WriteLine($"==> npm publish {Path.GetFileName(pkgDir)} --access public{(dryRun ? " --dry-run" : "")}");
        int code = dryRun
            ? RunNpm(root, "publish", pkgDir, "--access", "public", "--dry-run")
            : RunNpm(root, "publish", pkgDir, "--access", "public");
        if (code != 0) { Console.Error.WriteLine($"npm publish FAILED for {Path.GetFileName(pkgDir)} (exit {code})"); return code; }
    }
    return 0;
}

// ---- helpers ----------------------------------------------------------------

static string NextArg(string[] args, ref int i)
    => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {args[i]}");

// Read a secret without echoing it. Falls back to a plain read when stdin is redirected
// (piped/non-interactive), where key-by-key masking isn't possible.
static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected)
        return (Console.ReadLine() ?? "").Trim();

    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
    }
    return sb.ToString().Trim();
}

static int Run(string cwd, string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
    p.WaitForExit();
    return p.ExitCode;
}

// npm is a .cmd on Windows, which CreateProcess can't launch directly — go through cmd /c there.
static int RunNpm(string cwd, params string[] npmArgs)
{
    var psi = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = cwd };
    if (OperatingSystem.IsWindows())
    {
        psi.FileName = "cmd.exe";
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("npm");
    }
    else
    {
        psi.FileName = "npm";
    }
    foreach (var a in npmArgs) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start npm");
    p.WaitForExit();
    return p.ExitCode;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          dotnet run scripts/publish.cs -- [options]      (or: scripts\publish.cmd [options])

        Publishes artifacts\ (produced by scripts\pack.cmd) to NuGet and npm.

        Options:
          --source <url>    NuGet feed (default: nuget.org)
          --api-key <key>   NuGet API key (else NUGET_API_KEY env var, else prompted)
          --skip-nuget      publish npm only
          --skip-npm        publish NuGet only
          --dry-run, -n     validate/list only; push nothing (npm uses `npm publish --dry-run`)
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
