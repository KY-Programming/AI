namespace KY.AI.Wpf;

// How every tool names an element: the same selector for `find` and for each action, so the agent
// reads with one shape and acts with the same one.
//
// Fields AND together — `automationId:"ToolRow", name:"Rider"` is "the ToolRow called Rider", which
// is exactly how a WPF list that carries AutomationProperties.Name per item and an AutomationId per
// item *kind* is addressed (see KY.ProjectHub). `Index` disambiguates the remaining matches in tree
// order; `Path` short-circuits everything and takes the node at that position verbatim.
//
// Matching is pure over NodeInfo so it can be tested without a window, and so `find` and the actions
// can never disagree about what a selector means.
public sealed record ElementSelector(
    string? Path = null,
    string? AutomationId = null,
    string? Name = null,
    string? ControlType = null,
    string? Contains = null,
    int? Index = null)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Path) && string.IsNullOrWhiteSpace(AutomationId) &&
        string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(ControlType) &&
        string.IsNullOrWhiteSpace(Contains);

    public bool Matches(NodeInfo node)
    {
        if (!string.IsNullOrWhiteSpace(Path))
            return string.Equals(node.Path, Path!.Trim(), StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(AutomationId) && !Eq(node.AutomationId, AutomationId)) return false;
        if (!string.IsNullOrWhiteSpace(Name) && !Eq(node.Name, Name)) return false;
        // Control types are matched by their UIA name minus the "ControlType." prefix, so both
        // "Button" and "button" work and the agent can paste back what `tree` printed.
        if (!string.IsNullOrWhiteSpace(ControlType) && !Eq(node.Type, ControlType)) return false;
        if (!string.IsNullOrWhiteSpace(Contains) && !ContainsAny(node, Contains!)) return false;
        return true;
    }

    // `contains` is the escape hatch for a row whose Name carries more than the agent knows (a path,
    // a version, a status suffix) — so it spans every human-readable field of the node at once.
    private static bool ContainsAny(NodeInfo node, string needle) =>
        Has(node.Name, needle) || Has(node.AutomationId, needle) || Has(node.Value, needle);

    private static bool Eq(string? actual, string? expected) =>
        string.Equals(actual ?? string.Empty, (expected ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Has(string? actual, string needle) =>
        actual is not null && actual.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);

    // Apply the selector to an already-walked tree. Returns the chosen node plus how many matched,
    // so a caller can tell "no such element" from "you meant one of five" and say which.
    public (NodeInfo? Node, int Matches) Pick(IReadOnlyList<NodeInfo> nodes)
    {
        var hits = nodes.Where(Matches).ToList();
        if (hits.Count == 0) return (null, 0);
        var i = Index is null or < 0 ? 0 : Index.Value;
        return (i < hits.Count ? hits[i] : null, hits.Count);
    }
}
