namespace KY.AI.Wpf;

// Resolve the agent's `window` string to exactly one window — the by-name targeting every KY.AI
// tool uses, except that here the names are not a registry the user opted into but whatever the
// window manager happens to be showing. So this is deliberately strict about ambiguity: it prefers
// the most specific interpretation that matches, and when a tier still matches more than one window
// it refuses and lists the candidates rather than guessing at the user's expense.
//
// Pure over a supplied window list, so the tiers are unit-testable without a desktop.
internal static class WindowMatcher
{
    public sealed record Result(WindowInfo? Window, string? Error, IReadOnlyList<WindowInfo> Candidates);

    // Tiers, most specific first. Within a tier every match counts, so "two windows called
    // ProjectHub" is an ambiguity to report, not a coin flip.
    public static Result Resolve(IReadOnlyList<WindowInfo> windows, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            // The suite's "omit the name when there is only one" shorthand. On a desktop that is rare
            // — but when it holds it is unambiguous, so honour it.
            if (windows.Count == 1) return new Result(windows[0], null, windows);
            return new Result(null, windows.Count == 0
                ? "no windows visible"
                : "window not specified and more than one is visible", windows);
        }

        var q = target.Trim();

        // `#<handle>` and `pid:<n>` — exact machine identifiers, never ambiguous.
        if (q.StartsWith('#') && long.TryParse(q[1..], out var handle))
            return Single(windows.Where(w => w.Handle == handle).ToList(), windows, q);
        if (q.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) && int.TryParse(q[4..], out var pid))
            return Single(windows.Where(w => w.Pid == pid).ToList(), windows, q);

        var name = q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? q[..^4] : q;

        var tiers = new List<WindowInfo>[]
        {
            windows.Where(w => Eq(w.Process, name)).ToList(),
            windows.Where(w => Eq(w.Title, q)).ToList(),
            windows.Where(w => Has(w.Process, name)).ToList(),
            windows.Where(w => Has(w.Title, q)).ToList(),
        };
        foreach (var tier in tiers)
            if (tier.Count > 0)
                return Single(tier, windows, q);

        return new Result(null, $"no window matches '{q}'", windows);
    }

    private static Result Single(List<WindowInfo> hits, IReadOnlyList<WindowInfo> all, string q)
    {
        if (hits.Count == 0) return new Result(null, $"no window matches '{q}'", all);
        if (hits.Count == 1) return new Result(hits[0], null, all);

        // An app plus its own open dialog is not a real ambiguity: one owns the other, and naming the
        // app means the app's own window. Without this, listing dialogs would break every
        // `window: "MyApp"` call for as long as any dialog happens to be up — which is precisely when
        // an agent is mid-flow and least able to afford it. The dialog stays addressable by its own
        // title, which is how it is told apart in the first place.
        //
        // Deliberately narrow: it only resolves owner-vs-owned. Two unowned windows of one process
        // (two Chrome windows, two documents) are still a genuine ambiguity and still refused.
        var unowned = hits.Where(w => w.OwnerHandle is null).ToList();
        if (unowned.Count == 1 && hits.All(w => w.OwnerHandle is null || w.OwnerHandle == unowned[0].Handle))
            return new Result(unowned[0], null, all);

        return new Result(null, $"'{q}' matches {hits.Count} windows — target one by title, or by #<handle> from list", hits);
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Has(string a, string b) =>
        b.Length > 0 && a.Contains(b, StringComparison.OrdinalIgnoreCase);
}
