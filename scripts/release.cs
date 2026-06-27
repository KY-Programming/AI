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
// Before publishing, all releases' notes open together in an inline editor — Up/Down move across every
// line (and from one release into the next), type/Backspace edit, Enter adds a line, Entf drops a line,
// Ctrl+Enter commits, Esc cancels. Requires the GitHub CLI
// (https://cli.github.com), authenticated via `gh auth login`.
//
// Mapping (project -> tag):  KY.AI.Ng -> ng-v<version> · KY.AI.Net -> dotnet-v<version> ·
//                            KY.AI.Browser -> browser-v<version> · KY.AI.Terminal -> terminal-v<version> ·
//                            KY.AI.Serve -> serve-v<version>   (must match scripts/tag.cs)

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

// Let the user review and tweak all releases' notes together before publishing.
Console.WriteLine();
Console.WriteLine($"About to create {toCreate.Count} release(s){(draft ? " (draft)" : "")} — review/edit the notes, then commit.");
if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
{
    var edited = EditAllReleases(toCreate);   // null if the user cancelled (Esc); Ctrl+Enter commits
    if (edited is null)
    {
        Console.WriteLine("Cancelled — no releases created.");
        return 0;
    }
    toCreate = edited;
}
else   // no console to drive the editor (redirected) — show what will be released, then confirm
{
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
// KY.AI.Browser -> "Browser", KY.AI.Terminal -> "Terminal", KY.AI.Serve -> "Serve".
static string DisplayName(string prefix) => prefix switch
{
    "ng" => "Angular",
    "dotnet" => ".NET",
    "browser" => "Browser",
    "terminal" => "Terminal",
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

// Interactive editor for every release's notes at once, shown just before publishing. Controls:
//   Up/Down            move line by line, crossing from one release into the next at its edges
//   Left/Right, Home/End  move the cursor within the selected line
//   type a character   insert at the cursor   Backspace   delete the char before it
//   Enter              add a new line after the selected one   Delete (Entf)   drop the selected line
//   Ctrl+Enter         commit all releases    Esc   cancel (create nothing)
// The selected line is highlighted and carries the cursor. Blank lines are dropped on commit (so the
// "no blank lines" rule holds); a release left empty falls back to "Release <tag>.". Returns the edited
// releases, or null if the user cancelled.
static List<(string Tag, string Title, string Notes, string Prefix)>? EditAllReleases(
    List<(string Tag, string Title, string Notes, string Prefix)> toCreate)
{
    var releases = toCreate
        .Select(t => (t.Tag, t.Title, t.Prefix, Lines: t.Notes.Split('\n').Select(l => l.TrimEnd()).ToList()))
        .ToList();
    foreach (var r in releases) if (r.Lines.Count == 0) r.Lines.Add("");   // keep each release selectable

    int selRel = 0, selLine = 0, col = releases[0].Lines[0].Length;
    int top = -1, maxRows = 0;

    // Write one full-width row (truncate or pad) so each frame fully overwrites the previous one.
    static void Row(string s, int width)
    {
        s = s.Length > width ? s[..width] : s.PadRight(width);
        Console.Write(s);
        Console.Write('\n');
    }

    // Repaint every release (headers + lines, the selected line highlighted), then the hint + commit line.
    void Render()
    {
        int width = Math.Max(20, Console.WindowWidth - 1);
        if (top < 0) top = Console.CursorTop;
        Console.CursorVisible = false;
        Console.ResetColor();
        Console.SetCursorPosition(0, top);

        int printed = 0, selRow = top;
        for (int r = 0; r < releases.Count; r++)
        {
            if (r > 0) { Row("", width); printed++; }
            Row($"  ── {releases[r].Title}  ({releases[r].Tag}) ──", width); printed++;
            for (int i = 0; i < releases[r].Lines.Count; i++)
            {
                bool selected = r == selRel && i == selLine;
                if (selected) { selRow = top + printed; Console.BackgroundColor = ConsoleColor.DarkCyan; Console.ForegroundColor = ConsoleColor.Black; }
                Row("    " + releases[r].Lines[i], width);
                if (selected) Console.ResetColor();
                printed++;
            }
        }
        Row("", width); printed++;
        Row("  [Up/Down] line   [Left/Right] move   type/Backspace edit   [Enter] new line   [Entf] delete line", width); printed++;
        Row("  press Ctrl+Enter to commit the releases   (Esc to cancel)", width); printed++;

        for (int i = printed; i < maxRows; i++) Row("", width);   // wipe leftovers from a taller frame
        maxRows = Math.Max(maxRows, printed);

        Console.SetCursorPosition(Math.Min(4 + col, width), Math.Min(selRow, Console.BufferHeight - 1));
        Console.CursorVisible = true;
    }

    // On commit/cancel: repaint a clean copy (no highlight, no hint, blank lines removed) and park below.
    void FinalRender()
    {
        int width = Math.Max(20, Console.WindowWidth - 1);
        Console.CursorVisible = false;
        Console.ResetColor();
        Console.SetCursorPosition(0, top);
        int printed = 0;
        for (int r = 0; r < releases.Count; r++)
        {
            if (r > 0) { Row("", width); printed++; }
            Row($"  ── {releases[r].Title}  ({releases[r].Tag}) ──", width); printed++;
            foreach (var l in releases[r].Lines.Select(x => x.Trim()).Where(x => x.Length > 0)) { Row("    " + l, width); printed++; }
        }
        for (int i = printed; i < maxRows; i++) Row("", width);
        Console.SetCursorPosition(0, Math.Min(top + printed, Console.BufferHeight - 1));
        Console.CursorVisible = true;
    }

    // Step the selection down/up one line, spilling into the neighbouring release at a release's edge.
    bool MoveDown()
    {
        if (selLine < releases[selRel].Lines.Count - 1) { selLine++; return true; }
        for (int r = selRel + 1; r < releases.Count; r++)
            if (releases[r].Lines.Count > 0) { selRel = r; selLine = 0; return true; }
        return false;
    }
    bool MoveUp()
    {
        if (selLine > 0) { selLine--; return true; }
        for (int r = selRel - 1; r >= 0; r--)
            if (releases[r].Lines.Count > 0) { selRel = r; selLine = releases[r].Lines.Count - 1; return true; }
        return false;
    }

    while (true)
    {
        Render();
        var key = Console.ReadKey(intercept: true);
        var lines = releases[selRel].Lines;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:    if (MoveUp())   col = releases[selRel].Lines[selLine].Length; break;
            case ConsoleKey.DownArrow:  if (MoveDown()) col = releases[selRel].Lines[selLine].Length; break;
            case ConsoleKey.LeftArrow:  if (col > 0) col--; break;
            case ConsoleKey.RightArrow: if (col < lines[selLine].Length) col++; break;
            case ConsoleKey.Home:       col = 0; break;
            case ConsoleKey.End:        col = lines[selLine].Length; break;

            case ConsoleKey.Delete:                        // Entf: drop the selected line.
                lines.RemoveAt(selLine);
                if (lines.Count == 0) lines.Add("");
                if (selLine >= lines.Count) selLine = lines.Count - 1;
                col = lines[selLine].Length;
                break;

            case ConsoleKey.Backspace:
                if (col > 0) { lines[selLine] = lines[selLine].Remove(col - 1, 1); col--; }
                else if (selLine > 0)                      // at column 0: merge into the previous line
                {
                    col = lines[selLine - 1].Length;
                    lines[selLine - 1] += lines[selLine];
                    lines.RemoveAt(selLine);
                    selLine--;
                }
                break;

            case ConsoleKey.Enter:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)   // Ctrl+Enter: commit every release
                {
                    FinalRender();
                    return releases.Select(r =>
                    {
                        var kept = r.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                        return (r.Tag, r.Title, kept.Count > 0 ? string.Join("\n", kept) : $"Release {r.Tag}.", r.Prefix);
                    }).ToList();
                }
                lines.Insert(selLine + 1, "");             // Enter: add a new line after the selected one
                selLine++; col = 0;
                break;

            case ConsoleKey.Escape:
                FinalRender();
                return null;                               // cancelled — create nothing

            default:
                if (!char.IsControl(key.KeyChar)) { lines[selLine] = lines[selLine].Insert(col, key.KeyChar.ToString()); col++; }
                break;
        }
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
          KY.AI.Serve    -> serve-v<version>

        Title: "<Framework> v<version>" (.NET / Angular / Browser / Terminal / Serve); only Angular is marked "Latest".
        Notes: every feat:/fix: line since the project's previous tag (src\<Project> only),
        checked per line, one per line, no blank lines or dashes/hashes. Before publishing, all
        releases open together in an inline editor (Up/Down move across every line and between
        releases, type/Backspace edit, Enter add a line, Entf drop a line, Ctrl+Enter commit, Esc cancel).
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
