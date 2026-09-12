using KY.AI.Wpf;
using Xunit;

namespace KY.AI.Wpf.Tests;

// A window UI Automation will not let us read does not fail — it answers with a lone root that has
// no children, and `tree` reports "count: 1". Without the hint an agent concludes the app has no UI,
// which is worse than an error: it is a confident wrong answer it will act on. So the one condition
// that produces the hint, and the ones that must not, are pinned here.
public class BareRootHintTests
{
    private static NodeInfo Node(string path, int depth, int children) =>
        new(path, depth, "Window", null, "Some Window", null, true, false, null, null, children);

    private static AutomationReader.WalkResult Walk(params NodeInfo[] nodes) =>
        new(nodes.Select(n => new AutomationReader.Walked(n, null!)).ToList(), false);

    [Fact]
    public void A_root_with_no_children_is_called_out()
    {
        var hint = Engine.BareRootHint(Walk(Node("", 0, 0)), depth: 2);
        Assert.NotNull(hint);
        Assert.Contains("integrity level", hint);
    }

    // depth 0 means the caller asked for the root alone. Getting exactly the root back is then the
    // answer to the question, not a symptom — hinting there would cry wolf on every such call.
    [Fact]
    public void Asking_only_for_the_root_is_not_suspicious()
    {
        Assert.Null(Engine.BareRootHint(Walk(Node("", 0, 0)), depth: 0));
    }

    [Fact]
    public void A_window_that_returned_real_children_gets_no_hint()
    {
        Assert.Null(Engine.BareRootHint(Walk(Node("", 0, 3), Node("0", 1, 0)), depth: 2));
    }

    // The root says it HAS children but the walk returned none — a filtered-away subtree
    // (includeOffscreen) looks like this, and it is not an unreadable window.
    [Fact]
    public void A_root_that_reports_children_is_not_the_unreadable_case()
    {
        Assert.Null(Engine.BareRootHint(Walk(Node("", 0, 6)), depth: 2));
    }

    // A subtree walk starting below the root: the single node is not at depth 0, so it is an ordinary
    // leaf the caller navigated to, not a window that refused to open up.
    [Fact]
    public void A_lone_node_below_the_root_is_not_the_unreadable_case()
    {
        Assert.Null(Engine.BareRootHint(Walk(Node("3.1", 2, 0)), depth: 2));
    }

    // A scoped walk that comes back empty is an ordinary answer about one panel, not the window-wide
    // symptom this hint describes — saying "this window exposed no elements" there would be wrong.
    [Fact]
    public void A_scoped_walk_gets_no_hint()
    {
        Assert.Null(Engine.BareRootHint(Walk(Node("14.16", 0, 0)), depth: 2, scopePath: "14.16"));
    }

    [Fact]
    public void An_empty_walk_gets_no_hint()
    {
        Assert.Null(Engine.BareRootHint(Walk(), depth: 2));
    }
}
