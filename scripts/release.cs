// release.cs — create a GitHub release per project from its <Version> tag (via the gh CLI).
//
//   scripts\release.cmd                 publish a GitHub release per project
//   scripts\release.cmd --dry-run       show what would be released; create nothing
//   scripts\release.cmd --draft         create as drafts (review/publish them on GitHub)
//
// Ties into the bump/tag workflow: each project's release uses its <prefix>-v<version> tag, which
// must already exist on origin (run scripts\tag.cmd first). Titles are "<Framework> v<version>"
// (.NET / Angular / Browser / Terminal / Serve), and only the Angular release is marked GitHub's "Latest". Release notes
// are every feat:/fix: line since the project's previous tag (src\<Project> only), checked per line
// so mixed commits keep just their feat:/fix: lines, one per line with no blanks or dashes/hashes.
// Before publishing, all releases' notes open together in one file in your editor ($VISUAL / $EDITOR,
// default notepad) — one "=== <tag> ===" section per release; save and close to continue, then confirm.
// Deleting a release's whole section (header included) skips that release. Requires the GitHub CLI
// (https://cli.github.com), authenticated via `gh auth login`.
//
// Mapping (project -> tag):  KY.AI.Ng -> ng-v<version> · KY.AI.Net -> dotnet-v<version> ·
//                            KY.AI.Browser -> browser-v<version> · KY.AI.Terminal -> terminal-v<version> ·
//                            KY.AI.Updater -> updater-v<version> · KY.AI.Serve -> serve-v<version>   (must match scripts/tag.cs)

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var projects = new (string Csproj, string Prefix)[]
{
    ("src/Ng/KY.AI.Ng.csproj",             "ng"),
    ("src/Net/KY.AI.Net.csproj",           "dotnet"),
    ("src/Browser/KY.AI.Browser.csproj",   "browser"),
    ("src/Terminal/KY.AI.Terminal.csproj", "terminal"),
    ("src/Wpf/KY.AI.Wpf.csproj", "wpf"),
    ("src/Updater/KY.AI.Updater.csproj",   "updater"),
    ("src/Serve/KY.AI.Serve.csproj",       "serve"),
};

bool dryRun = false, draft = false;
foreach (var a in args)
{
    switch (a.ToLowerInvariant())
    {
        case "--dry-run" or "-n": dryRun = true; break;
        case "--draft" or "-d": draft = true; break;
        case "--help" or "-h": PrintUsage(); return 0;
        default: Console.Error.WriteLine($"unknown option '{a}'"); PrintUsage(); return 2;
    }
}

var root = RepoRoot();

if (Capture(root, "gh", "--version").Code != 0)
{
    Console.Error.WriteLine("GitHub CLI not found. Install it (https://cli.github.com), then `gh auth login`.");
    return 1;
}
if (Capture(root, "gh", "auth", "status").Code != 0)
{
    Console.Error.WriteLine("not logged in to the GitHub CLI — run `gh auth login`.");
    return 1;
}

// Build the plan: project -> version -> tag.
var plan = new List<(string Project, string Version, string Prefix, string Tag, string SrcDir)>();
foreach (var (csproj, prefix) in projects)
{
    var path = Path.Combine(root, csproj.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(path)) { Console.Error.WriteLine($"missing project: {csproj}"); return 1; }
    var version = ReadVersion(path);
    if (string.IsNullOrWhiteSpace(version)) { Console.Error.WriteLine($"no <Version> in {csproj}"); return 1; }
    var srcDir = csproj[..csproj.LastIndexOf('/')];   // the folder commits touch, e.g. "src/Ng"
    plan.Add((Path.GetFileNameWithoutExtension(path), version!, prefix, $"{prefix}-v{version}", srcDir));
}

// Resolve what to create (reporting skips), then preview each release's title + notes and confirm.
var toCreate = new List<(string Tag, string Title, string Notes, string Prefix)>();
foreach (var (project, version, prefix, tag, srcDir) in plan)
{
    // The tag must exist locally and on origin (gh --verify-tag checks the remote).
    if (Capture(root, "git", "rev-parse", "-q", "--verify", $"refs/tags/{tag}").Code != 0)
    {
        Console.WriteLine($"skip    {tag} — no such tag (run scripts\\tag.cmd first)");
        continue;
    }
    if (Capture(root, "gh", "release", "view", tag).Code == 0)
    {
        Console.WriteLine($"skip    {tag} — release already exists");
        continue;
    }
    toCreate.Add((tag, $"{DisplayName(prefix)} v{version}", ReleaseNotes(root, prefix, tag, srcDir), prefix));
}

if (toCreate.Count == 0)
{
    Console.WriteLine("\nNothing to release.");
    return 0;
}

// Dry run: preview the title + notes and change nothing (no interactive editing).
if (dryRun)
{
    Console.WriteLine();
    Console.WriteLine($"[dry run] would create {toCreate.Count} release(s):");
    foreach (var (tag, title, notes, _) in toCreate)
    {
        Console.WriteLine();
        Console.WriteLine($"  ── {title}  ({tag}){(draft ? "  (draft)" : "")} ──");
        foreach (var line in notes.Split('\n')) Console.WriteLine($"    {line}");
    }
    Console.WriteLine();
    Console.WriteLine("Dry run complete — no releases created.");
    return 0;
}

// Let the user review and tweak all releases' notes together in their editor (skipped when there's
// no console, e.g. redirected), then show the final notes and confirm.
Console.WriteLine();
Console.WriteLine($"About to create {toCreate.Count} release(s){(draft ? " (draft)" : "")}.");
if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
{
    var planned = toCreate.Count;
    toCreate = EditInEditor(toCreate);
    if (toCreate.Count == 0)
    {
        Console.WriteLine("Every release section was removed — no releases created.");
        return 0;
    }
    if (toCreate.Count < planned)
        Console.WriteLine($"{planned - toCreate.Count} release(s) removed in the editor — skipped.");
}

foreach (var (tag, title, notes, _) in toCreate)
{
    Console.WriteLine();
    Console.WriteLine($"  ── {title}  ({tag}){(draft ? "  (draft)" : "")} ──");
    foreach (var line in notes.Split('\n')) Console.WriteLine($"    {line}");
}
Console.WriteLine();
if (!ConfirmYes($"Create {toCreate.Count} release(s)?"))
{
    Console.WriteLine("Aborted — no releases created.");
    return 0;
}

int rc = 0;
foreach (var (tag, title, notes, prefix) in toCreate)
{
    // --verify-tag aborts if the tag isn't on the remote. Only the Ng release claims the repo's
    // "Latest" badge; the others are explicitly not-latest.
    var ghArgs = new List<string>
    {
        "release", "create", tag, "--title", title, "--notes", notes, "--verify-tag",
        $"--latest={(prefix == "ng" ? "true" : "false")}",
    };
    if (draft) ghArgs.Add("--draft");

    if (Run(root, "gh", ghArgs.ToArray()) != 0) { Console.Error.WriteLine($"failed to create release {tag}"); rc = 1; continue; }
    Console.WriteLine($"released {tag}");
}

Console.WriteLine();
Console.WriteLine(rc == 0 ? "Done." : "Done with errors.");
return rc;

// ---- helpers ----------------------------------------------------------------

static string? ReadVersion(string csprojPath)
    => XDocument.Load(csprojPath).Descendants()
        .FirstOrDefault(e => e.Name.LocalName == "Version")?.Value.Trim();

// Release title name per product: KY.AI.Ng -> "Angular", KY.AI.Net -> ".NET",
// KY.AI.Browser -> "Browser", KY.AI.Terminal -> "Terminal", KY.AI.Updater -> "Updater",
// KY.AI.Serve -> "Serve".
static string DisplayName(string prefix) => prefix switch
{
    "ng" => "Angular",
    "dotnet" => ".NET",
    "browser" => "Browser",
    "terminal" => "Terminal",
    "wpf" => "WPF",
    "updater" => "Updater",
    "serve" => "Serve",
    _ => prefix,
};

// Notes = every feat:/fix: line since the project's previous tag (same prefix), scoped to src\<path>.
// Each line of every commit message is checked on its own, so a fix: line survives even when its
// commit's subject (or another line) is a chore:. Blank lines and non-feat/fix lines are dropped;
// the result is one line per entry with no dashes or hashes.
static string ReleaseNotes(string root, string prefix, string tag, string path)
{
    var tags = Capture(root, "git", "tag", "-l", $"{prefix}-v*", "--sort=-v:refname").Out
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var prev = tags.SkipWhile(t => t != tag).Skip(1).FirstOrDefault();   // the version just below `tag`
    var range = prev is null ? tag : $"{prev}..{tag}";

    // %B is the full raw message (subject + body). We flatten every commit to its lines and keep
    // only those that start with feat:/fix: — checked per line, so mixed commits keep just the
    // relevant lines and nothing else (no blanks, no chore:/etc.).
    var raw = Capture(root, "git", "log", "--no-merges", "--pretty=format:%B", range, "--", path).Out;
    var lines = raw.Split('\n')
        .Select(l => l.Trim())
        .Where(l => Regex.IsMatch(l, @"^(feat|fix)(\([^)]*\))?:", RegexOptions.IgnoreCase))
        .ToList();

    return lines.Count > 0 ? string.Join("\n", lines) : $"Release {tag}.";
}

// Opens every release's notes together in one temp file in the user's editor ($VISUAL / $EDITOR,
// default notepad) and reads them back once it's saved and closed. Each release is a section headed
// "=== <tag> ===" (the tag is how sections map back to releases, so header lines are left as-is).
// Within a section blank lines are dropped (the "no blank lines" rule) and a section left empty falls
// back to "Release <tag>.". A release whose header was deleted is left out of the result (skipped).
// The editor command is split on spaces so a value like "code --wait" works.
static List<(string Tag, string Title, string Notes, string Prefix)> EditInEditor(
    List<(string Tag, string Title, string Notes, string Prefix)> toCreate)
{
    var file = Path.Combine(Path.GetTempPath(), $"ky-ai-release-notes-{DateTime.Now:yyyyMMddHHmmss}.txt");
    var nl = Environment.NewLine;
    File.WriteAllText(file, string.Join(nl + nl, toCreate.Select(t =>
        $"=== {t.Tag} ==={nl}{t.Notes.Replace("\n", nl)}")) + nl);

    var editorCommand = Environment.GetEnvironmentVariable("VISUAL")
                        ?? Environment.GetEnvironmentVariable("EDITOR")
                        ?? "notepad";
    var parts = editorCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    Console.WriteLine($"Opening release notes in {editorCommand} — save and close to continue...");
    try
    {
        var psi = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
        foreach (var a in parts.Skip(1)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(file);
        using (var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {parts[0]}"))
            p.WaitForExit();

        // Collect each section's lines by its header tag.
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            var header = Regex.Match(line, @"^===\s*(\S+)\s*===$");
            if (header.Success) { current = sections[header.Groups[1].Value] = []; continue; }
            if (current is not null && line.Length > 0) current.Add(line);
        }

        return toCreate
            .Where(t => sections.ContainsKey(t.Tag))
            .Select(t =>
            {
                var kept = sections[t.Tag];
                return (t.Tag, t.Title, kept.Count > 0 ? string.Join("\n", kept) : $"Release {t.Tag}.", t.Prefix);
            })
            .ToList();
    }
    finally
    {
        File.Delete(file);
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
    try
    {
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return (-1, "");   // exe not found (e.g. gh not installed)
    }
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

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          dotnet run scripts/release.cs -- [options]      (or: scripts\release.cmd [options])

        Creates a GitHub release per project from its <Version> tag (via the gh CLI):
          KY.AI.Ng       -> ng-v<version>
          KY.AI.Net      -> dotnet-v<version>
          KY.AI.Browser  -> browser-v<version>
          KY.AI.Terminal -> terminal-v<version>
          KY.AI.Wpf      -> wpf-v<version>
          KY.AI.Updater  -> updater-v<version>
          KY.AI.Serve    -> serve-v<version>

        Title: "<Framework> v<version>" (.NET / Angular / Browser / Terminal / Serve); only Angular is marked "Latest".
        Notes: every feat:/fix: line since the project's previous tag (src\<Project> only),
        checked per line, one per line, no blank lines or dashes/hashes. Before publishing, all
        releases open together in one file in your editor ($VISUAL / $EDITOR, default notepad), one
        "=== <tag> ===" section each; save and close, then confirm. Remove a section to skip that release.
        The tag must already exist on origin — run scripts\tag.cmd first. Requires the gh CLI, authed.

        Options:
          --dry-run, -n   show what would be released; create nothing
          --draft, -d     create the releases as drafts (review/publish on GitHub)
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
