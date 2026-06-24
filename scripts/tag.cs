// tag.cs — push the current branch, then create git tags at HEAD from each project's <Version> and
// push them to origin.
//
//   scripts\tag.cmd                 push the branch, then create an annotated tag per project from
//                                   its csproj <Version> and push every target tag (tag.cmd always --push)
//   scripts\tag.cmd --dry-run       show what would be pushed/tagged; change nothing
//   scripts\tag.cmd --force         move tags that already exist (git tag -f; push --force)
//
// With --push, the current branch is pushed FIRST — so the tags land on a commit that's actually on
// origin (e.g. a `bump` commit that wasn't pushed) — then ALL target tags are pushed (idempotent,
// so tags from an earlier run that never got pushed still make it up). Run `dotnet run scripts/tag.cs`
// directly (no --push) to tag locally without any push.
//
// Mapping (project -> tag):  KY.AI.Ng 22.1.0 -> ng-v22.1.0
//                            KY.AI.Net 10.1.0 -> dotnet-v10.1.0
//                            KY.AI.Serve 1.1.0 -> serve-v1.1.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

// csproj (relative to repo root) -> tag prefix. Add Terminal here when it ships.
var projects = new (string Csproj, string Prefix)[]
{
    ("src/Ng/KY.AI.Ng.csproj",       "ng"),      // ky-ai-ng
    ("src/Net/KY.AI.Net.csproj",     "dotnet"),  // ky-ai-dotnet
    ("src/Serve/KY.AI.Serve.csproj", "serve"),   // KY.AI.Serve — the shared engine
};

bool dryRun = false, push = false, force = false;
foreach (var a in args)
{
    switch (a.ToLowerInvariant())
    {
        case "--dry-run" or "-n": dryRun = true; break;
        case "--push" or "-p": push = true; break;
        case "--force" or "-f": force = true; break;
        case "--help" or "-h": PrintUsage(); return 0;
        default: Console.Error.WriteLine($"unknown option '{a}'"); PrintUsage(); return 2;
    }
}

var root = RepoRoot();

if (Capture(root, "git", "rev-parse", "--is-inside-work-tree").Code != 0)
{
    Console.Error.WriteLine($"not a git repository: {root}");
    return 1;
}

// Release tags usually mark a committed state — warn (don't block) on a dirty tree.
if (Capture(root, "git", "status", "--porcelain").Out.Trim().Length > 0)
    Console.WriteLine("warning: working tree has uncommitted changes — tags will point at the current HEAD.\n");

// Build the plan: a version + tag for each project.
var plan = new List<(string Project, string Version, string Tag)>();
foreach (var (csproj, prefix) in projects)
{
    var path = Path.Combine(root, csproj.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(path)) { Console.Error.WriteLine($"missing project: {csproj}"); return 1; }

    var version = ReadVersion(path);
    if (string.IsNullOrWhiteSpace(version)) { Console.Error.WriteLine($"no <Version> element in {csproj}"); return 1; }

    plan.Add((Path.GetFileNameWithoutExtension(path), version, $"{prefix}-v{version}"));
}

Console.WriteLine($"{(dryRun ? "[dry run] " : "")}tags at HEAD:");
foreach (var (project, version, tag) in plan)
    Console.WriteLine($"  {project,-13} {version,-10} -> {tag}");
Console.WriteLine();

// Push the current branch FIRST, so the tags land on a commit that's actually on origin — e.g. a
// `bump` commit that wasn't pushed. Only when pushing; skipped for a dry run.
if (push)
{
    if (dryRun)
        Console.WriteLine("[dry run] would push the current branch to origin before tagging.\n");
    else
    {
        Console.WriteLine("pushing current branch to origin (so tags land on a pushed commit)...");
        if (Run(root, "git", "push", "origin", "HEAD") != 0)
        {
            Console.Error.WriteLine("git push (branch) failed — reconcile with origin, then re-run. No tags created.");
            return 1;
        }
        Console.WriteLine();
    }
}

foreach (var (project, version, tag) in plan)
{
    var exists = Capture(root, "git", "rev-parse", "-q", "--verify", $"refs/tags/{tag}").Code == 0;
    if (exists && !force)
    {
        Console.WriteLine($"skip    {tag} (already exists — use --force to move it)");
        continue;
    }
    if (dryRun)
    {
        Console.WriteLine($"would {(exists ? "move  " : "create")} {tag}");
        continue;
    }

    string[] tagArgs = force
        ? ["tag", "-f", "-a", tag, "-m", $"{project} {version}"]
        : ["tag", "-a", tag, "-m", $"{project} {version}"];
    if (Run(root, "git", tagArgs) != 0) { Console.Error.WriteLine($"failed to create tag {tag}"); return 1; }
    Console.WriteLine($"{(exists ? "moved  " : "created")} {tag}");
}

// Push every target tag to origin (idempotent — already-present tags are a no-op), so tags created
// in an earlier run that were never pushed still make it up.
if (push)
{
    var tags = plan.Select(p => p.Tag).ToList();
    Console.WriteLine();
    Console.WriteLine($"{(dryRun ? "[dry run] would push" : "Pushing")} {tags.Count} tag(s) to origin:");
    foreach (var t in tags) Console.WriteLine($"  - {t}");
    if (!dryRun)
    {
        var pushArgs = new List<string> { "push" };
        if (force) pushArgs.Add("--force");
        pushArgs.Add("origin");
        pushArgs.AddRange(tags);
        if (Run(root, "git", pushArgs.ToArray()) != 0) { Console.Error.WriteLine("push failed"); return 1; }
    }
}

Console.WriteLine();
Console.WriteLine(dryRun ? "Dry run complete — no tags created." : "Done.");
return 0;

// ---- helpers ----------------------------------------------------------------

static string? ReadVersion(string csprojPath)
    => XDocument.Load(csprojPath).Descendants()
        .FirstOrDefault(e => e.Name.LocalName == "Version")?.Value.Trim();

static int Run(string cwd, string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
    p.WaitForExit();
    return p.ExitCode;
}

static (int Code, string Out) Capture(string cwd, string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe)
    {
        UseShellExecute = false,
        WorkingDirectory = cwd,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
    var stdout = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, stdout);
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          dotnet run scripts/tag.cs -- [options]      (or: scripts\tag.cmd [options])

        Creates an annotated git tag at HEAD for each project, from its csproj <Version>:
          KY.AI.Ng    -> ng-v<version>
          KY.AI.Net   -> dotnet-v<version>
          KY.AI.Serve -> serve-v<version>

        Options:
          --dry-run, -n   show what would be pushed/tagged; change nothing
          --push, -p      push the current branch first, then push every target tag to origin
                          (idempotent; uploads earlier untagged-but-created tags)
          --force, -f     move tags that already exist (git tag -f; push --force)

        (scripts\tag.cmd always passes --push.)
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
