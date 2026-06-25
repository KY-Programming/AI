using System.Text.Json;

namespace KY.AI.Ng;

// Resolve the index.html that ky-ai-browser's reversible inject targets. Prefers angular.json's build
// `index` option — a string, or an `{ "input": … }` object; under either `architect` or `targets`;
// trying `defaultProject` first — and falls back to `src/index.html`. Returns null if none exists.
internal static class NgIndexResolver
{
    public static string? Resolve(string workingDir)
    {
        try
        {
            var angularJson = Path.Combine(workingDir, "angular.json");
            if (File.Exists(angularJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(angularJson));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
                {
                    var def = root.TryGetProperty("defaultProject", out var dp) && dp.ValueKind == JsonValueKind.String
                        ? dp.GetString() : null;
                    foreach (var name in OrderProjects(projects, def))
                    {
                        if (projects.TryGetProperty(name, out var project) && IndexPath(project) is { } rel)
                        {
                            var full = Path.GetFullPath(Path.Combine(workingDir, rel));
                            if (File.Exists(full)) return full;
                        }
                    }
                }
            }
        }
        catch { /* malformed angular.json — fall back to the default */ }

        var fallback = Path.Combine(workingDir, "src", "index.html");
        return File.Exists(fallback) ? fallback : null;
    }

    // defaultProject first (if present), then the rest in document order.
    private static IEnumerable<string> OrderProjects(JsonElement projects, string? defaultProject)
    {
        if (defaultProject is not null && projects.TryGetProperty(defaultProject, out _)) yield return defaultProject;
        foreach (var p in projects.EnumerateObject())
            if (!string.Equals(p.Name, defaultProject, StringComparison.Ordinal)) yield return p.Name;
    }

    // architect|targets → build → options → index (a string, or an { input } object).
    private static string? IndexPath(JsonElement project)
    {
        foreach (var key in new[] { "architect", "targets" })
        {
            if (project.TryGetProperty(key, out var targets) && targets.ValueKind == JsonValueKind.Object &&
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
}
