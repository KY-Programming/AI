using System.Text.Json;

namespace KY.AI.Ng;

// Resolve the index.html that ky-ai-browser's inject targets for an nx project. Unlike a plain
// Angular workspace (one angular.json at the root → NgIndexResolver), an nx monorepo keeps each
// app under its own folder (e.g. apps/dashboard) with the index declared in that project's config,
// so resolution needs the project name (parsed from the nx target, e.g. `dashboard:serve:dev`).
//
// Tries, in order: a root angular.json entry for the project (inline config, or the nx string form
// that points at the project dir); the project's own project.json (modern nx) found by name; then
// conventional locations (apps/<p>/src/index.html, <p>/src/index.html, libs/<p>/…). Index paths in
// nx config are relative to the workspace root. Returns null if nothing exists.
internal static class NxIndexResolver
{
    private static readonly string[] ExcludeDirs = { "node_modules", "dist", ".nx", ".angular", ".git", "tmp" };

    public static string? Resolve(string workspaceRoot, string? project)
    {
        try
        {
            if (project is not null)
            {
                // 1) angular.json: projects[project] is either an inline config object, or — the nx
                //    convention — a string path to the project's folder.
                var angularJson = Path.Combine(workspaceRoot, "angular.json");
                if (File.Exists(angularJson))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(angularJson));
                    if (doc.RootElement.TryGetProperty("projects", out var projects) &&
                        projects.ValueKind == JsonValueKind.Object &&
                        projects.TryGetProperty(project, out var entry))
                    {
                        if (entry.ValueKind == JsonValueKind.Object && IndexFromConfig(entry) is { } relInline &&
                            Existing(workspaceRoot, relInline) is { } hitInline) return hitInline;
                        if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } projDir &&
                            FromProjectDir(workspaceRoot, projDir) is { } hitDir) return hitDir;
                    }
                }

                // 2) The project's project.json — conventional dirs first (cheap), then a bounded walk.
                foreach (var dir in new[] { Path.Combine("apps", project), project, Path.Combine("libs", project) })
                    if (FromProjectDir(workspaceRoot, dir) is { } hit) return hit;

                var found = FindProjectJson(workspaceRoot, project);
                if (found is not null && FromProjectJson(workspaceRoot, found) is { } hitFound) return hitFound;
            }
        }
        catch { /* malformed json — fall through to the conventional src/index.html locations */ }

        // 3) Conventional index.html locations, then the workspace-root fallback (matches NgIndexResolver).
        if (project is not null)
            foreach (var c in new[] { Path.Combine("apps", project, "src"), Path.Combine(project, "src"), Path.Combine("libs", project, "src") })
                if (Existing(workspaceRoot, Path.Combine(c, "index.html")) is { } hit) return hit;
        return Existing(workspaceRoot, Path.Combine("src", "index.html"));
    }

    // Read <dir>/project.json for an index, then fall back to <dir>/src/index.html.
    private static string? FromProjectDir(string workspaceRoot, string relDir)
    {
        var dir = Path.GetFullPath(Path.Combine(workspaceRoot, relDir));
        var pj = Path.Combine(dir, "project.json");
        if (File.Exists(pj) && FromProjectJson(workspaceRoot, pj) is { } hit) return hit;
        return Existing(dir, Path.Combine("src", "index.html"));
    }

    // An index declared in a project.json (index path is relative to the workspace root).
    private static string? FromProjectJson(string workspaceRoot, string projectJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(projectJsonPath));
            if (IndexFromConfig(doc.RootElement) is { } rel) return Existing(workspaceRoot, rel);
        }
        catch { /* malformed project.json */ }
        return null;
    }

    // Locate a project's project.json by its `name` (or folder name), via a bounded walk that skips
    // node_modules and other build/output dirs. Handles layouts that aren't apps/<name> or libs/<name>.
    private static string? FindProjectJson(string root, string project, int depth = 0)
    {
        if (depth > 6) return null;
        string[] entries;
        try { entries = Directory.GetFiles(root, "project.json"); }
        catch { return null; }
        foreach (var pj in entries)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                var name = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : null;
                if (string.Equals(name ?? new DirectoryInfo(root).Name, project, StringComparison.Ordinal)) return pj;
            }
            catch { /* skip malformed project.json */ }
        }
        foreach (var sub in SafeSubdirs(root))
            if (FindProjectJson(sub, project, depth + 1) is { } hit) return hit;
        return null;
    }

    private static IEnumerable<string> SafeSubdirs(string dir)
    {
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { yield break; }
        foreach (var s in subs)
        {
            var name = Path.GetFileName(s);
            if (name.StartsWith('.') || Array.Exists(ExcludeDirs, e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return s;
        }
    }

    // architect|targets → build → options → index (a string, or an { input } object). Same shape in
    // angular.json inline configs and in project.json (which uses `targets`).
    private static string? IndexFromConfig(JsonElement config)
    {
        foreach (var key in new[] { "architect", "targets" })
        {
            if (config.TryGetProperty(key, out var targets) && targets.ValueKind == JsonValueKind.Object &&
                targets.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object &&
                build.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object &&
                options.TryGetProperty("index", out var index))
            {
                if (index.ValueKind == JsonValueKind.String) return index.GetString();
                if (index.ValueKind == JsonValueKind.Object &&
                    index.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.String)
                    return input.GetString();
            }
        }
        return null;
    }

    // Combine + existence check; returns the full path only if the file exists.
    private static string? Existing(string baseDir, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(baseDir, rel));
        return File.Exists(full) ? full : null;
    }
}
