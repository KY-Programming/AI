// bump.cs — bump a project's <Version> and keep README's table in sync.
//
//   Serve/Browser (SemVer): dotnet run scripts/bump.cs -- Serve   --part minor
//                           dotnet run scripts/bump.cs -- Browser --part patch
//   Ng / Net leaf tools:    dotnet run scripts/bump.cs -- Ng    --part patch
//                           dotnet run scripts/bump.cs -- Net   --set-major 11
//   Preview only:           ... --dry-run
//   Interactive (no args):  dotnet run scripts/bump.cs        — or simply:  scripts\bump.cmd
//                           select one or more projects (arrows + space) and one bump part for all;
//                           Enter with nothing selected bumps the focused project.
//   (scripts\bump.cmd replaces "dotnet run scripts/bump.cs --"; pass args for scripted use.)
//
// Versioning scheme — independent per project, no central props:
//   * KY.AI.Serve, KY.AI.Browser, KY.AI.Terminal and KY.AI.Updater are plain SemVer: --part major|minor|patch increments them.
//   * KY.AI.Ng / KY.AI.Net are leaf tools whose MAJOR is pinned to the
//     framework they target (Ng major = Angular major; Net major = the .NET
//     *SDK* major whose build output it parses — not its own TFM). So their
//     major is *set* with --set-major <n> (which resets minor/patch to 0),
//     while --part minor|patch increments within the pinned major.
//
// After bumping, the matching row of README's "Supported versions" table is
// updated so the documented "N.x" line (and the framework it names) stays in
// sync with the csproj.

using System.Diagnostics;
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
    List<BumpRequest>? reqs = wantInteractive ? Interactive(root) : [ParseArgs(args)];
    if (reqs is null || reqs.Count == 0)
    {
        Console.WriteLine("Cancelled — nothing written.");
        return 0;
    }

    // CLI path: refuse to bump a dirty project (the interactive path checks right after the project
    // selection). Skipped for a dry run.
    if (!wantInteractive && !reqs.Any(r => r.DryRun) && !EnsureClean(root, reqs.Select(r => r.Project).Distinct()))
        return 1;

    var changed = new List<string>();
    for (int i = 0; i < reqs.Count; i++)
    {
        if (i > 0) Console.WriteLine();
        changed.AddRange(Apply(root, reqs[i]));
    }

    // After an interactive bump, offer to commit the version files (default yes).
    if (wantInteractive && changed.Count > 0)
        MaybeCommit(root, changed);

    return 0;
}

// Applies the bump; returns the files it actually changed (for the optional commit).
static List<string> Apply(string root, BumpRequest req)
{
    var changed = new List<string>();

    var csprojPath = Path.Combine(root, "src", req.Project, $"KY.AI.{req.Project}.csproj");
    if (!File.Exists(csprojPath))
        throw new BumpError($"project file not found: {csprojPath}");

    var csproj = File.ReadAllText(csprojPath);
    var current = ReadVersion(csproj, csprojPath);
    var next = Compute(req.Project, current, req.Part, req.SetMajor);

    Console.WriteLine($"KY.AI.{req.Project}: {current} -> {next}{(req.DryRun ? "   (dry run)" : "")}");

    // --- csproj ---
    var newCsproj = ReplaceVersion(csproj, next);
    if (WriteOrPreview(csprojPath, csproj, newCsproj, root, req.DryRun, "  <Version>")) changed.Add(csprojPath);

    // --- README "Supported versions" table ---
    var readmePath = Path.Combine(root, "README.md");
    if (File.Exists(readmePath))
    {
        var readme = File.ReadAllText(readmePath);
        var newReadme = UpdateReadmeRow(readme, $"KY.AI.{req.Project}", next.Major);
        if (WriteOrPreview(readmePath, readme, newReadme, root, req.DryRun,
                           $"  Supported versions: KY.AI.{req.Project} {next.Major}.x")) changed.Add(readmePath);
    }
    else
    {
        Console.Error.WriteLine("warning: README.md not found — table not synced.");
    }

    return changed;
}

// ---- commit -------------------------------------------------------------

// After an interactive bump, offer to commit just the version files (default yes). Commits only
// these paths, so any unrelated working-tree changes are left untouched.
static void MaybeCommit(string root, List<string> files)
{
    files = files.Distinct().ToList();
    Console.WriteLine();
    if (!ConfirmYes("Commit these changes?"))
    {
        Console.WriteLine("Left uncommitted.");
        return;
    }

    var args = new List<string> { "commit", "-m", "chore: prepare release", "--" };
    args.AddRange(files);
    if (Git(root, args.ToArray()) != 0)
        Console.Error.WriteLine("git commit failed — nothing committed.");
}

// Yes/No prompt that defaults to YES (empty input = yes; EOF = no).
static bool ConfirmYes(string question)
{
    Console.Write($"{question} [Y/n] ");
    var line = Console.ReadLine();
    if (line is null) return false;
    line = line.Trim();
    if (line.Length == 0) return true;
    return string.Equals(line, "y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(line, "yes", StringComparison.OrdinalIgnoreCase);
}

static int Git(string root, params string[] args)
{
    var psi = new ProcessStartInfo("git") { UseShellExecute = false, WorkingDirectory = root };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new BumpError("could not start git");
    p.WaitForExit();
    return p.ExitCode;
}

// Refuse to bump when a selected project's src\<Project> folder has uncommitted changes — the
// prepare-release commit/tag should reflect a clean project, and bump only commits the version
// files (not your other edits). Best-effort: never blocks if git is unavailable.
static bool EnsureClean(string root, IEnumerable<string> projectNames)
{
    try
    {
        var dirty = new List<string>();
        foreach (var project in projectNames)
        {
            var (code, outp) = GitCapture(root, "status", "--porcelain", "--", $"src/{project}");
            if (code != 0) continue;
            foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                dirty.Add(line.TrimEnd());
        }
        if (dirty.Count == 0) return true;

        Console.Error.WriteLine("Uncommitted changes in the selected project(s) — commit or stash first:");
        foreach (var d in dirty.Distinct()) Console.Error.WriteLine($"  {d}");
        return false;
    }
    catch { return true; }   // git missing — don't block the bump
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

    if (project is null) throw new BumpError("no project given (Serve, Ng, Net, Browser, Terminal or Updater)");
    project = project.ToLowerInvariant() switch
    {
        "serve" => "Serve",
        "ng" => "Ng",
        "net" => "Net",
        "browser" => "Browser",
        "terminal" => "Terminal",
        "updater" => "Updater",
        _ => throw new BumpError($"project must be Serve, Ng, Net, Browser, Terminal or Updater (got '{project}')"),
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
// select one or more projects (arrows + space), choose one bump part for all, preview, confirm.
// With nothing selected, Enter bumps the focused project. Returns null if cancelled / nothing to do.
static List<BumpRequest>? Interactive(string root)
{
    Console.WriteLine("Interactive version bump — select project(s), then one bump part applied to all.");
    Console.WriteLine();

    string[] projects = ["Serve", "Ng", "Net", "Browser", "Terminal", "Updater"];
    var curVers = projects.Select(p => TryCurrentVersion(root, p)).ToArray();
    // Pad the "(current X.Y.Z)" token to a common width so the commit counter lines up across rows.
    var verTokens = curVers.Select(v => $"(current {v?.ToString() ?? "?"})").ToArray();
    int verW = verTokens.Max(t => t.Length);

    var labels = new string[projects.Length];
    for (int i = 0; i < projects.Length; i++)
        labels[i] = $"KY.AI.{projects[i],-8}  {verTokens[i].PadRight(verW)}{CommitSuffix(root, projects[i], curVers[i])}";

    var picks = MultiSelect("Which project(s)?", labels);
    if (picks is null) return null;

    // Right after the selection: refuse if any picked project has uncommitted work.
    if (!EnsureClean(root, picks.Select(i => projects[i]).Distinct()))
        return null;

    Console.WriteLine();

    // Pick the bump part once, applied to every selected project (major resets minor+patch, minor
    // resets patch, revision = patch). For now only Serve's major is supported — on a major bump the
    // framework-pinned leaf tools (Ng/Net) are skipped (set their major via the CLI --set-major).
    string[] partKeys = ["patch", "minor", "major"];
    string[] partLabels =
    [
        "revision  (x.x.X)",
        "minor     (x.X.0)",
        "major     (X.0.0)",
    ];
    var part = partKeys[Choose(
        $"Bump which part for the {picks.Length} selected project{(picks.Length == 1 ? "" : "s")}?", partLabels)];

    var requests = new List<BumpRequest>();
    foreach (var idx in picks)
    {
        var project = projects[idx];

        if (part == "major" && project is not "Serve" and not "Browser" and not "Terminal" and not "Updater")
        {
            Console.WriteLine($"  KY.AI.{project} — major is framework-pinned; skipped (use --set-major).");
            continue;
        }

        var cur = TryCurrentVersion(root, project)
                  ?? throw new BumpError($"could not read current <Version> of {project}");

        if (Compute(project, cur, part, setMajor: null) == cur)
        {
            Console.WriteLine($"  KY.AI.{project} is already {cur} — skipping.");
            continue;
        }

        requests.Add(new BumpRequest(project, part, SetMajor: null, DryRun: false));
    }

    if (requests.Count == 0)
    {
        Console.WriteLine("Nothing to bump.");
        return null;
    }

    Console.WriteLine();   // Apply() prints each project's cur -> next and the files it changed.
    return requests;
}

// Arrow-key multi-select: ↑/↓ (or k/j) move focus, space toggles, enter confirms.
// Returns the selected indices; with none selected, returns just the focused one.
// Esc (or q) cancels and returns null. Needs a real terminal.
static int[]? MultiSelect(string title, string[] options)
{
    if (Console.IsInputRedirected)
        throw new BumpError("interactive selection needs a real terminal — pass project names as arguments instead");

    var selected = new bool[options.Length];
    int focus = 0;

    Console.WriteLine(title);
    Console.WriteLine("  up/down move · space toggle · a all · enter confirm (none selected = the focused one) · esc cancel");
    for (int i = 0; i < options.Length; i++) Console.WriteLine();   // reserve rows (absorbs any scroll)
    int top = Console.CursorTop - options.Length;

    void Draw()
    {
        Console.SetCursorPosition(0, top);
        for (int i = 0; i < options.Length; i++)
        {
            var pointer = i == focus ? ">" : " ";
            var box = selected[i] ? "[x]" : "[ ]";
            Console.WriteLine($" {pointer} {box} {options[i]}");
        }
    }

    Draw();
    while (true)
    {
        switch (Console.ReadKey(intercept: true).Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K:   focus = (focus - 1 + options.Length) % options.Length; Draw(); break;
            case ConsoleKey.DownArrow or ConsoleKey.J: focus = (focus + 1) % options.Length; Draw(); break;
            case ConsoleKey.Spacebar:                  selected[focus] = !selected[focus]; Draw(); break;
            case ConsoleKey.A:
            {
                var allOn = selected.All(b => b);     // 'a' selects all, or clears all if already all-on
                for (int i = 0; i < selected.Length; i++) selected[i] = !allOn;
                Draw();
                break;
            }
            case ConsoleKey.Enter:
                var chosen = Enumerable.Range(0, options.Length).Where(i => selected[i]).ToArray();
                return chosen.Length > 0 ? chosen : [focus];
            case ConsoleKey.Escape or ConsoleKey.Q:
                return null;
        }
    }
}

static Ver? TryCurrentVersion(string root, string project)
{
    var path = Path.Combine(root, "src", project, $"KY.AI.{project}.csproj");
    if (!File.Exists(path)) return null;
    var m = Regex.Match(File.ReadAllText(path), VersionRx());
    return m.Success ? Ver.Parse(m.Groups[1].Value) : null;
}

// Tag prefixes — must match scripts/tag.cs (Ng -> ng, Net -> dotnet, Serve -> serve).
static string TagPrefix(string project) => project switch
{
    "Serve" => "serve",
    "Ng" => "ng",
    "Net" => "dotnet",
    _ => project.ToLowerInvariant(),
};

// Best-effort suffix for the selector: commits since the project's highest release tag that
// touched its src\<Project> folder, and whether the csproj <Version> is already ahead of that tag
// (bumped but not yet tagged). Empty (silently) if git isn't available or the repo can't be read.
static string CommitSuffix(string root, string project, Ver? cur)
{
    try
    {
        // Highest-version release tag for this project (e.g. serve-v1.1.0).
        var lastTag = GitCapture(root, "tag", "-l", $"{TagPrefix(project)}-v*", "--sort=-v:refname")
            .Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        var range = lastTag is null ? "HEAD" : $"{lastTag}..HEAD";
        var (code, outp) = GitCapture(root, "rev-list", "--count", range, "--", $"src/{project}");
        if (code != 0 || !int.TryParse(outp.Trim(), out var n)) return "";

        var plural = n == 1 ? "" : "s";
        var commits = $"{n} commit{plural}";

        if (lastTag is null)
            return n == 0 ? " (no tag yet)" : $" ({commits}, no tag yet)";

        // csproj <Version> already higher than the latest tag -> bumped but not yet tagged/released.
        var tagVer = ParseTagVersion(lastTag);
        bool bumped = cur is not null && tagVer is not null && IsHigher(cur.Value, tagVer.Value);

        if (bumped) return n == 0 ? " (already bumped)" : $" (already bumped, {commits})";
        return n == 0 ? " (up to date)" : $" ({commits})";
    }
    catch { return ""; }
}

// Pull the version out of a "<prefix>-v<major.minor.patch>" tag name; null if it doesn't parse.
static Ver? ParseTagVersion(string tag)
{
    var i = tag.LastIndexOf("-v", StringComparison.Ordinal);
    if (i < 0) return null;
    try { return Ver.Parse(tag[(i + 2)..]); } catch { return null; }
}

static bool IsHigher(Ver a, Ver b) =>
    (a.Major, a.Minor, a.Patch).CompareTo((b.Major, b.Minor, b.Patch)) > 0;

static (int Code, string Out) GitCapture(string root, params string[] args)
{
    var psi = new ProcessStartInfo("git")
    {
        UseShellExecute = false,
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
    var stdout = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, stdout);
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

// ---- version maths ------------------------------------------------------

static Ver Compute(string project, Ver cur, string? part, int? setMajor)
{
    part = part?.ToLowerInvariant();

    if (project is "Serve" or "Browser" or "Terminal" or "Updater")
    {
        if (setMajor is not null)
            throw new BumpError("--set-major is for the framework-pinned tools (Ng, Net); Serve/Browser/Terminal/Updater use --part major|minor|patch");
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

static bool WriteOrPreview(string path, string before, string after, string root, bool dryRun, string label)
{
    var rel = Path.GetRelativePath(root, path);
    if (before == after)
    {
        Console.WriteLine($"  {rel,-28} unchanged");
        return false;
    }
    if (dryRun)
    {
        Console.WriteLine($"  {rel,-28} would change ({label.Trim()})");
        return false;
    }
    File.WriteAllText(path, after);
    Console.WriteLine($"  {rel,-28} updated");
    return true;
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
          dotnet run scripts/bump.cs                       (no args -> interactive: select project(s) w/ arrows+space, bump each)
          dotnet run scripts/bump.cs -- <project> <bump> [--dry-run]

        Serve / Browser / Terminal / Updater (plain SemVer):
          -- Serve    --part major|minor|patch
          -- Browser  --part major|minor|patch
          -- Terminal --part major|minor|patch
          -- Updater  --part major|minor|patch

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
