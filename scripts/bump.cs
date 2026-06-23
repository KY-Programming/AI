// bump.cs — bump a project's <Version> and keep README's table in sync.
//
//   Serve (plain SemVer):   dotnet run scripts/bump.cs -- Serve --part minor
//   Ng / Net leaf tools:    dotnet run scripts/bump.cs -- Ng    --part patch
//                           dotnet run scripts/bump.cs -- Net   --set-major 11
//   Preview only:           ... --dry-run
//   Interactive (no args):  dotnet run scripts/bump.cs        — or simply:  scripts\bump.cmd
//                           prompts for the project and how to bump it.
//   (scripts\bump.cmd replaces "dotnet run scripts/bump.cs --"; pass args for scripted use.)
//
// Versioning scheme — independent per project, no central props:
//   * KY.AI.Serve is plain SemVer: --part major|minor|patch increments it.
//   * KY.AI.Ng / KY.AI.Net are leaf tools whose MAJOR is pinned to the
//     framework they target (Ng major = Angular major; Net major = the .NET
//     *SDK* major whose build output it parses — not its own TFM). So their
//     major is *set* with --set-major <n> (which resets minor/patch to 0),
//     while --part minor|patch increments within the pinned major.
//
// After bumping, the matching row of README's "Supported versions" table is
// updated so the documented "N.x" line (and the framework it names) stays in
// sync with the csproj.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

try
{
    return Bump(args);
}
catch (BumpError ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine();
    PrintUsage();
    return 2;
}

static int Bump(string[] args)
{
    var root = RepoRoot();

    bool wantInteractive = args.Length == 0 || args.Contains("-i") || args.Contains("--interactive");
    BumpRequest? req = wantInteractive ? Interactive(root) : ParseArgs(args);
    if (req is null)
    {
        Console.WriteLine("Cancelled — nothing written.");
        return 0;
    }

    return Apply(root, req);
}

static int Apply(string root, BumpRequest req)
{
    var csprojPath = Path.Combine(root, "src", req.Project, $"KY.AI.{req.Project}.csproj");
    if (!File.Exists(csprojPath))
        throw new BumpError($"project file not found: {csprojPath}");

    var csproj = File.ReadAllText(csprojPath);
    var current = ReadVersion(csproj, csprojPath);
    var next = Compute(req.Project, current, req.Part, req.SetMajor);

    Console.WriteLine($"KY.AI.{req.Project}: {current} -> {next}{(req.DryRun ? "   (dry run)" : "")}");

    // --- csproj ---
    var newCsproj = ReplaceVersion(csproj, next);
    WriteOrPreview(csprojPath, csproj, newCsproj, root, req.DryRun, "  <Version>");

    // --- README "Supported versions" table ---
    var readmePath = Path.Combine(root, "README.md");
    if (File.Exists(readmePath))
    {
        var readme = File.ReadAllText(readmePath);
        var newReadme = UpdateReadmeRow(readme, $"KY.AI.{req.Project}", next.Major);
        WriteOrPreview(readmePath, readme, newReadme, root, req.DryRun,
                       $"  Supported versions: KY.AI.{req.Project} {next.Major}.x");
    }
    else
    {
        Console.Error.WriteLine("warning: README.md not found — table not synced.");
    }

    return 0;
}

// ---- argument parsing ---------------------------------------------------

static BumpRequest ParseArgs(string[] args)
{
    string? project = null, part = null, setMajorRaw = null;
    bool dryRun = false;

    for (int i = 0; i < args.Length; i++)
    {
        var a = args[i];
        switch (a.ToLowerInvariant())
        {
            case "-project" or "--project":      project = Next(args, ref i, a); break;
            case "-part" or "--part" or "-p":    part = Next(args, ref i, a); break;
            case "-setmajor" or "--set-major" or "--setmajor":
                                                 setMajorRaw = Next(args, ref i, a); break;
            case "-n" or "--dry-run" or "--dryrun": dryRun = true; break;
            case "-i" or "--interactive": break; // decided before ParseArgs; ignore here
            case "-h" or "--help": PrintUsage(); Environment.Exit(0); break;
            default:
                if (a.StartsWith('-')) throw new BumpError($"unknown option '{a}'");
                if (project is not null) throw new BumpError($"unexpected argument '{a}'");
                project = a;
                break;
        }
    }

    if (project is null) throw new BumpError("no project given (Serve, Ng or Net)");
    project = project.ToLowerInvariant() switch
    {
        "serve" => "Serve",
        "ng" => "Ng",
        "net" => "Net",
        _ => throw new BumpError($"project must be Serve, Ng or Net (got '{project}')"),
    };

    int? setMajor = null;
    if (setMajorRaw is not null)
    {
        if (!int.TryParse(setMajorRaw, out var m) || m < 0)
            throw new BumpError($"--set-major needs a non-negative integer (got '{setMajorRaw}')");
        setMajor = m;
    }

    return new BumpRequest(project, part, setMajor, dryRun);
}

static string Next(string[] args, ref int i, string flag)
    => i + 1 < args.Length ? args[++i] : throw new BumpError($"missing value for {flag}");

// ---- interactive mode ---------------------------------------------------

// Default path when bump runs with no arguments (e.g. a bare scripts\bump.cmd):
// pick a project, choose how to bump it, preview the result, and confirm.
// Returns null if the user cancels or there's nothing to do.
static BumpRequest? Interactive(string root)
{
    Console.WriteLine("Interactive version bump — pick a project, then how to bump it.");
    Console.WriteLine();

    string[] projects = ["Serve", "Ng", "Net"];
    var labels = new string[projects.Length];
    for (int i = 0; i < projects.Length; i++)
    {
        var v = TryCurrentVersion(root, projects[i]);
        labels[i] = $"KY.AI.{projects[i],-5}  (current {(v?.ToString() ?? "?")})";
    }

    var project = projects[Choose("Which project?", labels)];
    var cur = TryCurrentVersion(root, project)
              ?? throw new BumpError($"could not read current <Version> of {project}");

    string? part = null;
    int? setMajor = null;

    if (project == "Serve")
    {
        // Plain SemVer — any part may increment.
        int c = Choose($"Bump which part of KY.AI.Serve? (current {cur})",
        [
            $"major   {cur} -> {new Ver(cur.Major + 1, 0, 0)}",
            $"minor   {cur} -> {new Ver(cur.Major, cur.Minor + 1, 0)}",
            $"patch   {cur} -> {new Ver(cur.Major, cur.Minor, cur.Patch + 1)}",
        ]);
        part = c switch { 0 => "major", 1 => "minor", _ => "patch" };
    }
    else
    {
        // Leaf tool — major is pinned to the framework, so it's *set*, not incremented.
        var framework = project == "Ng" ? "Angular" : ".NET SDK";
        int c = Choose($"Bump KY.AI.{project}? (current {cur}; major is pinned to {framework})",
        [
            $"set major  -> <n>.0.0   (match the {framework} major)",
            $"minor      {cur} -> {new Ver(cur.Major, cur.Minor + 1, 0)}",
            $"patch      {cur} -> {new Ver(cur.Major, cur.Minor, cur.Patch + 1)}",
        ]);
        if (c == 0)
            setMajor = AskInt($"New major ({framework} major)", cur.Major);
        else
            part = c == 1 ? "minor" : "patch";
    }

    var next = Compute(project, cur, part, setMajor);
    if (next == cur)
    {
        Console.WriteLine($"\nKY.AI.{project} is already {cur} — nothing to do.");
        return null;
    }

    Console.WriteLine();
    if (!Confirm($"Apply  KY.AI.{project}: {cur} -> {next}  (updates csproj + README)?"))
        return null;

    return new BumpRequest(project, part, setMajor, DryRun: false);
}

static Ver? TryCurrentVersion(string root, string project)
{
    var path = Path.Combine(root, "src", project, $"KY.AI.{project}.csproj");
    if (!File.Exists(path)) return null;
    var m = Regex.Match(File.ReadAllText(path), VersionRx());
    return m.Success ? Ver.Parse(m.Groups[1].Value) : null;
}

// Print a numbered menu and return the chosen zero-based index.
static int Choose(string title, string[] options)
{
    while (true)
    {
        Console.WriteLine(title);
        for (int i = 0; i < options.Length; i++)
            Console.WriteLine($"  {i + 1}) {options[i]}");
        Console.Write("> ");

        var line = Console.ReadLine();
        if (line is null) throw new BumpError("no input on stdin — pass arguments instead of running interactively");
        if (int.TryParse(line.Trim(), out var n) && n >= 1 && n <= options.Length)
            return n - 1;

        Console.WriteLine($"  enter a number between 1 and {options.Length}.");
        Console.WriteLine();
    }
}

// Prompt for a non-negative integer; empty input keeps the fallback.
static int AskInt(string label, int fallback)
{
    while (true)
    {
        Console.Write($"{label} [{fallback}]: ");
        var line = Console.ReadLine();
        if (line is null) throw new BumpError("no input on stdin");
        line = line.Trim();
        if (line.Length == 0) return fallback;
        if (int.TryParse(line, out var n) && n >= 0) return n;
        Console.WriteLine("  enter a non-negative integer.");
    }
}

static bool Confirm(string question)
{
    Console.Write($"{question} [y/N] ");
    var line = Console.ReadLine()?.Trim();
    return string.Equals(line, "y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(line, "yes", StringComparison.OrdinalIgnoreCase);
}

// ---- version maths ------------------------------------------------------

static Ver Compute(string project, Ver cur, string? part, int? setMajor)
{
    part = part?.ToLowerInvariant();

    if (project == "Serve")
    {
        if (setMajor is not null)
            throw new BumpError("--set-major is for the leaf tools (Ng, Net); Serve uses --part major|minor|patch");
        return part switch
        {
            "major" => new Ver(cur.Major + 1, 0, 0),
            "minor" => new Ver(cur.Major, cur.Minor + 1, 0),
            "patch" => new Ver(cur.Major, cur.Minor, cur.Patch + 1),
            null => throw new BumpError("Serve needs --part major|minor|patch"),
            _ => throw new BumpError($"--part must be major, minor or patch (got '{part}')"),
        };
    }

    // Leaf tools (Ng, Net): major is pinned to the framework, so it's *set*.
    if (setMajor is not null && part is not null)
        throw new BumpError("use either --set-major <n> or --part minor|patch, not both");

    if (setMajor is not null)
        return new Ver(setMajor.Value, 0, 0);

    return part switch
    {
        "minor" => new Ver(cur.Major, cur.Minor + 1, 0),
        "patch" => new Ver(cur.Major, cur.Minor, cur.Patch + 1),
        "major" => throw new BumpError($"{project}'s major is pinned to its framework — set it with --set-major <n>"),
        null => throw new BumpError($"{project} needs --set-major <n> or --part minor|patch"),
        _ => throw new BumpError($"--part must be minor or patch for leaf tools (got '{part}')"),
    };
}

// ---- csproj <Version> ---------------------------------------------------

// One <Version>…</Version> element per leaf csproj; PackageReference uses a
// Version="…" attribute, which this element pattern deliberately doesn't match.
static string VersionRx() => @"<Version>\s*([^<\s]+)\s*</Version>";

static Ver ReadVersion(string csproj, string path)
{
    var matches = Regex.Matches(csproj, VersionRx());
    if (matches.Count == 0) throw new BumpError($"no <Version> element in {path}");
    if (matches.Count > 1) throw new BumpError($"multiple <Version> elements in {path}");
    return Ver.Parse(matches[0].Groups[1].Value);
}

static string ReplaceVersion(string csproj, Ver next)
    => Regex.Replace(csproj, VersionRx(), $"<Version>{next}</Version>");

// ---- README table -------------------------------------------------------

// Update the one "Supported versions" row for this tool: its `N.x` version
// line, plus the framework number the leaf rows name (Angular N / .NET N).
static string UpdateReadmeRow(string readme, string toolName, int newMajor)
{
    var nl = readme.Contains("\r\n") ? "\r\n" : "\n";
    var lines = readme.Split(nl);
    var token = $"`{toolName}`";

    int row = -1;
    for (int i = 0; i < lines.Length; i++)
    {
        if (lines[i].Contains(token) && Regex.IsMatch(lines[i], @"\b\d+\.x\b"))
        {
            if (row >= 0) throw new BumpError($"more than one '{toolName}' version row in README");
            row = i;
        }
    }
    if (row < 0)
    {
        Console.Error.WriteLine($"warning: no '{toolName}' version row in README — table not synced.");
        return readme;
    }

    var updated = Regex.Replace(lines[row], @"\b\d+\.x\b", $"{newMajor}.x");
    if (toolName.EndsWith(".Ng"))
        updated = Regex.Replace(updated, @"Angular\s+\d+", $"Angular {newMajor}");
    else if (toolName.EndsWith(".Net"))
        updated = Regex.Replace(updated, @"\.NET\s+\d+", $".NET {newMajor}");

    lines[row] = updated;
    return string.Join(nl, lines);
}

// ---- shared I/O ---------------------------------------------------------

static void WriteOrPreview(string path, string before, string after, string root, bool dryRun, string label)
{
    var rel = Path.GetRelativePath(root, path);
    if (before == after)
    {
        Console.WriteLine($"  {rel,-28} unchanged");
        return;
    }
    if (dryRun)
    {
        Console.WriteLine($"  {rel,-28} would change ({label.Trim()})");
        return;
    }
    File.WriteAllText(path, after);
    Console.WriteLine($"  {rel,-28} updated");
}

static string RepoRoot([CallerFilePath] string scriptPath = "")
{
    var dir = Path.GetDirectoryName(scriptPath)!;
    var root = Path.GetFullPath(Path.Combine(dir, ".."));
    if (!Directory.Exists(Path.Combine(root, "src")))
        throw new BumpError($"repo root not found above '{scriptPath}' (no src\\ folder)");
    return root;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          dotnet run scripts/bump.cs                       (no args -> interactive: pick project + bump)
          dotnet run scripts/bump.cs -- <project> <bump> [--dry-run]

        Serve (plain SemVer):
          -- Serve --part major|minor|patch

        Ng / Net (leaf tools; major is pinned to the framework):
          -- Ng  --set-major <n>      set major to the Angular major (resets minor/patch)
          -- Ng  --part minor|patch   increment within the pinned major
          -- Net --set-major <n>      set major to the .NET SDK major
          -- Net --part minor|patch   increment within the pinned major

        Options:
          --dry-run, -n   show what would change without writing
        """);
}

readonly record struct Ver(int Major, int Minor, int Patch)
{
    public static Ver Parse(string s)
    {
        var p = s.Split('.');
        if (p.Length != 3 || !int.TryParse(p[0], out var a) || !int.TryParse(p[1], out var b) || !int.TryParse(p[2], out var c))
            throw new BumpError($"version '{s}' is not major.minor.patch");
        return new Ver(a, b, c);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

sealed class BumpError(string message) : Exception(message);

// A resolved bump request, produced either from CLI args or interactively.
sealed record BumpRequest(string Project, string? Part, int? SetMajor, bool DryRun);
